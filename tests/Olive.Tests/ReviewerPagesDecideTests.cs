using System.Net;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ReviewerPagesDecideTests(PostgresFixture fixture)
{
    private static Dictionary<string, string> Form(string decision, string rationale) => new() { ["decision"] = decision, ["rationale"] = rationale };

    private static async Task<(HttpClient Ui, Guid Item, Guid Proposal)> ClaimedAsync(OliveApiFactory factory, TestDatabase db, string run, string reviewer = "alpha-sup-1", string onBehalfOf = "analyst.alpha@example.test")
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, run, onBehalfOf);
        var ui = await factory.SignInDevAsync(reviewer);
        await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Claim"));
        return (ui, item, proposal);
    }

    [Fact]
    public async Task UI_04_approve_with_rationale_decides_and_executes_exactly_once_in_simulation()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (ui, item, proposal) = await ClaimedAsync(factory, db, "ui4a");
        using var _ = ui;

        var post = await UiHttp.PostAsync(ui, item, "Decide", Form("approve", DecisionHttp.Rationale));
        var html = await UiHttp.FollowAsync(ui, post);
        Assert.Contains("Done: approved.", html);
        Assert.DoesNotContain("Submit decision", html);

        Assert.Equal("decided", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("executed", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and phase = 'finished' and execution_mode = 'simulated'", ("p", proposal)));

        var scan = await SentinelScan.CollectAsync(factory, db, post);
        scan.AddSource("html", html);
        scan.AssertAbsent(DecisionHttp.Rationale);
    }

    [Fact]
    public async Task UI_04_reject_records_no_execution()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (ui, item, proposal) = await ClaimedAsync(factory, db, "ui4b");
        using var _ = ui;

        var html = await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Decide", Form("reject", DecisionHttp.Rationale)));
        Assert.Contains("Done: rejected.", html);
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p", ("p", proposal)));
        Assert.NotEqual("executed", await DecisionHttp.ProposalState(db, proposal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOO-LONG")]
    public async Task UI_04_empty_or_oversized_rationale_flashes_invalid_request_and_changes_nothing(string kind)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (ui, item, proposal) = await ClaimedAsync(factory, db, "ui4c" + kind.Length);
        using var _ = ui;
        var rationale = kind == "" ? "" : new string('r', 4001);

        var post = await UiHttp.PostAsync(ui, item, "Decide", Form("approve", rationale));
        var html = await UiHttp.FollowAsync(ui, post);
        Assert.Contains("choose Approve or Reject and enter a rationale of up to 4000 characters", html);
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task UI_04_self_approval_is_refused_with_static_copy_and_the_item_stays_claimed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (ui, item, proposal) = await ClaimedAsync(factory, db, "ui4d", "alpha-self");
        using var _ = ui;

        var post = await UiHttp.PostAsync(ui, item, "Decide", Form("approve", DecisionHttp.Rationale));
        var html = await UiHttp.FollowAsync(ui, post);
        Assert.Contains("you cannot approve a proposal made on your own behalf", html);
        Assert.DoesNotContain(DecisionHttp.Rationale, html);
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task UI_04_decision_form_is_only_shown_to_the_claimant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (ui, item, _) = await ClaimedAsync(factory, db, "ui4e");
        using var _ui = ui;

        Assert.Contains("Submit decision", await ui.GetStringAsync(UiHttp.ItemPath(item), UiHttp.Ct));
        using var other = await factory.SignInDevAsync("alpha-sup-2");
        var otherHtml = await other.GetStringAsync(UiHttp.ItemPath(item), UiHttp.Ct);
        Assert.DoesNotContain("Submit decision", otherHtml);
        Assert.Contains("Claimed by another reviewer.", otherHtml);
        var forged = await UiHttp.PostAsync(other, item, "Decide", Form("approve", DecisionHttp.Rationale));
        Assert.Contains("only the reviewer who claimed this item can do that", await UiHttp.FollowAsync(other, forged));
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
    }
}
