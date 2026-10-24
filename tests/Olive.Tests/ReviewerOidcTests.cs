using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olive.Host.Auth;

namespace Olive.Tests;

// UI-01: the real OpenIdConnect handler (code flow, PKCE, response_mode=query) against a digest-pinned mock-oauth2-server.
[Trait("Category", "Integration")]
public sealed class ReviewerOidcTests(PostgresFixture pg, MockOidcFixture mock) : IClassFixture<MockOidcFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OliveApiFactory NewFactory(TestDatabase db) =>
        new(db, mockOidc: mock, configureServices: s => s.AddSingleton<IStartupFilter>(new ReviewerCookieProbeStartupFilter()));

    private static async Task<JsonElement> ProbeAsync(OidcFlowDriver driver)
    {
        var response = await driver.GetAsync("/_probe/reviewer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    [Fact]
    public async Task UI_01_real_handler_sign_in_issues_the_reviewer_cookie_with_entra_claims()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        var callback = await driver.SignInAsync("role-present", "/review");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/review", callback.Headers.Location?.OriginalString);
        Assert.NotNull(driver.GetCookie(ReviewerCookie.CookieName));

        var page = await driver.GetAsync("/review");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Microsoft Entra ID", html);

        var probe = await ProbeAsync(driver);
        Assert.Equal("entra", probe.GetProperty("auth_source").GetString());
        Assert.Equal(TestPaths.TenantAlpha.ToString(), probe.GetProperty("tenant_id").GetString());
        Assert.Equal(MockOidcFixture.ReviewerOid, probe.GetProperty("oid").GetString());
        Assert.Equal(MockOidcFixture.Tenant, probe.GetProperty("tid").GetString());
        var groups = probe.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToArray();
        Assert.Contains("role:case_supervisor", groups);
        Assert.Contains(MockOidcFixture.AlphaGroup, groups);
        Assert.Null(probe.GetProperty("auth_time").GetString());
        Assert.False(string.IsNullOrEmpty(probe.GetProperty("session_start").GetString()));
    }

    [Fact]
    public async Task UI_01_role_absent_token_gives_no_role_group()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync("role-absent")).StatusCode);
        var groups = (await ProbeAsync(driver)).GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToArray();
        Assert.DoesNotContain(groups, g => g!.StartsWith("role:", StringComparison.Ordinal));
        Assert.Contains(MockOidcFixture.AlphaGroup, groups);
    }

    [Fact]
    public async Task UI_01_token_auth_time_is_carried_into_the_session()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync("fresh")).StatusCode);
        Assert.Equal(mock.T0.ToUnixTimeSeconds().ToString(), (await ProbeAsync(driver)).GetProperty("auth_time").GetString());
    }

    [Fact]
    public async Task UI_01_tokens_code_and_secret_never_reach_cookies_html_or_logs()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        factory.Scenario.Set("role-present");
        var authorize = await driver.GetAuthorizeUrlAsync();
        var callbackUrl = await driver.FollowAuthorizeAsync(authorize);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri("https://localhost" + callbackUrl).Query)["code"];
        Assert.False(string.IsNullOrEmpty(code));
        var callback = await driver.GetAsync(callbackUrl);
        var page = await driver.GetAsync("/review");

        Assert.False(string.IsNullOrEmpty(factory.Scenario.LastIdToken));
        var scan = await SentinelScan.CollectAsync(factory, db, callback, page);
        scan.AddSource("cookies", string.Join("\n", driver.Cookies.Select(c => c.Key + "=" + c.Value)));
        scan.AddSource("authorize", authorize.ToString().Replace(code!, "", StringComparison.Ordinal));
        var sentinels = new List<string> { factory.Scenario.LastIdToken!, factory.OidcClientSecret };
        if (!string.IsNullOrEmpty(factory.Scenario.LastAccessToken)) { sentinels.Add(factory.Scenario.LastAccessToken); }
        scan.AssertAbsent(sentinels.ToArray());

        var logsAndCookies = new SentinelScan();
        logsAndCookies.AddSource("logs", factory.Logs.Text);
        logsAndCookies.AddSource("cookies", string.Join("\n", driver.Cookies.Select(c => c.Key + "=" + c.Value)));
        logsAndCookies.AssertAbsent(code!);
    }

    [Fact]
    public async Task UI_01_groups_overage_gives_403_with_groups_overage()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync("overage")).StatusCode);
        var page = await driver.GetAsync("/review");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("Reason code: groups_overage", await page.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("no-tenant")]
    [InlineData("two-tenant")]
    public async Task UI_01_zero_or_several_tenants_give_403_with_no_reviewer_tenant_and_the_cookie_is_still_issued(string scenario)
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync(scenario)).StatusCode);
        Assert.NotNull(driver.GetCookie(ReviewerCookie.CookieName));
        var page = await driver.GetAsync("/review");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("Reason code: no_reviewer_tenant", await page.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task UI_01_tampered_state_redirects_to_the_static_failed_page_with_no_session()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        var callback = await driver.SignInAsync("role-present", "/review", tamper: q => System.Text.RegularExpressions.Regex.Replace(q, "state=[^&]+", "state=tampered"));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/auth/failed", callback.Headers.Location?.OriginalString);
        Assert.Null(driver.GetCookie(ReviewerCookie.CookieName));

        var failed = await driver.GetAsync("/auth/failed");
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Sign-in did not complete", body);
        Assert.Contains("Try signing in again", body);
        foreach (var leak in new[] { "tampered", "unable to unprotect", "Correlation", "error_description", "Exception" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }

        var withError = await driver.GetAsync("/auth/failed?error=access_denied&error_description=SECRET-TEXT");
        Assert.DoesNotContain("SECRET-TEXT", await withError.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task UI_01_sign_out_clears_the_cookie_and_signs_out_of_the_idp()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync("role-present")).StatusCode);
        var response = await driver.PostWithAntiforgeryAsync("/review", "/auth/sign-out");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.True(location.Contains("endsession", StringComparison.OrdinalIgnoreCase) || location == "/signed-out", "unexpected sign-out target");
        Assert.Null(driver.GetCookie(ReviewerCookie.CookieName));

        var after = await driver.GetAsync("/review");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/auth/sign-in", after.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UI_01_sign_in_page_ignores_a_non_local_return_url()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        factory.Scenario.Set("role-present");
        var authorize = await driver.GetAuthorizeUrlAsync("https://evil.example/steal");
        var callback = await driver.GetAsync(await driver.FollowAuthorizeAsync(authorize));
        Assert.Equal("/review", callback.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UI_01_authorize_request_has_no_prompt_parameter_for_plain_sign_in()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);

        var authorize = await driver.GetAuthorizeUrlAsync();
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Null(query["prompt"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(MockOidcFixture.ClientId, query["client_id"]);
    }

    [Fact]
    public async Task UI_01_http_authority_works_only_because_the_environment_is_ci()
    {
        Assert.StartsWith("http://", mock.Authority);
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync("role-present")).StatusCode);
        Assert.True(Olive.Host.Startup.OidcConfig.Read(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OLIVE_ENVIRONMENT"] = "production",
            ["OLIVE_OIDC_AUTHORITY"] = mock.Authority,
            ["OLIVE_OIDC_CLIENT_ID"] = "x",
            ["OLIVE_OIDC_CLIENT_SECRET"] = "y",
        }).Build())!.RequireHttpsMetadata);
    }
}

