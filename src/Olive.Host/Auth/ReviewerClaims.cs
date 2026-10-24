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
    public const string AuthTime = "olive:auth_time";
    public const string SessionStart = "olive:session_start";
    public const string ReviewerId = "olive:reviewer_id";
    public const string NoTenantReason = "olive:no_tenant_reason";

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

    // D-03: session bookkeeping claims added when the cookie session is created. Times are unix seconds.
    public static ClaimsPrincipal WithSession(ClaimsPrincipal principal, DateTimeOffset sessionStart, string? reviewerId, long? authTime, string? noTenantReason)
    {
        var source = (ClaimsIdentity)principal.Identity!;
        var claims = source.Claims.ToList();
        claims.Add(new Claim(SessionStart, sessionStart.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (reviewerId is not null) { claims.Add(new Claim(ReviewerId, reviewerId)); }
        if (authTime is { } at) { claims.Add(new Claim(AuthTime, at.ToString(System.Globalization.CultureInfo.InvariantCulture))); }
        if (noTenantReason is not null) { claims.Add(new Claim(NoTenantReason, noTenantReason)); }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, source.AuthenticationType));
    }

    public static ReviewerContext? ToReviewerContext(this ClaimsPrincipal user)
    {
        if (!Guid.TryParse(user.FindFirstValue(TenantId), out var tenant)) { return null; }
        return new ReviewerContext(
            tenant, user.FindFirstValue(Oid) ?? "", user.FindFirstValue(Tid) ?? "",
            user.FindAll(Group).Select(c => c.Value).ToArray(), user.FindFirstValue(AuthSource) ?? "");
    }
}
