using System.Globalization;
using System.Text.Json;
using Olive.Domain;

namespace Olive.Host.Endpoints;

// Q1 pre-idempotency I-JSON gate (API-04; D-04, D-05, D-18). The body is parsed exactly once, from raw bytes,
// and the whole tree is walked so no later hash or validator sees input that two parsers could read differently.
// Failure reasons are never reported: no exception text or submitted value leaves this class.
public static class JsonGate
{
    public const int MaxDepth = 64;
    public const int MaxRunIdLength = 256;

    // jsonb numeric holds roughly 131072 digits before the point and 16383 after; text expansion on GET is bounded
    // by refusing exponents outside the double range and by a per-request total expansion budget (CR-02, D-07).
    private const int MaxNumberChars = 64;
    private const int MaxExponentDigits = 6;
    private const int MaxExponent = 308;
    private const int MinExponent = -324; // smallest subnormal double is 5e-324
    private const int MaxNumberExpansionBudget = 65536;

    // On true the caller owns (and must dispose) doc. On false doc is null.
    public static bool TryAdmit(byte[] bytes, out JsonDocument? doc, out string runId)
    {
        doc = null;
        runId = "";
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth, AllowDuplicateProperties = false });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }

        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Walk(root) || !TryRunId(root, out var id))
        {
            parsed.Dispose();
            return false;
        }

        doc = parsed;
        runId = id;
        return true;
    }

    // Whole-tree check: well-formed names and strings, in-range and lossless numbers (one expansion budget for the body).
    public static bool Walk(JsonElement root)
    {
        var budget = 0;
        return WalkElement(root, ref budget);
    }

    public static bool TryGetCleanString(JsonElement v, out string? value)
    {
        value = null;
        try
        {
            value = v.GetString();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return value is not null && IsWellFormed(value);
    }

    public static bool TryGetPropertyName(JsonProperty p, out string name)
    {
        name = "";
        try
        {
            name = p.Name;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return IsWellFormed(name);
    }

    // jsonb rejects U+0000 and unpaired surrogates.
    public static bool IsWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\0')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryRunId(JsonElement root, out string runId)
    {
        runId = "";
        if (!root.TryGetProperty("run_id", out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!TryGetCleanString(v, out var s) || string.IsNullOrEmpty(s) || s.Length > MaxRunIdLength)
        {
            return false;
        }

        runId = s;
        return true;
    }

    private static bool WalkElement(JsonElement e, ref int budget)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (!TryGetPropertyName(p, out _) || !WalkElement(p.Value, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (!WalkElement(item, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.String:
                return TryGetCleanString(e, out _);
            case JsonValueKind.Number:
                return IsNumberInRange(e, ref budget) && JsonNumbers.IsLossless(e.GetRawText());
            default:
                return true;
        }
    }

    // Adds the number's worst-case growth over its wire size to budget; false when out of range or over budget.
    private static bool IsNumberInRange(JsonElement e, ref int budget)
    {
        var raw = e.GetRawText();
        if (raw.Length > MaxNumberChars)
        {
            return false;
        }

        var exponent = 0;
        var mantissaEnd = raw.Length;
        var ePos = raw.AsSpan().IndexOfAny('e', 'E');
        if (ePos >= 0)
        {
            mantissaEnd = ePos;
            var expText = raw.AsSpan(ePos + 1);
            var digits = expText.Length > 0 && (expText[0] == '+' || expText[0] == '-') ? expText.Length - 1 : expText.Length;
            if (digits is < 1 or > MaxExponentDigits
                || !int.TryParse(expText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is < MinExponent or > MaxExponent)
            {
                return false;
            }
        }

        var mantissaDigits = 0;
        for (var i = 0; i < mantissaEnd; i++)
        {
            if (char.IsAsciiDigit(raw[i]))
            {
                mantissaDigits++;
            }
        }

        // Charge only what the stored text can add beyond the raw token, so numbers without an exponent are free (WR-10).
        budget += Math.Max(0, mantissaDigits + Math.Abs(exponent) - raw.Length);
        return budget <= MaxNumberExpansionBudget;
    }
}
