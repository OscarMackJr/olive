using System.Security.Claims;
using Olive.Application;

namespace Olive.Host.Auth;

public static class ReviewerClaims
{
    public const string Oid = "olive:reviewer_oid";
    public const string Tid = "olive:reviewer_tid";
    public const string Group = "olive:group";
    public const string AuthSource = "olive:auth_source";
    public const string TenantId = OliveClaims.TenantId;

    // The tenant claim is added only when the server-side map yielded exactly one tenant (D-08). auth_source is supplied by the handler.
    public static ClaimsPrincipal ToPrincipal(ReviewerIdentity identity, Guid? tenantId, string authSource, string scheme)
    {
        var claims = new List<Claim>
        {
            new(Oid, identity.Oid),
            new(Tid, identity.Tid),
            new(AuthSource, authSource),
        };
        claims.AddRange(identity.Groups.Select(g => new Claim(Group, g)));
        if (tenantId is { } tenant) { claims.Add(new Claim(TenantId, tenant.ToString("D"))); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static ReviewerContext? ToReviewerContext(this ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(TenantId), out var tenant)) { return null; }
        return new ReviewerContext(
            tenant, user.FindFirstValue(Oid) ?? "", user.FindFirstValue(Tid) ?? "",
            user.FindAll(Group).Select(c => c.Value).ToArray(), user.FindFirstValue(AuthSource) ?? "");
    }
}
