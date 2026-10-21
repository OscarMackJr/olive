using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Olive.Tests;

// Test-only subset checker. Phase 2 (API-05) replaces it with real draft-2020-12 validation.
// Supports: oneOf (exactly one), required, properties, type string/boolean/object, const, enum, pattern,
// format date-time, additionalProperties:false.
internal static class JsonSchemaLite
{
    public static List<string> Validate(JsonElement schema, JsonElement instance)
    {
        var errors = new List<string>();
        Check(schema, instance, "$", errors);
        return errors;
    }

    private static void Check(JsonElement schema, JsonElement inst, string path, List<string> errors)
    {
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = oneOf.EnumerateArray().Count(branch =>
            {
                var sub = new List<string>();
                Check(branch, inst, path, sub);
                return sub.Count == 0;
            });
            if (matches != 1)
            {
                errors.Add($"{path}: oneOf matched {matches} branches");
            }

            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var ok = type.GetString() switch
            {
                "string" => inst.ValueKind == JsonValueKind.String,
                "boolean" => inst.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "object" => inst.ValueKind == JsonValueKind.Object,
                _ => false,
            };
            if (!ok)
            {
                errors.Add($"{path}: wrong type");
                return;
            }
        }

        if (schema.TryGetProperty("const", out var c) && !JsonEquals(c, inst))
        {
            errors.Add($"{path}: const mismatch");
        }

        if (schema.TryGetProperty("enum", out var en) && !en.EnumerateArray().Any(e => JsonEquals(e, inst)))
        {
            errors.Add($"{path}: not in enum");
        }

        if (inst.ValueKind == JsonValueKind.String)
        {
            var s = inst.GetString()!;
            if (schema.TryGetProperty("pattern", out var pat) && !Regex.IsMatch(s, pat.GetString()!))
            {
                errors.Add($"{path}: pattern mismatch");
            }

            if (schema.TryGetProperty("format", out var fmt) && fmt.GetString() == "date-time"
                && !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                errors.Add($"{path}: bad date-time");
            }
        }

        if (inst.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("required", out var req))
        {
            foreach (var r in req.EnumerateArray().Select(x => x.GetString()!).Where(n => !inst.TryGetProperty(n, out _)))
            {
                errors.Add($"{path}: missing {r}");
            }
        }

        var props = schema.TryGetProperty("properties", out var p) ? p : default;
        var closed = schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False;
        foreach (var member in inst.EnumerateObject())
        {
            if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(member.Name, out var sub))
            {
                Check(sub, member.Value, path + "." + member.Name, errors);
            }
            else if (closed)
            {
                errors.Add($"{path}: unexpected property {member.Name}");
            }
        }
    }

    private static bool JsonEquals(JsonElement a, JsonElement b) => a.GetRawText() == b.GetRawText();
}
