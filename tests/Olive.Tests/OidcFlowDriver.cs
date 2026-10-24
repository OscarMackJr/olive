using System.Globalization;
using System.Net;

namespace Olive.Tests;

// Drives the real OpenIdConnect handler without a browser: host client with auto-redirect and cookie handling off, a MANUAL cookie
// jar replayed through an explicit Cookie header, and a plain HttpClient for the mock IdP. The jar deliberately ignores
// Expires/Max-Age lifetimes (ASP.NET stamps Expires from the factory's pinned TimeProvider, which can differ from real time); only an
// explicit delete signal (empty value, Max-Age <= 0 or an epoch Expires) removes a cookie.
public sealed class OidcFlowDriver : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OliveApiFactory _factory;
    private readonly Dictionary<string, string> _jar = new(StringComparer.Ordinal);
    private readonly HttpClient _mock = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public OidcFlowDriver(OliveApiFactory factory)
    {
        _factory = factory;
        Host = factory.CreateUiClient(handleCookies: false);
    }

    public HttpClient Host { get; }

    public string? GetCookie(string name) => _jar.GetValueOrDefault(name);

    public void SetCookie(string name, string value) => _jar[name] = value;

    public IReadOnlyDictionary<string, string> Cookies => _jar;

    public void Dispose()
    {
        Host.Dispose();
        _mock.Dispose();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        if (_jar.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _jar.Select(c => c.Key + "=" + c.Value)));
        }

        var response = await Host.SendAsync(request, Ct);
        Absorb(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string pathAndQuery) => SendAsync(new HttpRequestMessage(HttpMethod.Get, pathAndQuery));

    public async Task<HttpResponseMessage> PostFormAsync(string pathAndQuery, IDictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery) { Content = new FormUrlEncodedContent(form) };
        return await SendAsync(request);
    }

    // Scrapes the antiforgery token from a page (using the jar) and posts the form with it.
    public async Task<HttpResponseMessage> PostWithAntiforgeryAsync(string tokenPage, string postPath, IDictionary<string, string>? form = null)
    {
        var page = await GetAsync(tokenPage);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var match = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "no antiforgery token on " + tokenPage);
        var fields = new Dictionary<string, string>(form ?? new Dictionary<string, string>())
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
        };
        return await PostFormAsync(postPath, fields);
    }

    // GET /auth/sign-in: expects the 302 to the mock authorize endpoint and returns that URL. Holds the correlation and nonce cookies.
    public async Task<Uri> GetAuthorizeUrlAsync(string returnUrl = "/review")
    {
        var response = await GetAsync("/auth/sign-in?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        // The handler omits response_mode when it equals the default for the code flow (query); the callback arriving as a GET proves it.
        Assert.True(query["response_mode"] is null or "query");
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
        return location;
    }

    // Follows the authorize URL at the mock and returns the callback path+query on the host (302 to https://localhost/signin-oidc?...).
    public async Task<string> FollowAuthorizeAsync(Uri authorizeUrl)
    {
        var response = await _mock.GetAsync(authorizeUrl, Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var callback = response.Headers.Location;
        Assert.NotNull(callback);
        Assert.Equal("/signin-oidc", callback.AbsolutePath);
        return callback.PathAndQuery;
    }

    // Full sign-in for one scenario; returns the response to the callback (normally the 302 to the return URL).
    public async Task<HttpResponseMessage> SignInAsync(string scenario, string returnUrl = "/review", Func<string, string>? tamper = null)
    {
        _factory.Scenario.Set(scenario);
        var authorize = await GetAuthorizeUrlAsync(returnUrl);
        var callback = await FollowAuthorizeAsync(authorize);
        return await GetAsync(tamper is null ? callback : tamper(callback));
    }

    private void Absorb(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var header in values)
        {
            var parts = header.Split(';');
            var pair = parts[0].Trim();
            var eq = pair.IndexOf('=');
            if (eq <= 0) { continue; }
            var name = pair[..eq];
            var value = pair[(eq + 1)..];
            var delete = value.Length == 0;
            foreach (var attr in parts.Skip(1).Select(a => a.Trim()))
            {
                if (attr.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(attr["max-age=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge) && maxAge <= 0)
                {
                    delete = true;
                }
                else if (attr.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(attr["expires=".Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
                    && expires.Year <= 1970)
                {
                    delete = true;
                }
            }

            if (delete) { _jar.Remove(name); } else { _jar[name] = value; }
        }
    }
}
