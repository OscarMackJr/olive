using System.Globalization;

namespace Olive.Host.Pages.Shared;

// UI-SPEC formats: UTC timestamps, ages and time remaining, all invariant culture.
public static class Display
{
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Age(long seconds)
    {
        var minutes = Math.Max(0, seconds) / 60;
        var hours = minutes / 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h {minutes % 60:00} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes} min");
    }

    public static string Remaining(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is not { } expiry) { return "No expiry"; }
        var left = expiry - now;
        if (left <= TimeSpan.Zero) { return "expired"; }
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes >= 60
            ? string.Create(CultureInfo.InvariantCulture, $"in {minutes / 60} h {minutes % 60} min")
            : string.Create(CultureInfo.InvariantCulture, $"in {minutes} min");
    }
}
