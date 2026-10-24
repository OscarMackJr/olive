using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Olive.Host.Auth;

// D-03: the reviewer browser session. Sliding 30 minutes with an 8 hour absolute cap measured from olive:session_start.
public static class ReviewerCookie
{
    public const string Scheme = "OliveReviewer";
    public const string CookieName = "__Host-olive-reviewer";
    public const string LoginPath = "/dev/sign-in";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromHours(8);

    public static AuthenticationBuilder AddReviewerSession(this AuthenticationBuilder builder)
    {
        builder.Services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<TimeProvider>((o, time) => o.TimeProvider = time);
        builder.Services.AddSingleton<IDecisionFreshness, DecisionFreshness>();
        return builder.AddCookie(Scheme, o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = IdleTimeout;
            o.SlidingExpiration = true;
            o.LoginPath = LoginPath;
            o.AccessDeniedPath = "/review";
            o.ReturnUrlParameter = "returnUrl";
            o.Events.OnValidatePrincipal = EnforceAbsoluteCap;
            o.Events.OnRedirectToLogin = ctx =>
            {
                // Agent and API routes never get a browser redirect.
                if (ctx.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        });
    }

    // D-03: sliding renewal must not extend a session past the absolute cap; a missing or malformed start claim is rejected.
    public static async Task EnforceAbsoluteCap(CookieValidatePrincipalContext ctx)
    {
        var time = ctx.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var raw = ctx.Principal?.FindFirstValue(ReviewerClaims.SessionStart);
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds) > AbsoluteCap)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(Scheme);
        }
    }

    public static Task SignInAsync(HttpContext http, ClaimsPrincipal principal) =>
        http.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = false });
}
