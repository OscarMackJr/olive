using Olive.Domain;

namespace Olive.Application;

// D-12 ASSUMPTION: no spec names this header; confirm with the nono owner. Changing it means editing this one class.
public static class AgentKeyHeader
{
    public const string Name = "Authorization";
    public const string Scheme = "Bearer";
    private const int MaxLength = 4096;

    public static SessionKey? TryRead(string? headerValue)
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

        return new SessionKey(parts[1]);
    }
}
