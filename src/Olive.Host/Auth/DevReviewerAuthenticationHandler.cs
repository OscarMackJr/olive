using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Problems;

namespace Olive.Host.Auth;

// D-07: dev reviewer scheme. The header carries only a fixture id; identity, groups and tenant come from server-side sources.
// Phase 7 replaces this with Entra OIDC emitting the same claims. Never logs the header value or any id.
public sealed class DevReviewerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IReviewerIdentitySource identities,
    IReviewerTenantMap tenantMap) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevReviewer";
    public const string HeaderName = "X-Olive-Dev-Reviewer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? identities.Find(values[0]!) : null;
        if (identity is null)
        {
            return Task.FromResult(AuthenticateResult.Fail(ProblemReasons.ReviewerUnauthenticated));
        }

        // D-08: the tenant claim exists only when the server-side map yields exactly one tenant.
        var tenants = tenantMap.TenantsFor(identity.Groups).Distinct().ToArray();
        Guid? tenant = tenants.Length == 1 ? tenants[0] : null;
        var principal = ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Stub, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    // D-02: an agent credential with no reviewer header is a wrong-caller case (403), not an unauthenticated one.
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var agentCredential = Request.Headers.ContainsKey(AgentKeyHeader.Name) && !Request.Headers.ContainsKey(HeaderName);
        if (agentCredential)
        {
            await ProblemResults.WriteAsync(Context, StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
            return;
        }

        await ProblemResults.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemReasons.ReviewerUnauthenticated);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResults.WriteAsync(Context, StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
}
