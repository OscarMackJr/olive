using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueDecisionStoreTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Rationale = "RATIONALE-SENTINEL-7731";

    private static string Ts(DateTimeOffset t) => "'" + t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "+00'";

    private static async Task<(Guid Proposal, Guid Item, PostgresQueueStoreHandle Store)> Claimed(TestDatabase db, string run, string reviewer = "rev-1")
    {
        var (proposal, item) = await QueueClaimTests.SeedAwaiting(db, Alpha, run);
        var store = new PostgresQueueStoreHandle(QueueClaimTests.Store(db));
        Assert.IsType<ClaimResult.Claimed>(await store.Inner.ClaimAsync(Alpha, item, reviewer, Now, Ct));
        return (proposal, item, store);
    }

    // Thin alias so tuples stay readable.
    private sealed record PostgresQueueStoreHandle(Olive.Persistence.PostgresQueueStore Inner)
    {
        public Task<DecideResult> Decide(Guid item, string actor, string decision, DateTimeOffset? at = null, string rationale = Rationale, bool verified = true) =>
            Inner.DecideAsync(Alpha, item, actor, "stub", decision, rationale, verified, at ?? Now, Ct);
    }

    private static Task<long> EventCount(TestDatabase db, Guid item, string type) =>
        QueueClaimTests.Scalar<long>(db, Alpha, "select count(*) from queue_item_event where item_id = @i and event_type = @t", ("i", item), ("t", type))!;

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    public async Task APR_04_decision_updates_item_and_proposal_in_one_transaction(string decision, string proposalState)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (proposal, item, store) = await Claimed(db, "d-" + decision);

        var decided = Assert.IsType<DecideResult.Decided>(await store.Decide(item, "rev-1", decision));

        Assert.Equal("decided", decided.Item.State);
        Assert.Equal(decision, decided.Item.Decision);
        Assert.Equal("rev-1", decided.Item.DecidedBy);
        Assert.Equal(Now, decided.Item.DecidedAt);
        Assert.Equal("stub", decided.Item.AuthSource);
        Assert.Equal(Rationale, await QueueClaimTests.Scalar<string>(db, Alpha, "select rationale from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(proposalState, await QueueClaimTests.Scalar<string>(db, Alpha, "select state from proposal where proposal_id = @p", ("p", proposal)));
        Assert.Equal(1L, await QueueClaimTests.Scalar<long>(db, Alpha,
            "select count(*) from proposal_event where proposal_id = @p and event_type = 'transition' and from_state = 'awaiting_approval' and to_state = @s and actor = 'rev-1'", ("p", proposal), ("s", proposalState)));
        Assert.Equal(1L, await EventCount(db, item, "decided"));
        Assert.Equal(0L, await QueueClaimTests.Scalar<long>(db, Alpha,
            "select (select count(*) from proposal_event where detail::text like '%' || @r || '%') + (select count(*) from queue_item_event where detail::text like '%' || @r || '%')", ("r", Rationale)));
    }

    [Fact]
    public async Task APR_04_proposal_cas_miss_rolls_back_the_item_update()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (proposal, item, store) = await Claimed(db, "rb");
        await db.AdminExecAsync($"update proposal set state = 'approved' where proposal_id = '{proposal}'");

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Decide(item, "rev-1", "approve"));

        Assert.Equal("claimed", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(0L, await EventCount(db, item, "decided"));
    }

    [Fact]
    public async Task QUE_08_same_decision_is_a_no_op_different_conflicts_and_other_actor_is_refused()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item, store) = await Claimed(db, "idem");
        Assert.IsType<DecideResult.Decided>(await store.Decide(item, "rev-1", "approve"));

        var again = Assert.IsType<DecideResult.AlreadyDecidedSame>(await store.Decide(item, "rev-1", "approve", Now.AddMinutes(5)));
        Assert.Equal("approve", again.Item.Decision);
        Assert.Equal(Now, again.Item.DecidedAt);
        Assert.IsType<DecideResult.Conflict>(await store.Decide(item, "rev-1", "reject"));
        Assert.IsType<DecideResult.NotClaimant>(await store.Decide(item, "rev-2", "approve"));
        Assert.Equal(1L, await EventCount(db, item, "decided"));
    }

    [Fact]
    public async Task QUE_08_parallel_identical_decisions_give_one_decided_and_one_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item, store) = await Claimed(db, "par");

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.Decide(item, "rev-1", "approve")));

        Assert.Single(results.OfType<DecideResult.Decided>());
        Assert.Single(results.OfType<DecideResult.AlreadyDecidedSame>());
        Assert.Equal(1L, await EventCount(db, item, "decided"));
    }

    [Fact]
    public async Task QUE_07_claimant_rules_and_argument_guards()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, open) = await QueueClaimTests.SeedAwaiting(db, Alpha, "cl-open");
        var (_, item, store) = await Claimed(db, "cl");

        Assert.IsType<DecideResult.NotClaimed>(await store.Decide(open, "rev-1", "approve"));
        Assert.IsType<DecideResult.NotClaimant>(await store.Decide(item, "rev-2", "approve"));
        Assert.IsType<DecideResult.NotFound>(await store.Decide(Guid.NewGuid(), "rev-1", "approve"));
        Assert.IsType<DecideResult.NotFound>(await store.Inner.DecideAsync(Beta, item, "rev-1", "stub", "approve", Rationale, true, Now, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Decide(item, " ", "approve"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Decide(item, "rev-1", "approve", rationale: "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Decide(item, "rev-1", "defer"));
        Assert.Equal("claimed", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
    }

    [Fact]
    public async Task APR_02_CR_01_unverified_approve_on_a_claim_that_landed_after_the_pre_read_is_not_applied()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (proposal, item, store) = await Claimed(db, "cr01");

        Assert.IsType<DecideResult.NotClaimed>(await store.Decide(item, "rev-1", "approve", verified: false));

        Assert.Equal("claimed", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("awaiting_approval", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from proposal where proposal_id = @p", ("p", proposal)));
        Assert.Equal(0L, await EventCount(db, item, "decided"));

        // The client retries and the identity check then runs, so the verified approve succeeds.
        Assert.IsType<DecideResult.Decided>(await store.Decide(item, "rev-1", "approve", verified: true));
    }

    [Fact]
    public async Task APR_02_CR_01_unverified_reject_is_still_applied()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item, store) = await Claimed(db, "cr01r");
        Assert.IsType<DecideResult.Decided>(await store.Decide(item, "rev-1", "reject", verified: false));
    }

    [Fact]
    public async Task POL_08_expired_claimed_item_commits_expiry_then_returns_proposal_expired()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (proposal, item, store) = await Claimed(db, "exp");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now.AddMinutes(1))} where item_id = '{item}'");
        var later = Now.AddMinutes(2);

        Assert.IsType<DecideResult.ProposalExpired>(await store.Decide(item, "rev-1", "approve", later));

        Assert.Equal("expired", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from proposal where proposal_id = @p", ("p", proposal)));
        Assert.Equal("withdrawn", await QueueClaimTests.Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await EventCount(db, item, "claimed"));
        Assert.Equal(1L, await EventCount(db, item, "withdrawn"));
        Assert.IsType<DecideResult.ProposalExpired>(await store.Decide(item, "rev-1", "approve", later.AddHours(1)));
        Assert.Equal(1L, await EventCount(db, item, "withdrawn"));
    }

    [Fact]
    public async Task POL_08_decision_made_before_expiry_stays_valid_afterwards()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item, store) = await Claimed(db, "pre");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now.AddMinutes(10))} where item_id = '{item}'");
        Assert.IsType<DecideResult.Decided>(await store.Decide(item, "rev-1", "approve", Now));

        Assert.IsType<DecideResult.AlreadyDecidedSame>(await store.Decide(item, "rev-1", "approve", Now.AddHours(1)));
        Assert.Equal(0L, await EventCount(db, item, "withdrawn"));
    }

    [Fact]
    public async Task QUE_07_withdrawn_item_that_is_not_expiry_is_closed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item, store) = await Claimed(db, "wd");
        await db.AdminExecAsync($"update queue_item set state = 'withdrawn', withdrawn_reason = 'manual' where item_id = '{item}'");

        Assert.IsType<DecideResult.Closed>(await store.Decide(item, "rev-1", "approve"));
    }
}
