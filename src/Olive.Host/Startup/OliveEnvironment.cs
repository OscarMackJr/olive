namespace Olive.Host.Startup;

// FND-04: explicit, case-sensitive, no default and not derived from the ASP.NET Core environment name.
public sealed record OliveEnvironment(string Name)
{
    public static readonly string[] Allowed = ["local", "ci", "nonprod", "production"];

    public bool IsProduction => Name == "production";

    public static OliveEnvironment Parse(IConfiguration configuration)
    {
        var value = configuration["OLIVE_ENVIRONMENT"];
        if (value is null || !Allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new StartupGuardException("FND-04", "OLIVE_ENVIRONMENT must be exactly one of local, ci, nonprod, production");
        }

        return new OliveEnvironment(value);
    }
}
