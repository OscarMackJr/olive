using System.Globalization;
using System.Security.Claims;
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

    // AuthenticationProperties item set by the decide handler: unix seconds at which the re-challenge was issued.
    public const string ReauthItem = "olive.reauth";
    private const long SkewSeconds = 60;

    public static AuthenticationBuilder AddReviewerOidc(this AuthenticationBuilder builder, OidcConfig cfg)
    {
        builder.Services.AddSingleton(new DecisionFreshnessOptions(cfg.Freshness));
        return builder.AddOpenIdConnect(Scheme, o =>
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
            o.Events.OnTokenValidated = async ctx =>
            {
                var services = ctx.HttpContext.RequestServices;
                var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
                try
                {
                    var result = EntraClaimsMapper.Map(
                        ctx.Principal!.Claims,
                        services.GetRequiredService<IReviewerTenantMap>(),
                        RoleAllowlist.FromRegistry(services.GetRequiredService<IQueueKindRegistry>()),
                        cfg.ExpectedTid);
                    var sessionStart = now;
                    if (ctx.Properties is not null && ctx.Properties.Items.TryGetValue(ReauthItem, out var issuedText))
                    {
                        // APR-03 / T-07-27: Entra may satisfy prompt=login silently, so the token itself must prove a recent authentication.
                        var existing = await ctx.HttpContext.AuthenticateAsync(ReviewerCookie.Scheme);
                        if (!long.TryParse(issuedText, NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
                            || result.AuthTime <= 0
                            || result.AuthTime < issued - SkewSeconds
                            || result.AuthTime > now.ToUnixTimeSeconds() + SkewSeconds)
                        {
                            ctx.Fail("reauth_evidence_missing");
                            return;
                        }

                        if (existing.Succeeded && existing.Principal is { } prior)
                        {
                            // T-07-31: a re-challenge cannot switch identity or extend the absolute cap.
                            if (!string.Equals(prior.FindFirstValue(ReviewerClaims.Oid), result.Identity.Oid, StringComparison.Ordinal)
                                || !long.TryParse(prior.FindFirstValue(ReviewerClaims.SessionStart), NumberStyles.None, CultureInfo.InvariantCulture, out var startSeconds))
                            {
                                ctx.Fail("reauth_evidence_missing");
                                return;
                            }

                            sessionStart = DateTimeOffset.FromUnixTimeSeconds(startSeconds);
                        }
                    }

                    var principal = ReviewerClaims.ToPrincipal(result.Identity, result.TenantId, QueueStates.Entra, ReviewerCookie.Scheme);
                    ctx.Principal = ReviewerClaims.WithSession(
                        principal, sessionStart, null,
                        result.AuthTime > 0 ? result.AuthTime : null, result.NoTenantReason);
                }
                catch (InvalidOperationException)
                {
                    ctx.Fail("identity_claims_invalid");
                }
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
}
