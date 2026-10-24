using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Olive.Application;
using Olive.Domain;
using Olive.Host.Startup;
using Olive.Integrations.Entra;

namespace Olive.Host.Auth;

// UI-01 / D-01: the real OpenIdConnect handler (code flow, PKCE, query response). Tokens are never stored and only Olive claims
// reach the reviewer cookie. Failure handlers are static: they never read or echo the failure message or the protocol message.
public static class ReviewerOidc
{
    public const string Scheme = "Entra";
    public const string FailedPath = "/auth/failed";

    public static AuthenticationBuilder AddReviewerOidc(this AuthenticationBuilder builder, OidcConfig cfg) =>
        builder.AddOpenIdConnect(Scheme, o =>
        {
            o.SignInScheme = ReviewerCookie.Scheme;
            o.Authority = cfg.Authority;
            o.ClientId = cfg.ClientId;
            o.ClientSecret = cfg.ClientSecret.ExposeSecret();
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.RequireHttpsMetadata = cfg.RequireHttpsMetadata;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Events.OnTokenValidated = ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                try
                {
                    var result = EntraClaimsMapper.Map(
                        ctx.Principal!.Claims,
                        services.GetRequiredService<IReviewerTenantMap>(),
                        RoleAllowlist.FromRegistry(services.GetRequiredService<IQueueKindRegistry>()),
                        cfg.ExpectedTid);
                    var principal = ReviewerClaims.ToPrincipal(result.Identity, result.TenantId, QueueStates.Entra, ReviewerCookie.Scheme);
                    ctx.Principal = ReviewerClaims.WithSession(
                        principal, services.GetRequiredService<TimeProvider>().GetUtcNow(), null,
                        result.AuthTime > 0 ? result.AuthTime : null, result.NoTenantReason);
                }
                catch (InvalidOperationException)
                {
                    ctx.Fail("identity_claims_invalid");
                }

                return Task.CompletedTask;
            };
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect(FailedPath);
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
            o.Events.OnAuthenticationFailed = ctx =>
            {
                ctx.Response.Redirect(FailedPath);
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
}
