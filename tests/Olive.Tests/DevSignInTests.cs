using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Olive.Host.Auth;
using Olive.Host.Startup;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class DevSignInTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> PostPickerAsync(HttpClient client, string reviewerId, string returnUrl = "", bool withToken = true)
    {
        var fields = new Dictionary<string, string> { ["reviewerId"] = reviewerId, ["returnUrl"] = returnUrl };
        if (withToken) { fields["__RequestVerificationToken"] = await OliveApiFactory.ScrapeAntiforgeryTokenAsync(client, "/dev/sign-in"); }
        return await client.PostAsync("/dev/sign-in", new FormUrlEncodedContent(fields), Ct);
    }

    private static bool SetsReviewerCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(v => v.StartsWith(ReviewerCookie.CookieName + "=", StringComparison.Ordinal) && !v.StartsWith(ReviewerCookie.CookieName + "=;", StringComparison.Ordinal));

    [Fact]
    public async Task UI_01_picker_lists_the_fixture_reviewers()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var html = await client.GetStringAsync("/dev/sign-in", Ct);
        foreach (var id in new[] { "alpha-sup-1", "alpha-norole", "beta-sup", "two-tenant", "no-tenant" })
        {
            Assert.Contains("Sign in as " + id, html);
        }

        Assert.Contains("Development sign-in", html);
    }

    [Fact]
    public async Task UI_01_picking_a_reviewer_issues_the_host_cookie_and_review_returns_200()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var response = await PostPickerAsync(client, "alpha-sup-1");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/review", response.Headers.Location?.OriginalString);
        Assert.True(SetsReviewerCookie(response));
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(ReviewerCookie.CookieName, StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        var review = await client.GetAsync("/review", Ct);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var html = await review.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Development fixture", html);
        Assert.Contains("Sign out", html);
    }

    [Fact]
    public async Task UI_01_post_without_an_antiforgery_token_is_400_and_issues_no_cookie()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var response = await PostPickerAsync(client, "alpha-sup-1", withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(SetsReviewerCookie(response));
    }

    [Fact]
    public async Task UI_01_unknown_reviewer_id_issues_no_cookie_and_does_not_echo_the_value()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var response = await PostPickerAsync(client, "no-such-reviewer-xyz");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(SetsReviewerCookie(response));
        Assert.DoesNotContain("no-such-reviewer-xyz", response.Headers.Location?.OriginalString ?? "");
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/review", Ct)).StatusCode);
    }

    [Fact]
    public async Task UI_01_anonymous_review_redirects_to_the_picker()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var response = await client.GetAsync("/review", Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/dev/sign-in", response.Headers.Location!.AbsolutePath);
    }

    [Theory]
    [InlineData("//evil.example", "/review")]
    [InlineData("https://evil.example/x", "/review")]
    [InlineData("/review", "/review")]
    public async Task UI_01_return_url_is_honoured_only_when_local(string returnUrl, string expected)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = factory.CreateUiClient();
        var response = await PostPickerAsync(client, "alpha-sup-1", returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UI_01_sign_out_post_clears_the_session()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = await factory.SignInDevAsync("alpha-sup-1");
        var token = await OliveApiFactory.ScrapeAntiforgeryTokenAsync(client, "/review");
        var response = await client.PostAsync("/auth/sign-out", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signed-out", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/review", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/signed-out", Ct)).StatusCode);
    }

    [Fact]
    public async Task UI_01_sign_out_without_a_token_is_400()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = await factory.SignInDevAsync("alpha-sup-1");
        var response = await client.PostAsync("/auth/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/review", Ct)).StatusCode);
    }

    [Fact]
    public async Task UI_01_pages_send_csp_without_form_action_and_no_store()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var client = await factory.SignInDevAsync("alpha-sup-1");
        var response = await client.GetAsync("/review", Ct);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.DoesNotContain("form-action", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single());
        var css = await client.GetAsync("/olive.css", Ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/signed-out", Ct)).StatusCode);
    }

    [Fact]
    public void UI_01_production_refuses_to_start_with_the_dev_sign_in_gate_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DevSignInGate>();
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<StartupGuardException>(() =>
            StartupGuards.CheckNoNonProductionComponents(services, provider, new OliveEnvironment("production")));
        Assert.Contains("FND-05", ex.Message);
        StartupGuards.CheckNoNonProductionComponents(services, provider, new OliveEnvironment("ci"));
    }
}

// UI-01 / WR-01: with Entra configured the dev picker is not a sign-in path. The gate is never registered, /dev/sign-in is 404
// on both verbs, no reviewer cookie is issued and an anonymous /review goes to the OIDC sign-in, never to the picker.
[Trait("Category", "Integration")]
public sealed class DevSignInOidcModeTests(PostgresFixture fixture, MockOidcFixture mock) : IClassFixture<MockOidcFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UI_01_dev_sign_in_is_not_reachable_when_oidc_is_configured()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, mockOidc: mock);
        Assert.Null(factory.Services.GetService<DevSignInGate>());
        using var client = factory.CreateUiClient();

        var get = await client.GetAsync("/dev/sign-in", Ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.DoesNotContain("Development sign-in", await get.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain("alpha-sup-1", await get.Content.ReadAsStringAsync(Ct));

        var post = await client.PostAsync("/dev/sign-in", new FormUrlEncodedContent(new Dictionary<string, string> { ["reviewerId"] = "alpha-sup-1", ["returnUrl"] = "" }), Ct);
        Assert.NotEqual(HttpStatusCode.Redirect, post.StatusCode);
        Assert.False((int)post.StatusCode is >= 200 and < 400, "picker POST was admitted: " + post.StatusCode);
        Assert.False(post.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(v => v.StartsWith(ReviewerCookie.CookieName + "=", StringComparison.Ordinal)));

        var review = await client.GetAsync("/review", Ct);
        Assert.Equal(HttpStatusCode.Redirect, review.StatusCode);
        var location = review.Headers.Location!;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        Assert.StartsWith("/auth/sign-in", path, StringComparison.Ordinal);
    }
}
