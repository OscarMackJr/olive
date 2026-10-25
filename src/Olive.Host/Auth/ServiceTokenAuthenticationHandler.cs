using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Olive.Application;
using Olive.Contracts;
using Olive.Domain;
using Olive.Host.Problems;

namespace Olive.Host.Auth;

// D-05: inbound service Bearer tokens (bluto -> Olive) on the queue routes, verified through popeye only.
// Never logs or stores the token. IntrospectionUnavailableException is deliberately not caught: it fails closed via the exception handler.
public sealed class ServiceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceTokenVerifier verifier,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ServiceToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/v1/queue/items", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = InboundBearerToken.TryRead(Request.Headers.Authorization.ToString());
        if (token is null)
        {
            return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
        }

        var result = await verifier.VerifyAsync(token, Context.RequestAborted);
        if (!result.Active || result.Caller is null || result.Caller.ExpiresAt <= time.GetUtcNow())
        {
            return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
        }

        var principal = ServiceClaims.ToPrincipal(result.Caller, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = AgentKeyHeader.Scheme;
        await ProblemResults.WriteAsync(Context, StatusCodes.Status401Unauthorized, ProblemReasons.KeyInactive);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResults.WriteAsync(Context, StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
}

// D-01: one policy scheme on the queue routes picks the handler from header shape only (no I/O).
public static class QueueCaller
{
    public const string SchemeName = "QueueCaller";

    public static string Select(HttpContext context) =>
        context.Request.Headers.ContainsKey(AgentKeyHeader.Name) && !context.Request.Headers.ContainsKey(DevReviewerAuthenticationHandler.HeaderName)
            ? ServiceTokenAuthenticationHandler.SchemeName
            : DevReviewerAuthenticationHandler.SchemeName;
}
