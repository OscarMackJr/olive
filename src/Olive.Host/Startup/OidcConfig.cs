using System.Globalization;
using Olive.Domain;

namespace Olive.Host.Startup;

// UI-01 / D-18: Entra sign-in settings from environment variables. The secret is wrapped at once; messages name settings, never values.
public sealed record OidcConfig(string Authority, string ClientId, OidcClientSecret ClientSecret, string? ExpectedTid, TimeSpan? Freshness, bool RequireHttpsMetadata)
{
    // Null when the authority, client id or secret is absent (sign-in is then not offered; production refuses start through the guard).
    public static OidcConfig? Read(IConfiguration configuration)
    {
        var authority = Trimmed(configuration["OLIVE_OIDC_AUTHORITY"]);
        var clientId = Trimmed(configuration["OLIVE_OIDC_CLIENT_ID"]);
        var secret = configuration["OLIVE_OIDC_CLIENT_SECRET"];
        if (authority is null || clientId is null || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new OidcConfig(authority, clientId, new OidcClientSecret(secret), Trimmed(configuration["OLIVE_OIDC_TENANT_ID"]),
            ReadFreshness(configuration), RequireHttps(configuration, authority));
    }

    // Plain http metadata is allowed only for an http authority in the ci or local environments.
    private static bool RequireHttps(IConfiguration configuration, string authority)
    {
        var plainHttp = authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var env = configuration["OLIVE_ENVIRONMENT"];
        return !(plainHttp && (env == "ci" || env == "local"));
    }

    private static TimeSpan? ReadFreshness(IConfiguration configuration)
    {
        var text = configuration["OLIVE_OIDC_FRESHNESS_MINUTES"];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            throw new InvalidOperationException("OLIVE_OIDC_FRESHNESS_MINUTES must be a positive integer.");
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
