using System.Globalization;
using System.Security.Claims;
using Olive.Application;

namespace Olive.Host.Auth;

public static class OliveClaims
{
    public const string KeyId = "olive:key_id";
    public const string PrincipalType = "olive:principal_type";
    public const string AgentId = "olive:agent_id";
    public const string OnBehalfOf = "olive:on_behalf_of";
    public const string Purpose = "olive:purpose";
    public const string TenantId = "olive:tenant_id";
    public const string DataClassCeiling = "olive:data_class_ceiling";
    public const string ExpiresAt = "olive:expires_at";

    public static ClaimsPrincipal ToPrincipal(ActiveKey key, string scheme)
    {
        var claims = new List<Claim>
        {
            new(KeyId, key.KeyId),
            new(PrincipalType, key.PrincipalType),
            new(Purpose, key.Purpose),
            new(TenantId, key.TenantId.ToString("D")),
            new(DataClassCeiling, key.DataClassCeiling),
            new(ExpiresAt, key.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
        };
        if (key.AgentId is not null) { claims.Add(new Claim(AgentId, key.AgentId)); }
        if (key.OnBehalfOf is not null) { claims.Add(new Claim(OnBehalfOf, key.OnBehalfOf)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static ActiveKey ToActiveKey(this ClaimsPrincipal user) => new(
        user.FindFirstValue(KeyId)!,
        user.FindFirstValue(PrincipalType)!,
        user.FindFirstValue(AgentId),
        user.FindFirstValue(OnBehalfOf),
        user.FindFirstValue(Purpose)!,
        Guid.Parse(user.FindFirstValue(TenantId)!),
        user.FindFirstValue(DataClassCeiling)!,
        DateTimeOffset.Parse(user.FindFirstValue(ExpiresAt)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
