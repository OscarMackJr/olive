namespace Olive.Host.Startup;

// D-20: no form-action (it can block the IdP redirect after a POST), no inline script or style, no third-party origin.
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    private static readonly string[] NoStorePrefixes = ["/review", "/dev", "/auth", "/signed-out"];

    public static IApplicationBuilder UseOliveSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            if (NoStorePrefixes.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                headers["Cache-Control"] = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    });
}
