using System.Net;
using System.Text.RegularExpressions;

namespace Olive.Tests;

// IRQ-02 / D-03 / D-10 / D-11: the Phase 7 reviewer pages serve identity_review items (refs only, kind-driven decisions, full history).
[Trait("Category", "Integration")]
public sealed class IdentityReviewPagesTests(PostgresFixture fixture)
{
    private const string Sentinel = "IRQ-PAGE-RATIONALE-SENTINEL-8812";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Form(string decision, string rationale) => new() { ["decision"] = decision, ["rationale"] = rationale };

    [Fact]
    public async Task IRQ_02_pages_steward_claims_defers_reclaims_and_accepts_with_full_history()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        using var ui = await factory.SignInDevAsync("alpha-steward-1");

        var page = await ui.GetStringAsync(UiHttp.ItemPath(item), Ct);
        Assert.Contains("identity_review", page);
        Assert.Contains("candidate_pair", page);
        Assert.Contains("Producer ref", page);
        Assert.Contains("Ruleset version", page);
        Assert.Contains("test-ruleset", page);
        Assert.Contains("Evidence comes from bluto's evidence endpoint under your own credential. Olive does not show or fetch it.", page);
        Assert.DoesNotContain("href=\"http", page);
        Assert.DoesNotContain("http://", page);
        Assert.DoesNotContain("https://", page);

        Assert.Contains("Done: you claimed", await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Claim")));
        var claimed = await ui.GetStringAsync(UiHttp.ItemPath(item), Ct);
        Assert.Contains("value=\"accept\"", claimed);
        Assert.Contains("value=\"reject\"", claimed);
        Assert.Contains("value=\"defer\"", claimed);
        Assert.DoesNotContain("value=\"approve\"", claimed);
        Assert.Contains("Do not include names, dates, identifiers or other record values", claimed);

        var deferred = await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Decide", Form("defer", Sentinel)));
        Assert.Contains("Done: deferred.", deferred);
        Assert.DoesNotContain(Sentinel, deferred);
        Assert.Equal("open", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));

        Assert.Contains("Done: you claimed", await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Claim")));
        var accepted = await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Decide", Form("accept", "Accepted after review.")));
        Assert.Contains("Done: accepted.", accepted);
        Assert.Equal("decided", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));

        var audit = await ui.GetStringAsync(UiHttp.ItemPath(item) + "/audit", Ct);
        foreach (var evt in new[] { "claimed", "deferred", "decided" })
        {
            Assert.Contains("<td>" + evt + "</td>", audit);
        }

        Assert.Equal(4, Regex.Matches(audit, "<td class=\"mono\">").Count);
        Assert.Contains("<h2>Decision</h2>", audit);
        Assert.Contains("<dd>accept</dd>", audit);
        Assert.DoesNotContain("Accepted after review.", audit);
        Assert.Contains(Sentinel, audit);
    }

    [Fact]
    public async Task IRQ_02_pages_non_steward_sees_refs_only_role_notice_and_no_rationale_in_audit()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        using var steward = await factory.SignInDevAsync("alpha-steward-1");
        await UiHttp.FollowAsync(steward, await UiHttp.PostAsync(steward, item, "Claim"));
        await UiHttp.FollowAsync(steward, await UiHttp.PostAsync(steward, item, "Decide", Form("defer", Sentinel)));

        using var sup = await factory.SignInDevAsync("alpha-sup-1");
        var page = await sup.GetStringAsync(UiHttp.ItemPath(item), Ct);
        var notice = page.IndexOf("You do not hold the role needed to decide this item. You can see its references only.", StringComparison.Ordinal);
        Assert.True(notice > 0);
        Assert.True(notice < page.IndexOf("Claim item", StringComparison.Ordinal));
        Assert.DoesNotContain(Sentinel, page);

        var audit = await sup.GetStringAsync(UiHttp.ItemPath(item) + "/audit", Ct);
        Assert.Contains("<td>deferred</td>", audit);
        Assert.DoesNotContain(Sentinel, audit);
        Assert.DoesNotContain(Sentinel, await sup.GetStringAsync("/review", Ct));
    }

    [Fact]
    public async Task IRQ_02_pages_list_shows_both_kinds_with_badges_and_filters()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (action, _) = await DecisionHttp.SubmitAsync(factory, db, "irq02list");
        var identity = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        using var sup = await factory.SignInDevAsync("alpha-sup-1");

        var all = await sup.GetStringAsync("/review", Ct);
        Assert.Contains("<span class=\"badge\">action_approval</span>", all);
        Assert.Contains("<span class=\"badge\">identity_review</span>", all);
        Assert.Contains(identity.ToString(), all);
        Assert.Contains(action.ToString(), all);

        var filtered = await sup.GetStringAsync("/review?kind=identity_review", Ct);
        Assert.Contains(identity.ToString(), filtered);
        Assert.DoesNotContain(action.ToString(), filtered);
    }

    [Fact]
    public async Task IRQ_02_pages_withdrawn_item_decide_flashes_item_withdrawn()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid(), state: "withdrawn", withdrawnReason: "producer_withdrawn");
        using var ui = await factory.SignInDevAsync("alpha-steward-1");

        var html = await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Decide", Form("accept", "Reason text.")));
        Assert.Contains("the producer withdrew this item", html);
    }

    [Fact]
    public async Task IRQ_02_pages_blank_rationale_flashes_invalid_request_and_missing_token_is_rejected()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        using var ui = await factory.SignInDevAsync("alpha-steward-1");
        await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Claim"));

        var noToken = await UiHttp.PostAsync(ui, item, "Decide", Form("defer", "Reason."), withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var blank = await UiHttp.PostAsync(ui, item, "Decide", Form("defer", ""));
        Assert.Equal(HttpStatusCode.Redirect, blank.StatusCode);
        Assert.Contains("choose a decision and enter a rationale", await UiHttp.FollowAsync(ui, blank));
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task IRQ_02_pages_role_refusal_is_403_without_prg_and_no_echo()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var item = await db.SeedIdentityItemAsync(TestPaths.TenantAlpha, Guid.NewGuid());
        using var sup = await factory.SignInDevAsync("alpha-sup-1");
        await UiHttp.FollowAsync(sup, await UiHttp.PostAsync(sup, item, "Claim"));

        var post = await UiHttp.PostAsync(sup, item, "Decide", Form("accept", Sentinel));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.DoesNotContain(Sentinel, await post.Content.ReadAsStringAsync(Ct));
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }
}
