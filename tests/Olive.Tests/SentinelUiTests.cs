using System.Net;
using System.Text.Json;

namespace Olive.Tests;

// D-20 / IDN-08 extension: every page type, as a supervisor, carries the strict CSP and no-store, and no secret reaches a body,
// a header, a cookie or a log. Drives the dev picker and the real OpenIdConnect handler against the mock IdP.
[Trait("Category", "Integration")]
public sealed class SentinelUiTests(PostgresFixture pg, MockOidcFixture mock) : IClassFixture<MockOidcFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ParameterSentinel = "payload://PARAM-SENTINEL-4471";

    // One captured response: the body is read once so it can be scanned and asserted on.
    private sealed record Captured(string Name, string Path, HttpStatusCode Status, string Headers, string Body, string? Csp, string? CacheControl, bool ShowsDraftOrParameters);

    private static async Task<Captured> CaptureAsync(string name, string path, HttpResponseMessage response, bool showsDraftOrParameters = false)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        var headers = response.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value))
            .Concat(response.Content.Headers.Select(h => h.Key + ": " + string.Join(",", h.Value)))
            .Append("Location: " + response.Headers.Location);
        return new Captured(name, path, response.StatusCode, string.Join("\n", headers),
            body,
            response.Headers.TryGetValues("Content-Security-Policy", out var csp) ? string.Join(" | ", csp) : null,
            response.Headers.TryGetValues("Cache-Control", out var cc) ? string.Join(",", cc) : null,
            showsDraftOrParameters);
    }

    private static async Task<(Guid Item, Guid Proposal)> SubmitAsync(OliveApiFactory factory, TestDatabase db, string run)
    {
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor(run).Replace("payload://n1", ParameterSentinel, StringComparison.Ordinal);
        var post = await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var proposal = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("proposal_id").GetGuid();
        await db.AdminExecAsync($"update proposal set on_behalf_of = 'analyst.alpha@example.test' where proposal_id = '{proposal}'");
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var items = await ReviewerTenantTests.ListAsync(rev);
        return (items.Single(i => i.GetProperty("producer_ref").GetGuid() == proposal).GetProperty("item_id").GetGuid(), proposal);
    }

    [Fact]
    public async Task IDN_08_ui_every_page_type_has_the_strict_csp_no_store_and_leaks_no_secret()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, mockOidc: mock);
        var captured = new List<Captured>();
        var idTokens = new List<string>();
        var codes = new List<string>();

        // Dev picker session: list, item (open, claimed), audit, 404.
        using var dev = new OidcFlowDriver(factory);
        captured.Add(await CaptureAsync("picker", "/dev/sign-in", await dev.GetAsync("/dev/sign-in")));
        captured.Add(await CaptureAsync("picker post", "/dev/sign-in", await dev.PostWithAntiforgeryAsync("/dev/sign-in", "/dev/sign-in",
            new Dictionary<string, string> { ["reviewerId"] = "alpha-sup-1", ["returnUrl"] = "" })));
        var (itemA, _) = await SubmitAsync(factory, db, "sentinel-ui-a");
        captured.Add(await CaptureAsync("list", "/review", await dev.GetAsync("/review")));
        captured.Add(await CaptureAsync("item open", UiHttp.ItemPath(itemA), await dev.GetAsync(UiHttp.ItemPath(itemA)), showsDraftOrParameters: true));
        captured.Add(await CaptureAsync("claim post", UiHttp.ItemPath(itemA), await dev.PostWithAntiforgeryAsync(UiHttp.ItemPath(itemA), UiHttp.ItemPath(itemA) + "?handler=Claim")));
        captured.Add(await CaptureAsync("item claimed", UiHttp.ItemPath(itemA), await dev.GetAsync(UiHttp.ItemPath(itemA)), showsDraftOrParameters: true));
        captured.Add(await CaptureAsync("audit", UiHttp.ItemPath(itemA) + "/audit", await dev.GetAsync(UiHttp.ItemPath(itemA) + "/audit"), showsDraftOrParameters: true));
        captured.Add(await CaptureAsync("404", "/review/items/missing", await dev.GetAsync(UiHttp.ItemPath(Guid.NewGuid()))));
        captured.Add(await CaptureAsync("stylesheet", "/olive.css", await dev.GetAsync("/olive.css")));

        // 403 pages: role not held (dev), no tenant (dev), groups overage (OIDC).
        using (var other = new OidcFlowDriver(factory))
        {
            await other.PostWithAntiforgeryAsync("/dev/sign-in", "/dev/sign-in", new Dictionary<string, string> { ["reviewerId"] = "no-tenant", ["returnUrl"] = "" });
            var forbidden = await CaptureAsync("403 no_reviewer_tenant", "/review", await other.GetAsync("/review"));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
            Assert.Contains("Reason code: no_reviewer_tenant", forbidden.Body);
            captured.Add(forbidden);
        }

        // A reviewer without the decision role is not refused: the item page shows references only (APR-03).
        using (var norole = new OidcFlowDriver(factory))
        {
            await norole.PostWithAntiforgeryAsync("/dev/sign-in", "/dev/sign-in", new Dictionary<string, string> { ["reviewerId"] = "alpha-norole", ["returnUrl"] = "" });
            var restricted = await CaptureAsync("role_not_held item", UiHttp.ItemPath(itemA), await norole.GetAsync(UiHttp.ItemPath(itemA)), showsDraftOrParameters: true);
            Assert.Equal(HttpStatusCode.OK, restricted.Status);
            Assert.Contains("You can see its references only.", restricted.Body);
            captured.Add(restricted);
        }

        using var overage = new OidcFlowDriver(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await overage.SignInAsync("overage")).StatusCode);
        idTokens.Add(factory.Scenario.LastIdToken!);
        var overagePage = await CaptureAsync("403 groups_overage", "/review", await overage.GetAsync("/review"));
        Assert.Equal(HttpStatusCode.Forbidden, overagePage.Status);
        Assert.Contains("Reason code: groups_overage", overagePage.Body);
        captured.Add(overagePage);

        using var absent = new OidcFlowDriver(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await absent.SignInAsync("role-absent")).StatusCode);
        idTokens.Add(factory.Scenario.LastIdToken!);
        var absentPage = await CaptureAsync("oidc role absent item", UiHttp.ItemPath(itemA), await absent.GetAsync(UiHttp.ItemPath(itemA)), showsDraftOrParameters: true);
        Assert.Equal(HttpStatusCode.OK, absentPage.Status);
        Assert.Contains("You can see its references only.", absentPage.Body);
        captured.Add(absentPage);

        // Static pages, including the failure page reached through a tampered callback.
        using var failing = new OidcFlowDriver(factory);
        var tampered = await failing.SignInAsync("role-present", "/review", q => System.Text.RegularExpressions.Regex.Replace(q, "state=[^&]+", "state=tampered"));
        Assert.Equal("/auth/failed", tampered.Headers.Location?.OriginalString);
        captured.Add(await CaptureAsync("tampered callback", "/signin-oidc", tampered));
        captured.Add(await CaptureAsync("auth failed", "/auth/failed", await failing.GetAsync("/auth/failed")));
        captured.Add(await CaptureAsync("signed out", "/signed-out", await failing.GetAsync("/signed-out")));

        // Full OIDC re-challenge decide with the rationale sentinel.
        var (itemB, proposalB) = await SubmitAsync(factory, db, "sentinel-ui-b");
        using var oidc = new OidcFlowDriver(factory);
        factory.Scenario.Set("role-present");
        var authorizeUrl = await oidc.GetAuthorizeUrlAsync("/review");
        var callbackUrl = await oidc.FollowAuthorizeAsync(authorizeUrl);
        codes.Add(System.Web.HttpUtility.ParseQueryString(new Uri("https://localhost" + callbackUrl).Query)["code"]!);
        captured.Add(await CaptureAsync("oidc callback", "/signin-oidc", await oidc.GetAsync(callbackUrl)));
        idTokens.Add(factory.Scenario.LastIdToken!);
        captured.Add(await CaptureAsync("oidc list", "/review", await oidc.GetAsync("/review")));
        captured.Add(await CaptureAsync("oidc claim", UiHttp.ItemPath(itemB), await oidc.PostWithAntiforgeryAsync(UiHttp.ItemPath(itemB), UiHttp.ItemPath(itemB) + "?handler=Claim")));

        var staleDecide = await oidc.PostWithAntiforgeryAsync(UiHttp.ItemPath(itemB), UiHttp.ItemPath(itemB) + "?handler=Decide",
            new Dictionary<string, string> { ["decision"] = "approve", ["rationale"] = DecisionHttp.Rationale });
        Assert.Equal(HttpStatusCode.Redirect, staleDecide.StatusCode);
        var authorize = staleDecide.Headers.Location!;
        captured.Add(await CaptureAsync("re-challenge redirect", UiHttp.ItemPath(itemB), staleDecide));

        factory.Scenario.Set("fresh");
        using (var mockClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }))
        {
            var login = await mockClient.PostAsync(authorize, new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "reviewer@example.test" }), Ct);
            Assert.Equal(HttpStatusCode.Found, login.StatusCode);
            var reauthCallback = login.Headers.Location!.PathAndQuery;
            codes.Add(System.Web.HttpUtility.ParseQueryString(login.Headers.Location.Query)["code"]!);
            var callback = await oidc.GetAsync(reauthCallback);
            idTokens.Add(factory.Scenario.LastIdToken!);
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            var resume = callback.Headers.Location!.OriginalString;
            captured.Add(await CaptureAsync("re-auth callback", "/signin-oidc", callback));
            // The restored draft shows the reviewer's own rationale back to them by design (07-05); every other page must not.
            captured.Add(await CaptureAsync("resume", resume, await oidc.GetAsync(resume), showsDraftOrParameters: true));
        }

        var decide = await oidc.PostWithAntiforgeryAsync(UiHttp.ItemPath(itemB), UiHttp.ItemPath(itemB) + "?handler=Decide",
            new Dictionary<string, string> { ["decision"] = "approve", ["rationale"] = DecisionHttp.Rationale });
        Assert.Equal(HttpStatusCode.Redirect, decide.StatusCode);
        captured.Add(await CaptureAsync("decide post", UiHttp.ItemPath(itemB), decide));
        captured.Add(await CaptureAsync("item decided", UiHttp.ItemPath(itemB), await oidc.GetAsync(UiHttp.ItemPath(itemB)), showsDraftOrParameters: true));
        captured.Add(await CaptureAsync("audit decided", UiHttp.ItemPath(itemB) + "/audit", await oidc.GetAsync(UiHttp.ItemPath(itemB) + "/audit"), showsDraftOrParameters: true));
        Assert.Contains(DecisionHttp.Rationale, captured[^1].Body); // the audit chain shows the recorded rationale to the reviewer by design (UI-05)
        captured.Add(await CaptureAsync("sign-out", "/auth/sign-out", await oidc.GetAsync("/auth/sign-out")));
        Assert.Equal("decided", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", itemB)));
        Assert.Equal("executed", await DecisionHttp.ProposalState(db, proposalB));

        Assert.True(captured.Count >= 12, "scanned " + captured.Count + " responses");
        Assert.True(captured.Select(c => c.Name).Distinct().Count() >= 12);

        // Headers on every response.
        foreach (var c in captured)
        {
            Assert.True(c.Csp is not null, c.Name + " has no Content-Security-Policy");
            Assert.Equal(Olive.Host.Startup.SecurityHeaders.ContentSecurityPolicy, c.Csp);
            foreach (var needle in new[] { "default-src 'self'", "frame-ancestors 'none'", "base-uri 'self'", "object-src 'none'" }) { Assert.Contains(needle, c.Csp); }
            Assert.DoesNotContain("form-action", c.Csp);
            Assert.DoesNotContain("unsafe-inline", c.Csp);
            if (new[] { "/review", "/dev", "/auth", "/signed-out", "/signin-oidc" }.Any(p => c.Path.StartsWith(p, StringComparison.Ordinal)))
            {
                Assert.True(c.CacheControl?.Contains("no-store", StringComparison.Ordinal) == true, c.Name + " is not no-store");
            }
        }

        // Cookies the browser would hold: every jar plus every Set-Cookie header (the flash TempData cookie is scanned as an opaque value).
        var cookieText = string.Join("\n", new[] { dev, overage, absent, failing, oidc }.SelectMany(d => d.Cookies).Select(c => c.Key + "=" + c.Value));

        var secrets = new List<string> { factory.OidcClientSecret, ApiProposalTests.AlphaKey };
        secrets.AddRange(idTokens.Where(t => !string.IsNullOrEmpty(t)));
        secrets.AddRange(codes.Where(t => !string.IsNullOrEmpty(t)));

        var scan = new SentinelScan();
        foreach (var c in captured) { scan.AddSource(c.Name + " headers", c.Headers); scan.AddSource(c.Name + " body", c.Body); }
        scan.AddSource("cookies", cookieText);
        scan.AddSource("logs", factory.Logs.Text);
        scan.AssertAbsent(secrets.ToArray());

        // The rationale never appears except where the reviewer's own restored draft is echoed back and on the audit chain (which also shows parameter references to the role holder); the parameter reference is
        // shown only on the item pages that render the proposal.
        var rationaleScan = new SentinelScan();
        var parameterScan = new SentinelScan();
        foreach (var c in captured.Where(c => !c.ShowsDraftOrParameters))
        {
            rationaleScan.AddSource(c.Name + " body", c.Body);
            parameterScan.AddSource(c.Name + " body", c.Body);
        }

        foreach (var c in captured)
        {
            rationaleScan.AddSource(c.Name + " headers", c.Headers);
            parameterScan.AddSource(c.Name + " headers", c.Headers);
        }

        foreach (var s in new[] { rationaleScan, parameterScan })
        {
            s.AddSource("cookies", cookieText);
            s.AddSource("logs", factory.Logs.Text);
        }

        rationaleScan.AssertAbsent(DecisionHttp.Rationale);
        parameterScan.AssertAbsent(ParameterSentinel);
        foreach (var c in captured.Where(c => c.ShowsDraftOrParameters && c.Name is not ("resume" or "audit decided" or "audit"))) { Assert.DoesNotContain(DecisionHttp.Rationale, c.Body); }
    }
}
