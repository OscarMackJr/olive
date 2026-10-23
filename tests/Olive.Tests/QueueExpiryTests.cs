using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueExpiryTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Ts(DateTimeOffset t) => "'" + t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "+00'";

    private static async Task<Guid> SeedExpiring(TestDatabase db, string run, DateTimeOffset at)
    {
        var (_, item) = await QueueClaimTests.SeedAwaiting(db, Alpha, run);
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(at)} where item_id = '{item}'");
        return item;
    }

    private static async Task AssertExpiredCommitted(TestDatabase db, Guid item, params string[] itemEvents)
    {
        Assert.Equal("expired", await QueueClaimTests.Scalar<string>(db, Alpha, "select p.state from proposal p join queue_item q on q.proposal_id = p.proposal_id where q.item_id = @i", ("i", item)));
        Assert.Equal("withdrawn", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("proposal_expired", await QueueClaimTests.Scalar<string>(db, Alpha, "select withdrawn_reason from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await QueueClaimTests.Scalar<long>(db, Alpha,
            "select count(*) from proposal_event e join queue_item q on q.proposal_id = e.proposal_id where q.item_id = @i and e.event_type = 'transition' and e.from_state = 'awaiting_approval' and e.to_state = 'expired' and e.actor = 'system'", ("i", item)));
        var store = QueueClaimTests.Store(db);
        var events = await store.EventsAsync(Alpha, item, Ct);
        Assert.Equal(itemEvents, events!.Select(e => e.EventType).ToArray());
    }

    [Fact]
    public async Task QUE_10_claim_at_exactly_expires_at_commits_expiry_and_returns_proposal_expired()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await SeedExpiring(db, "x1", Expiry);
        var store = QueueClaimTests.Store(db);

        Assert.IsType<ClaimResult.ProposalExpired>(await store.ClaimAsync(Alpha, item, "rev-1", Expiry, Ct));

        await AssertExpiredCommitted(db, item, "created", "withdrawn");
        // The repeat call is stable and writes nothing more.
        Assert.IsType<ClaimResult.ProposalExpired>(await store.ClaimAsync(Alpha, item, "rev-1", Expiry.AddHours(1), Ct));
        Assert.IsType<ReleaseResult.ProposalExpired>(await store.ReleaseAsync(Alpha, item, "rev-1", Expiry.AddHours(1), Ct));
        await AssertExpiredCommitted(db, item, "created", "withdrawn");
    }

    [Fact]
    public async Task QUE_10_one_tick_before_expiry_is_claimable_and_cross_tenant_is_not_found()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await SeedExpiring(db, "x2", Expiry.AddTicks(TimeSpan.TicksPerSecond));
        var store = QueueClaimTests.Store(db);

        Assert.IsType<ClaimResult.NotFound>(await store.ClaimAsync(Beta, item, "rev-1", Expiry, Ct));
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "rev-1", Expiry, Ct));
    }

    [Fact]
    public async Task QUE_10_claimed_item_released_after_expiry_keeps_claimed_event_and_withdraws_from_claimed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await SeedExpiring(db, "x3", Expiry);
        var store = QueueClaimTests.Store(db);

        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "rev-1", Expiry.AddMinutes(-1), Ct));
        Assert.IsType<ReleaseResult.ProposalExpired>(await store.ReleaseAsync(Alpha, item, "rev-1", Expiry.AddMinutes(1), Ct));

        await AssertExpiredCommitted(db, item, "created", "claimed", "withdrawn");
        var withdrawn = (await store.EventsAsync(Alpha, item, Ct))!.Last();
        Assert.Equal("claimed", withdrawn.FromState);
        Assert.Equal("withdrawn", withdrawn.ToState);
    }

    [Fact]
    public async Task POL_08_inconsistent_proposal_state_throws_and_rolls_everything_back()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await SeedExpiring(db, "x4", Expiry);
        await db.AdminExecAsync($"update proposal set state = 'approved' where proposal_id = (select proposal_id from queue_item where item_id = '{item}')");
        var store = QueueClaimTests.Store(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClaimAsync(Alpha, item, "rev-1", Expiry, Ct));

        Assert.Equal("open", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("approved", await QueueClaimTests.Scalar<string>(db, Alpha, "select p.state from proposal p join queue_item q on q.proposal_id = p.proposal_id where q.item_id = @i", ("i", item)));
        Assert.Equal(1L, await QueueClaimTests.Scalar<long>(db, Alpha, "select count(*) from queue_item_event where item_id = @i", ("i", item)));
    }

    private static async Task<(OliveApiFactory Factory, ManualTimeProvider Clock, Guid Item)> HttpSetup(TestDatabase db, string run)
    {
        var n = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, TimeSpan.Zero));
        var factory = new OliveApiFactory(db, configureServices: s => s.AddSingleton<TimeProvider>(clock));
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var body = ApiProposalTests.BodyFor(run).TrimEnd('}') + ",\"expires_in_seconds\":60}";
        Assert.Equal(HttpStatusCode.Created, (await agent.PostAsync("/v1/proposals", ApiProposalTests.Json(body), Ct)).StatusCode);
        using var rev = factory.CreateReviewerClient("alpha-sup-1");
        var item = Assert.Single(await ReviewerTenantTests.ListAsync(rev)).GetProperty("item_id").GetGuid();
        return (factory, clock, item);
    }

    private static async Task<string> ReasonOf(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    [Fact]
    public async Task POL_08_http_claim_after_expiry_is_409_proposal_expired_and_committed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (factory, clock, item) = await HttpSetup(db, "hx1");
        await using var _ = factory;
        using var rev = factory.CreateReviewerClient("alpha-sup-1");

        clock.Advance(TimeSpan.FromSeconds(60));
        var response = await rev.PostAsync($"/v1/queue/items/{item}/claim", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("proposal_expired", await ReasonOf(response));

        await AssertExpiredCommitted(db, item, "created", "withdrawn");
        Assert.Equal("proposal_expired", await ReasonOf(await rev.PostAsync($"/v1/queue/items/{item}/claim", null, Ct)));
        Assert.Empty(await ReviewerTenantTests.ListAsync(rev));
    }

    [Fact]
    public async Task D_12_http_release_after_expiry_keeps_history_in_seq_order()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (factory, clock, item) = await HttpSetup(db, "hx2");
        await using var _ = factory;
        using var rev = factory.CreateReviewerClient("alpha-sup-1");

        Assert.Equal(HttpStatusCode.OK, (await rev.PostAsync($"/v1/queue/items/{item}/claim", null, Ct)).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(61));
        var release = await rev.PostAsync($"/v1/queue/items/{item}/release", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, release.StatusCode);
        var body = await release.Content.ReadAsStringAsync(Ct);
        Assert.Contains("proposal_expired", body);
        Assert.DoesNotContain("aaaaaaaa-0000-4000-8000-000000000001", body);

        var events = await rev.GetAsync($"/v1/queue/items/{item}/events", Ct);
        using var doc = JsonDocument.Parse(await events.Content.ReadAsStringAsync(Ct));
        var list = doc.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(["created", "claimed", "withdrawn"], list.Select(e => e.GetProperty("event_type").GetString()).ToArray());
        var seqs = list.Select(e => e.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(seqs.OrderBy(x => x).ToArray(), seqs);
        Assert.Equal("proposal_expired", await ReasonOf(await rev.PostAsync($"/v1/queue/items/{item}/release", null, Ct)));
    }
}
