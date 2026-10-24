using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;
using Olive.Integrations.Entra;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class DecisionTimeRoleTests(PostgresFixture fixture)
{
    private const string Role = "role:case_supervisor";

    // Wraps the fixture source; once Strip is set the named reviewer no longer holds the role.
    private sealed class RoleRevokingSource(IReviewerIdentitySource inner, string reviewerId) : IReviewerIdentitySource
    {
        public volatile bool Strip;

        public ReviewerIdentity? Find(string id)
        {
            var found = inner.Find(id);
            return found is not null && Strip && id == reviewerId
                ? found with { Groups = found.Groups.Where(g => g != Role).ToArray() }
                : found;
        }

        public IReadOnlyList<ReviewerIdentity> All() => inner.All();
    }

    [Fact]
    public async Task APR_03_stub_role_removed_after_claim_gives_403_role_not_held_and_writes_no_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var source = new RoleRevokingSource(FixtureReviewerIdentitySource.CreateDefault(), "alpha-sup-1");
        await using var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<IReviewerIdentitySource>(source));
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "apr3");
        using var ui = await factory.SignInDevAsync("alpha-sup-1");
        await UiHttp.FollowAsync(ui, await UiHttp.PostAsync(ui, item, "Claim"));
        var token = await OliveApiFactory.ScrapeAntiforgeryTokenAsync(ui, UiHttp.ItemPath(item));
        var eventsBefore = await DecisionHttp.Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i", ("i", item));
        var claimedBy = await DecisionHttp.Scalar<string>(db, "select claimed_by from queue_item where item_id = @i", ("i", item));

        source.Strip = true;
        var response = await ui.PostAsync(UiHttp.ItemPath(item) + "?handler=Decide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["decision"] = "approve",
            ["rationale"] = DecisionHttp.Rationale,
            ["__RequestVerificationToken"] = token,
        }), UiHttp.Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(UiHttp.Ct);
        Assert.Contains("role_not_held", body);
        Assert.Contains("Reason code", body);
        Assert.DoesNotContain(DecisionHttp.Rationale, body);
        Assert.Equal("claimed", await DecisionHttp.Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(claimedBy, await DecisionHttp.Scalar<string>(db, "select claimed_by from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(eventsBefore, await DecisionHttp.Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task APR_03_non_role_reviewer_sees_refs_only_and_no_payload_values()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, _) = await DecisionHttp.SubmitAsync(factory, db, "apr3b");
        using var ui = await factory.SignInDevAsync("alpha-norole");

        var html = await ui.GetStringAsync(UiHttp.ItemPath(item), UiHttp.Ct);
        Assert.Contains("You can see its references only.", html);
        Assert.DoesNotContain("payload://", html);
        Assert.DoesNotContain("party://", html);
    }
}
