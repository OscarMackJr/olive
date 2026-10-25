using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Olive.Domain;

// D-05 / FND-06: an inbound service Bearer token (bluto -> Olive). A sealed class, never a record. Deliberately distinct from
// SessionKey, ServiceToken and ServiceTokenGrant so the IDN-08 architecture rules can tell the credentials apart.
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<InboundBearerToken>))]
public sealed class InboundBearerToken : IFormattable
{
    private const string Scheme = "Bearer";
    private const int MaxLength = 4096;

    private readonly string _value;

    public InboundBearerToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("An inbound bearer token must not be empty.");
        }

        _value = value;
    }

    // Same parsing rules as the agent key header: at most 4096 chars, exactly "Bearer <token>", scheme case-insensitive.
    public static InboundBearerToken? TryRead(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue) || headerValue.Length > MaxLength)
        {
            return null;
        }

        var parts = headerValue.Split(' ');
        if (parts.Length != 2 || !string.Equals(parts[0], Scheme, StringComparison.OrdinalIgnoreCase) || parts[1].Length == 0)
        {
            return null;
        }

        return new InboundBearerToken(parts[1]);
    }

    public string ExposeSecret() => _value;

    public override string ToString() => "[REDACTED]";

    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
