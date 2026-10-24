namespace Olive.Host.Pages.Shared;

// Item page time formats (UI-SPEC). The caller passes the time from the injected TimeProvider.
public static class TimeText
{
    public static string Utc(DateTimeOffset value) => Display.Timestamp(value);

    public static string Remaining(DateTimeOffset? expiresAt, DateTimeOffset now) => Display.Remaining(expiresAt, now);
}
