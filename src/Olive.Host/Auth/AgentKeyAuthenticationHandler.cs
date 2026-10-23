using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Olive.Application;
using Olive.Contracts;
using Olive.Host.Problems;

namespace Olive.Host.Auth;

// Introspects on every request, before the body is read or the store is touched (IDN-01, D-14).
public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IKeyIntrospector introspector,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // D-02: reviewer routes belong to the DevReviewer scheme; an agent key there is never introspected (T-05-23).
        if (Request.Path.StartsWithSegments("/v1/queue", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var key = AgentKeyHeader.TryRead(Request.Headers[AgentKeyHeader.Name].ToString());
        if (key is null)
        {
            return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
        }

        // IntrospectionUnavailableException is deliberately not caught: it fails closed via the exception handler.
        var result = await introspector.IntrospectAsync(key, Context.RequestAborted);
        if (!result.Active || result.Key is null || result.Key.ExpiresAt <= time.GetUtcNow())
        {
            return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
        }

        var principal = OliveClaims.ToPrincipal(result.Key, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // D-02: a reviewer identity with no agent credential on an agent route is a wrong-caller case.
        if (Request.Headers.ContainsKey(DevReviewerAuthenticationHandler.HeaderName) && !Request.Headers.ContainsKey(AgentKeyHeader.Name))
        {
            await ProblemResults.WriteAsync(Context, StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
            return;
        }

        Response.Headers.WWWAuthenticate = AgentKeyHeader.Scheme;
        await ProblemResults.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemReasons.KeyInactive);
    }
}
