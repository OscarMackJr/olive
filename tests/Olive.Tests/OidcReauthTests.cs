using System.Net;
using System.Web;
using Olive.Host.Auth;

namespace Olive.Tests;

// APR-03 / UI-04 on the real OpenIdConnect path: a stale Entra session is re-challenged (prompt=login, max_age=0), the draft survives
// server-side, the fresh token's evidence and roles are what decide, and a removed role or stale evidence is refused.
[Trait("Category", "Integration")]
public sealed class OidcReauthTests(PostgresFixture pg, MockOidcFixture mock) : IClassFixture<MockOidcFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OliveApiFactory NewFactory(TestDatabase db) => new(db, mockOidc: mock);

    private static Dictionary<string, string> Form(string decision = "approve") => new() { ["decision"] = decision, ["rationale"] = DecisionHttp.Rationale };

    // Signs in with scenario role-present (no auth_time: stale by rule), claims a fresh item and returns it.
    private static async Task<(Guid Item, Guid Proposal)> SeedClaimedAsync(OliveApiFactory factory, TestDatabase db, OidcFlowDriver driver, string run, string scenario = "role-present")
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, run);
        Assert.Equal(HttpStatusCode.Redirect, (await driver.SignInAsync(scenario)).StatusCode);
        var claim = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Claim");
        Assert.Equal(HttpStatusCode.Redirect, claim.StatusCode);
        Assert.Equal("claimed", await ItemState(db, item));
        return (item, proposal);
    }

    private static Task<string?> ItemState(TestDatabase db, Guid item) => DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item));

    private static Task<long> Events(TestDatabase db, Guid item) =>
        DecisionHttp.Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i", ("i", item));

    // POSTs a decision and returns the redirect to the mock authorize endpoint that the stale session must produce.
    private static async Task<Uri> StaleDecideAsync(OidcFlowDriver driver, Guid item, string decision = "approve")
    {
        var response = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form(decision));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.True(location.IsAbsoluteUri, "expected the mock authorize URL");
        return location;
    }

    private static async Task<HttpResponseMessage> CompleteCallbackAsync(OliveApiFactory factory, OidcFlowDriver driver, Uri authorize, string scenario)
    {
        factory.Scenario.Set(scenario);
        return await driver.GetAsync(await FollowReauthAsync(authorize));
    }

    // prompt=login makes the mock show its login form even with interactiveLogin=false; the form post stands in for the user re-entering credentials.
    private static async Task<string> FollowReauthAsync(Uri authorize)
    {
        using var mockClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var response = await mockClient.PostAsync(authorize, new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "reviewer@example.test" }), Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var callback = response.Headers.Location;
        Assert.NotNull(callback);
        Assert.Equal("/signin-oidc", callback.AbsolutePath);
        return callback.PathAndQuery;
    }

    private static string ResumeKey(HttpResponseMessage callback)
    {
        var location = callback.Headers.Location!.OriginalString;
        Assert.Contains("?resume=", location);
        return HttpUtility.ParseQueryString(location[(location.IndexOf('?') + 1)..])["resume"]!;
    }

    [Fact]
    public async Task UI_04_APR_03_stale_session_decide_is_re_challenged_with_prompt_login_and_max_age_zero()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea1");
        var events = await Events(db, item);

        var authorize = await StaleDecideAsync(driver, item);

        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("0", query["max_age"]);
        Assert.DoesNotContain(DecisionHttp.Rationale, authorize.ToString());
        Assert.DoesNotContain(DecisionHttp.Rationale, Uri.UnescapeDataString(authorize.ToString()));
        Assert.Equal("claimed", await ItemState(db, item));
        Assert.Equal(events, await Events(db, item));
    }

    [Fact]
    public async Task UI_04_APR_03_fresh_re_authentication_resumes_the_draft_once_and_a_normal_post_then_decides()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, proposal) = await SeedClaimedAsync(factory, db, driver, "rea2");
        var authorize = await StaleDecideAsync(driver, item, "reject");

        var callback = await CompleteCallbackAsync(factory, driver, authorize, "fresh");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var key = ResumeKey(callback);
        Assert.StartsWith(UiHttp.ItemPath(item) + "?resume=", callback.Headers.Location!.OriginalString);
        Assert.Equal(43, key.Length);

        var restored = await driver.GetAsync(callback.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var html = await restored.Content.ReadAsStringAsync(Ct);
        Assert.Contains("You signed in again. Your draft decision is restored below.", html);
        Assert.Matches("value=\"reject\"\\s+required\\s+checked", html);
        Assert.DoesNotMatch("value=\"approve\"\\s+required\\s+checked", html);
        Assert.Contains(DecisionHttp.Rationale, html);
        Assert.DoesNotContain(key, html);
        Assert.Equal("claimed", await ItemState(db, item));

        var again = await (await driver.GetAsync(callback.Headers.Location.OriginalString)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("your draft decision could not be restored", again);
        Assert.DoesNotContain("You signed in again", again);
        Assert.DoesNotContain(DecisionHttp.Rationale, again);

        var decide = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form("approve"));
        Assert.Equal(HttpStatusCode.Redirect, decide.StatusCode);
        Assert.Equal(UiHttp.ItemPath(item), decide.Headers.Location?.OriginalString);
        Assert.Equal("decided", await ItemState(db, item));
        Assert.Equal(1L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and execution_mode = 'simulated'", ("p", proposal)));
    }

    [Fact]
    public async Task UI_04_resume_get_never_decides()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, proposal) = await SeedClaimedAsync(factory, db, driver, "rea2b");
        var callback = await CompleteCallbackAsync(factory, driver, await StaleDecideAsync(driver, item), "fresh");
        var events = await Events(db, item);

        Assert.Equal(HttpStatusCode.OK, (await driver.GetAsync(callback.Headers.Location!.OriginalString)).StatusCode);

        Assert.Equal("claimed", await ItemState(db, item));
        Assert.Equal(events, await Events(db, item));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p", ("p", proposal)));
    }

    [Fact]
    public async Task APR_03_role_removed_in_the_fresh_token_gives_403_role_not_held_and_the_item_stays_claimed()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea3");
        var claimedBy = await DecisionHttp.Scalar<string>(db, "select claimed_by from queue_item where item_id = @i", ("i", item));
        var events = await Events(db, item);

        var callback = await CompleteCallbackAsync(factory, driver, await StaleDecideAsync(driver, item), "fresh-role-absent");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        var decide = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form());

        Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);
        var body = await decide.Content.ReadAsStringAsync(Ct);
        Assert.Contains("role_not_held", body);
        Assert.DoesNotContain(DecisionHttp.Rationale, body);
        Assert.Equal("claimed", await ItemState(db, item));
        Assert.Equal(claimedBy, await DecisionHttp.Scalar<string>(db, "select claimed_by from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(events, await Events(db, item));
    }

    [Fact]
    public async Task APR_03_stale_auth_time_evidence_fails_closed_and_the_prior_session_is_kept()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea4");
        var authorize = await StaleDecideAsync(driver, item);
        var before = driver.GetCookie(ReviewerCookie.CookieName);
        Assert.NotNull(before);

        var callback = await CompleteCallbackAsync(factory, driver, authorize, "stale-auth-time");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(ReviewerOidc.FailedPath, callback.Headers.Location?.OriginalString);
        Assert.Equal((HttpStatusCode)400, (await driver.GetAsync("/auth/failed")).StatusCode);
        Assert.Equal(before, driver.GetCookie(ReviewerCookie.CookieName));
        Assert.Equal(HttpStatusCode.OK, (await driver.GetAsync("/review")).StatusCode);
        Assert.Equal("claimed", await ItemState(db, item));
    }

    [Fact]
    public async Task APR_03_another_reviewers_resume_key_is_unavailable_and_the_owner_can_still_use_it_once()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var a = new OidcFlowDriver(factory);
        using var b = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, a, "rea5");
        var authorize = await StaleDecideAsync(a, item);
        var callback = await CompleteCallbackAsync(factory, a, authorize, "fresh");
        var resume = callback.Headers.Location!.OriginalString;

        Assert.Equal(HttpStatusCode.Redirect, (await b.SignInAsync("fresh-other-reviewer")).StatusCode);
        var other = await (await b.GetAsync(resume)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("your draft decision could not be restored", other);
        Assert.DoesNotContain(DecisionHttp.Rationale, other);

        var owner = await (await a.GetAsync(resume)).Content.ReadAsStringAsync(Ct);
        Assert.Contains("You signed in again", owner);
        Assert.Contains(DecisionHttp.Rationale, owner);
    }

    [Fact]
    public async Task APR_03_re_authentication_as_a_different_oid_fails_closed_and_the_cookie_is_unchanged()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea5b");
        var authorize = await StaleDecideAsync(driver, item);
        var before = driver.GetCookie(ReviewerCookie.CookieName);

        var callback = await CompleteCallbackAsync(factory, driver, authorize, "fresh-other-reviewer");

        Assert.Equal(ReviewerOidc.FailedPath, callback.Headers.Location?.OriginalString);
        Assert.Equal(before, driver.GetCookie(ReviewerCookie.CookieName));
        Assert.Equal("claimed", await ItemState(db, item));
    }

    [Fact]
    public async Task APR_03_session_with_fresh_auth_time_decides_directly_without_a_re_challenge()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea6", "fresh");

        var decide = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form());

        Assert.Equal(HttpStatusCode.Redirect, decide.StatusCode);
        Assert.Equal(UiHttp.ItemPath(item), decide.Headers.Location?.OriginalString);
        Assert.Equal("decided", await ItemState(db, item));
    }

    [Fact]
    public async Task APR_03_stale_decide_with_an_invalid_request_stores_nothing_and_does_not_re_challenge()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea6b");

        var response = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide",
            new Dictionary<string, string> { ["decision"] = "approve", ["rationale"] = "   " });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(UiHttp.ItemPath(item), response.Headers.Location?.OriginalString);
        Assert.Equal("claimed", await ItemState(db, item));
    }

    [Fact]
    public async Task APR_03_rationale_and_tokens_never_reach_urls_html_cookies_or_logs()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = NewFactory(db);
        using var driver = new OidcFlowDriver(factory);
        var (item, _) = await SeedClaimedAsync(factory, db, driver, "rea7");

        var stale = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form());
        var authorize = stale.Headers.Location!;
        var callback = await CompleteCallbackAsync(factory, driver, authorize, "fresh");
        var resumed = await driver.GetAsync(callback.Headers.Location!.OriginalString);
        var decided = await driver.PostWithAntiforgeryAsync(UiHttp.ItemPath(item), UiHttp.ItemPath(item) + "?handler=Decide", Form());
        var followed = await driver.GetAsync(decided.Headers.Location!.OriginalString);

        var scan = new SentinelScan();
        scan.AddSource("authorize url", authorize.ToString());
        scan.AddSource("callback location", callback.Headers.Location!.OriginalString);
        scan.AddSource("decided location", decided.Headers.Location!.OriginalString);
        scan.AddSource("logs", factory.Logs.Text);
        scan.AddSource("cookies", string.Join("\n", driver.Cookies.Select(c => c.Key + "=" + c.Value)));
        await scan.AddResponseAsync("stale post", stale);
        await scan.AddResponseAsync("callback", callback);
        await scan.AddResponseAsync("followed", followed);
        scan.AssertAbsent(DecisionHttp.Rationale);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);

        var tokens = new SentinelScan();
        tokens.AddSource("logs", factory.Logs.Text);
        tokens.AddSource("cookies", string.Join("\n", driver.Cookies.Select(c => c.Key + "=" + c.Value)));
        tokens.AddSource("followed", await followed.Content.ReadAsStringAsync(Ct));
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(factory.Scenario.LastIdToken)) { secrets.Add(factory.Scenario.LastIdToken); }
        if (!string.IsNullOrEmpty(factory.Scenario.LastAccessToken)) { secrets.Add(factory.Scenario.LastAccessToken); }
        Assert.NotEmpty(secrets);
        tokens.AssertAbsent(secrets.ToArray());
    }
}
