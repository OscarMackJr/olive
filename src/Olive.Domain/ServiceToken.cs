using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Olive.Domain;

// FND-06: a sealed class, never a record (a record's generated ToString prints members).
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<ServiceToken>))]
public sealed class ServiceToken : IFormattable
{
    private readonly string _value;

    public ServiceToken(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A service token must not be empty.");
        }

        _value = value;
    }

    public string ExposeSecret() => _value;

    public override string ToString() => "[REDACTED]";

    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
