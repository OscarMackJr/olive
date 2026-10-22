using System.Text.Json;

namespace Olive.Catalog;

// D-02: closure is enforced here across the whole parameters subtree (the action-type schema does not look inside it).
public static class CatalogVetting
{
    private const string Invalid = "Catalog parameter schema failed vetting.";
    private const int MaxDepth = 64;

    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal) { "uuid", "date-time", "date", "uri" };
    private static readonly string[] MapKeywords = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"];
    private static readonly string[] SchemaKeywords = ["items", "contains", "not", "if", "then", "else", "additionalProperties", "unevaluatedProperties", "unevaluatedItems", "propertyNames"];
    private static readonly string[] ArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems"];

    public static void Check(JsonElement parametersSchema)
    {
        if (parametersSchema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(Invalid);
        }

        Vet(parametersSchema, 0);
    }

    private static void Vet(JsonElement schema, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(Invalid);
        }

        // Allowlist (CR-01): `false` admits nothing; `true` (and any untyped node) admits arbitrary values, so it is refused.
        if (schema.ValueKind == JsonValueKind.False)
        {
            return;
        }

        if (schema.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(Invalid);
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(Invalid);
        }

        foreach (var p in schema.EnumerateObject())
        {
            switch (p.Name)
            {
                case "default":
                    throw new InvalidOperationException(Invalid);
                case "$ref" or "$dynamicRef":
                    if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { } r || !r.StartsWith('#'))
                    {
                        throw new InvalidOperationException(Invalid);
                    }

                    break;
                case "format":
                    if (p.Value.ValueKind != JsonValueKind.String || !Formats.Contains(p.Value.GetString()!))
                    {
                        throw new InvalidOperationException(Invalid);
                    }

                    break;
            }
        }

        if (!IsConstrained(schema))
        {
            throw new InvalidOperationException(Invalid);
        }

        if (IncludesType(schema, "array") && !schema.TryGetProperty("items", out _))
        {
            throw new InvalidOperationException(Invalid);
        }

        if (IsObjectSchema(schema)
            && !(schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False))
        {
            throw new InvalidOperationException(Invalid);
        }

        foreach (var k in MapKeywords)
        {
            if (schema.TryGetProperty(k, out var map))
            {
                if (map.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(Invalid);
                }

                foreach (var sub in map.EnumerateObject())
                {
                    Vet(sub.Value, depth + 1);
                }
            }
        }

        foreach (var k in SchemaKeywords)
        {
            if (schema.TryGetProperty(k, out var sub) && sub.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
            {
                Vet(sub, depth + 1);
            }
        }

        foreach (var k in ArrayKeywords)
        {
            if (schema.TryGetProperty(k, out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(Invalid);
                }

                foreach (var sub in arr.EnumerateArray())
                {
                    Vet(sub, depth + 1);
                }
            }
        }
    }

    // A node must name what it admits: a type, an exact value set, a reference (vetted at its target) or a non-empty
    // combinator whose branches are each vetted under these same rules. `{}`, `{ not: ... }`, `{ required: [...] }` are refused.
    private static bool IsConstrained(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out _) || schema.TryGetProperty("const", out _) || schema.TryGetProperty("enum", out _)
            || schema.TryGetProperty("$ref", out _))
        {
            return true;
        }

        foreach (var k in (string[])["allOf", "anyOf", "oneOf"])
        {
            if (schema.TryGetProperty(k, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsObjectSchema(JsonElement schema) =>
        schema.TryGetProperty("properties", out _) || schema.TryGetProperty("patternProperties", out _) || IncludesType(schema, "object");

    private static bool IncludesType(JsonElement schema, string type)
    {
        if (!schema.TryGetProperty("type", out var t))
        {
            return false;
        }

        return t.ValueKind == JsonValueKind.String
            ? t.GetString() == type
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == type);
    }
}
