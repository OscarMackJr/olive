using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Olive.Domain;

// D-18 / FND-06: the Entra sign-in client secret. A sealed class, never a record (a record's generated ToString prints members).
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<OidcClientSecret>))]
public sealed class OidcClientSecret : IFormattable
{
    private readonly string _value;

    public OidcClientSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("An OIDC client secret must not be empty.");
        }

        _value = value;
    }

    public string ExposeSecret() => _value;

    public override string ToString() => "[REDACTED]";

    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
