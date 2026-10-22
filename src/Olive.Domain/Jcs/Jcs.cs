using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Olive.Domain;

// RFC 8785 JSON Canonicalization Scheme. BCL only; callers apply the I-JSON gate first (plan 04).
public static class Jcs
{
    private static readonly string[] ProposalKeys = ["action_type", "party_ref", "parameters", "justification"];

    public static byte[] Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] CanonicalizeProposalFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A proposal body must be a JSON object.");
        }

        var fields = new List<(string Name, JsonElement Value)>();
        foreach (var key in ProposalKeys)
        {
            if (!root.TryGetProperty(key, out var value))
            {
                throw new ArgumentException("Proposal body is missing a hashed field.");
            }

            fields.Add((key, value));
        }

        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var sb = new StringBuilder("{");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            WriteString(fields[i].Name, sb);
            sb.Append(':');
            Write(fields[i].Value, sb);
        }

        sb.Append('}');
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    public static string ProposalHash(JsonElement root) => Sha256Hex(CanonicalizeProposalFields(root));

    // ES6 Number::toString layout over the shortest round-trip digits.
    public static string FormatNumber(double d)
    {
        if (!double.IsFinite(d))
        {
            throw new ArgumentException("Non-finite numbers are not valid JSON.");
        }

        if (d == 0)
        {
            return "0";
        }

        var (digits, n) = JsonNumbers.Decompose(Math.Abs(d).ToString("R", CultureInfo.InvariantCulture));
        var k = digits.Length;
        var sign = d < 0 ? "-" : "";
        string body;
        if (k <= n && n <= 21)
        {
            body = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            body = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            body = "0." + new string('0', -n) + digits;
        }
        else
        {
            var exp = n - 1;
            var expText = (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            body = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + expText;
        }

        return sign + body;
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var props = e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                sb.Append('{');
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteString(props[i].Name, sb);
                    sb.Append(':');
                    Write(props[i].Value, sb);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    Write(item, sb);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(e.GetDouble()));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new ArgumentException("Unsupported JSON value kind.");
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
