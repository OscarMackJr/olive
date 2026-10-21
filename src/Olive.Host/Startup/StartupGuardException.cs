namespace Olive.Host.Startup;

// Static message only: the rule code and a fixed reason. Never carries configured values, URLs or secrets.
public sealed class StartupGuardException(string rule, string reason)
    : Exception($"Startup refused ({rule}): {reason}.")
{
    public string Rule { get; } = rule;
}
