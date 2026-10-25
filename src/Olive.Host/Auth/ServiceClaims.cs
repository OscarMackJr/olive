using System.Globalization;
using System.Security.Claims;
using Olive.Application;

namespace Olive.Host.Auth;

// Claims for an authenticated inbound service token (bluto). Never carries the token itself.
public static class ServiceClaims
{
    public const string PrincipalType = OliveClaims.PrincipalType;
    public const string ClientId = "olive:client_id";
    public const string TenantId = OliveClaims.TenantId;
    public const string DelegatedAgentId = "olive:delegated_agent_id";
    public const string ExpiresAt = OliveClaims.ExpiresAt;

    public static ClaimsPrincipal ToPrincipal(ServiceCaller caller, string scheme)
    {
        var claims = new List<Claim>
        {
            new(PrincipalType, caller.PrincipalType),
            new(ClientId, caller.ClientId),
            new(TenantId, caller.TenantId.ToString("D")),
            new(ExpiresAt, caller.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
        };
        if (caller.DelegatedAgentId is not null) { claims.Add(new Claim(DelegatedAgentId, caller.DelegatedAgentId)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static ServiceCaller? ToServiceCaller(this ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(TenantId), out var tenant)
            || user.FindFirstValue(PrincipalType) is not { } type
            || user.FindFirstValue(ClientId) is not { } client
            || !DateTimeOffset.TryParse(user.FindFirstValue(ExpiresAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires))
        {
            return null;
        }

        return new ServiceCaller(type, client, tenant, user.FindFirstValue(DelegatedAgentId), expires);
    }
}
