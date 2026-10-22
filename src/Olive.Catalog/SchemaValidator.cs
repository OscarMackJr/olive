using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Olive.Catalog;

// Offline JSON Schema 2020-12 validation with format assertion on. No SchemaRegistry.Fetch is registered (REQ-ACT-04).
// Compiled schemas are immutable; callers cache and share instances.
public sealed class SchemaValidator
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private const int MaxPointerLength = 256;

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private readonly JsonSchema _schema;

    private SchemaValidator(JsonSchema schema) => _schema = schema;

    public static SchemaValidator Compile(string schemaText)
    {
        var node = JsonNode.Parse(schemaText) ?? throw new ArgumentException("schema is null", nameof(schemaText));
        // Dialect is forced in an in-memory copy; the source file is never changed (A4).
        if (node is JsonObject root)
        {
            root["$schema"] = Dialect;
        }

        return new SchemaValidator(JsonSchema.FromText(node.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
    }

    public SchemaViolation? Validate(JsonElement instance)
    {
        var result = _schema.Evaluate(instance, Options);
        if (result.IsValid)
        {
            return null;
        }

        var first = result.Details.FirstOrDefault(d => d.Errors is { Count: > 0 });
        if (first is null)
        {
            return new SchemaViolation("", "schema");
        }

        var pointer = first.InstanceLocation.ToString();
        var key = first.Errors!.Keys.First();
        if (key.Length == 0)
        {
            key = LastSegment(first.EvaluationPath.ToString());
        }

        // WR-03: an unknown-key failure is reported at the object that refused it, never at the caller-chosen key name.
        if (key is "additionalProperties" or "unevaluatedProperties")
        {
            var i = pointer.LastIndexOf('/');
            pointer = i < 0 ? "" : pointer[..i];
        }

        return new SchemaViolation(CapPointer(pointer), key);
    }

    // Applied to the final (prefixed) pointer. Never cuts a surrogate pair or a `~0`/`~1` escape in half.
    public static string CapPointer(string pointer)
    {
        if (pointer.Length <= MaxPointerLength)
        {
            return pointer;
        }

        var cut = MaxPointerLength;
        while (cut > 0 && (char.IsHighSurrogate(pointer[cut - 1]) || pointer[cut - 1] == '~'))
        {
            cut--;
        }

        return pointer[..cut];
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }
}
