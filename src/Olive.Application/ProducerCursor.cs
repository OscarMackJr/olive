using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Olive.Application;

// D-13: opaque poll cursor over (decided_at, item_id). Plain base64url of "v1.<unix microseconds>.<item id N-format>" with no MAC: the
// ordering keys are not secret and every query is confined by the token's tenant and producer predicates. Microseconds (not ticks)
// so a Postgres timestamptz round trip is lossless. Decoding is strict: anything not produced by Encode is rejected.
public static class ProducerCursor
{
    private const int MaxLength = 128;
    private const string Prefix = "v1.";
    private const long MaxMicros = 253402300799999999L; // 9999-12-31T23:59:59.999999Z

    public static string Encode(DateTime decidedAtUtc, Guid itemId)
    {
        var utc = decidedAtUtc.Kind == DateTimeKind.Local ? decidedAtUtc.ToUniversalTime() : DateTime.SpecifyKind(decidedAtUtc, DateTimeKind.Utc);
        var micros = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Prefix + micros.ToString(CultureInfo.InvariantCulture) + "." + itemId.ToString("N")));
    }

    public static bool TryDecode(string? cursor, out DateTime decidedAtUtc, out Guid itemId)
    {
        decidedAtUtc = default;
        itemId = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > MaxLength) { return false; }
        foreach (var c in cursor)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) { return false; }
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        // Non-canonical encodings (for example non-zero trailing bits) decode but do not re-encode to the same string.
        if (!string.Equals(Base64Url.EncodeToString(bytes), cursor, StringComparison.Ordinal)) { return false; }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) { return false; }
        var parts = text[Prefix.Length..].Split('.');
        if (parts.Length != 2) { return false; }
        var micros = parts[0];
        if (micros.Length is 0 or > 18 || !micros.All(char.IsAsciiDigit)) { return false; }
        if (!long.TryParse(micros, NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > MaxMicros) { return false; }
        if (parts[1].Length != 32 || !Guid.TryParseExact(parts[1], "N", out var id) || id.ToString("N") != parts[1]) { return false; }

        decidedAtUtc = new DateTime(DateTime.UnixEpoch.Ticks + m * 10, DateTimeKind.Utc);
        itemId = id;
        return true;
    }
}