// Test-only (never in src): echoes the reviewer cookie principal's Olive claims so tests can see what the OIDC handler issued.
internal sealed class ReviewerCookieProbeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nxt) =>
        {
            if (!ctx.Request.Path.Equals("/_probe/reviewer", StringComparison.OrdinalIgnoreCase))
            {
                await nxt();
                return;
            }

            var policy = new AuthorizationPolicyBuilder(ReviewerCookie.Scheme).RequireAuthenticatedUser().Build();
            var evaluator = ctx.RequestServices.GetRequiredService<IPolicyEvaluator>();
            var authn = await evaluator.AuthenticateAsync(policy, ctx);
            if (authn.Principal is null || !authn.Succeeded)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var user = authn.Principal;
            await ctx.Response.WriteAsJsonAsync(new
            {
                auth_source = user.FindFirst(ReviewerClaims.AuthSource)?.Value,
                tenant_id = user.FindFirst(ReviewerClaims.TenantId)?.Value,
                oid = user.FindFirst(ReviewerClaims.Oid)?.Value,
                tid = user.FindFirst(ReviewerClaims.Tid)?.Value,
                groups = user.FindAll(ReviewerClaims.Group).Select(c => c.Value).ToArray(),
                auth_time = user.FindFirst(ReviewerClaims.AuthTime)?.Value,
                session_start = user.FindFirst(ReviewerClaims.SessionStart)?.Value,
                no_tenant_reason = user.FindFirst(ReviewerClaims.NoTenantReason)?.Value,
                claim_types = user.Claims.Select(c => c.Type).Distinct().ToArray(),
            });
        });
        next(app);
    };
}
