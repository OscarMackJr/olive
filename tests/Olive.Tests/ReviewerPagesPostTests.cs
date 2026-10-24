using System.Net;
using System.Text.RegularExpressions;

namespace Olive.Tests;

internal static partial class UiHttp
{
    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static string ItemPath(Guid item) => "/review/items/" + item;

    // POSTs a handler form with a token scraped from the item page itself.
    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid item, string handler, IDictionary<string, string>? fields = null, bool withToken = true)
    {
        var form = new Dictionary<string, string>(fields ?? new Dictionary<string, string>());
        if (withToken) { form["__RequestVerificationToken"] = await OliveApiFactory.ScrapeAntiforgeryTokenAsync(client, ItemPath(item)); }
        return await client.PostAsync(ItemPath(item) + "?handler=" + handler, new FormUrlEncodedContent(form), Ct);
    }

    // Follows the PRG redirect once and returns the page body.
    internal static async Task<string> FollowAsync(HttpClient client, HttpResponseMessage redirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var response = await client.GetAsync(redirect.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    internal static string StripTokens(string html) => Token().Replace(html, "TOKEN");

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"[^\"]+\"")]
    private static partial Regex Token();
}

[Trait("Category", "Integration")]
public sealed class ReviewerPagesPostTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UI_03_claim_without_token_is_400_and_with_token_redirects_and_flashes_once()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "ui3a");
        using var ui = await factory.SignInDevAsync("alpha-sup-1");

        var noToken = await UiHttp.PostAsync(ui, item, "Claim", withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal("open", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));

        var claim = await UiHttp.PostAsync(ui, item, "Claim");
        Assert.Equal(HttpStatusCode.Redirect, claim.StatusCode);
        Assert.Equal(UiHttp.ItemPath(item), claim.Headers.Location?.OriginalString);
        var first = await UiHttp.FollowAsync(ui, claim);
        Assert.Contains("Done: you claimed this item.", first);
        Assert.Contains("You have claimed this item.", first);
        var second = await ui.GetStringAsync(UiHttp.ItemPath(item), Ct);
        Assert.DoesNotContain("Done: you claimed this item.", second);
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task UI_03_release_by_non_claimant_and_second_claim_show_static_error_copy()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "ui3b");
        using var first = await factory.SignInDevAsync("alpha-sup-1");
        using var second = await factory.SignInDevAsync("alpha-sup-2");
        Assert.Contains("Done: you claimed", await UiHttp.FollowAsync(first, await UiHttp.PostAsync(first, item, "Claim")));

        var already = await UiHttp.FollowAsync(second, await UiHttp.PostAsync(second, item, "Claim"));
        Assert.Contains("another reviewer claimed this item first", already);

        var release = await UiHttp.FollowAsync(second, await UiHttp.PostAsync(second, item, "Release"));
        Assert.Contains("only the reviewer who claimed this item can do that", release);
        Assert.DoesNotContain("Release item", release);

        var released = await UiHttp.FollowAsync(first, await UiHttp.PostAsync(first, item, "Release"));
        Assert.Contains("Done: you released this item.", released);
        Assert.Equal("open", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task UI_03_missing_and_cross_tenant_items_give_identical_404_pages()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var betaAgent = factory.CreateKeyClient(ApiProposalTests.BetaKey);
        await ReviewerTenantTests.SubmitAsync(betaAgent, "ui3c");
        using var betaApi = factory.CreateReviewerClient("beta-sup");
        var betaItem = Assert.Single(await ReviewerTenantTests.ListAsync(betaApi)).GetProperty("item_id").GetGuid();
        using var ui = await factory.SignInDevAsync("alpha-sup-1");

        var missing = await ui.GetAsync(UiHttp.ItemPath(Guid.NewGuid()), Ct);
        var cross = await ui.GetAsync(UiHttp.ItemPath(betaItem), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        var missingBody = UiHttp.StripTokens(await missing.Content.ReadAsStringAsync(Ct));
        var crossBody = UiHttp.StripTokens(await cross.Content.ReadAsStringAsync(Ct));
        Assert.Equal(missingBody, crossBody);
        Assert.Contains("This item does not exist or is not in your tenant.", crossBody);
        Assert.DoesNotContain(betaItem.ToString(), crossBody);
    }

    [Fact]
    public async Task UI_05_role_holder_sees_encoded_payload_and_non_holder_sees_refs_only()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "ui5a");
        await db.AdminExecAsync($"update proposal set justification = '<b>x</b>' where proposal_id = '{proposal}'");

        using var holder = await factory.SignInDevAsync("alpha-sup-1");
        var html = await holder.GetStringAsync(UiHttp.ItemPath(item), Ct);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("crm.case_note@1", html);
        Assert.Contains("payload://c1", html);
        Assert.Contains("Fixture trace: this proposal's trace was verified against a test fixture", html.Replace("&#x27;", "'"));

        using var other = await factory.SignInDevAsync("alpha-norole");
        var refs = await other.GetStringAsync(UiHttp.ItemPath(item), Ct);
        Assert.Contains("You do not hold the role required to view this item's payload.", refs.Replace("&#x27;", "'"));
        Assert.DoesNotContain("payload://c1", refs);
        Assert.DoesNotContain("crm.case_note@1", refs);
        Assert.Contains("olive:proposal/", refs);
    }
}
