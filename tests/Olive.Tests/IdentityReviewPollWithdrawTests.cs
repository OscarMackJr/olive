using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Persistence;

namespace Olive.Tests;

// IRQ-03 / TC-IRQ-09 / TC-IRQ-10: the producer poll of decided identity items and the producer withdraw.
[Collection("DbAblation")]
[Trait("Category", "Integration")]
public sealed class IdentityReviewPollWithdrawTests(PostgresFixture fixture)
{
    private const string Items = "/v1/queue/items";
    private const string AlphaToken = "fixture-service-token-bluto-alpha";
    private const string BetaToken = "fixture-service-token-bluto-beta";
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProducerContext Producer(Guid tenant, string name = "bluto") =>
        new(tenant, name, "client", new Dictionary<string, IReadOnlySet<string>>());

    private static PostgresQueueStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    private static Task<T?> Scalar<T>(TestDatabase db, string sql, params (string, object)[] args) => QueueClaimTests.Scalar<T>(db, Alpha, sql, args);

    private static Task<Guid> Decided(TestDatabase db, Guid tenant, DateTimeOffset at, string decision = "accept", string producer = "bluto") =>
        db.SeedIdentityItemAsync(tenant, Guid.NewGuid(), "decided", claimedBy: "stew-1", decision: decision, decidedBy: "stew-1", decidedAt: at, producer: producer);

    // ---- service level (counting fake) ----

    private sealed class CountingStore : IQueueStore
    {
        public int PollCalls { get; private set; }
        public int WithdrawCalls { get; private set; }
        public List<QueueItemDetail> Rows { get; } = [];

        public Task<IReadOnlyList<QueueItemDetail>> ListDecidedForProducerAsync(Guid tenantId, string producer, DateTime? afterAtUtc, Guid? afterItemId, int limit, CancellationToken ct)
        {
            PollCalls++;
            return Task.FromResult<IReadOnlyList<QueueItemDetail>>(Rows);
        }

        public Task<WithdrawResult> WithdrawByProducerAsync(Guid tenantId, string producer, Guid itemId, string reason, CancellationToken ct)
        {
            WithdrawCalls++;
            return Task.FromResult<WithdrawResult>(new WithdrawResult.Withdrawn(itemId));
        }

        public Task<IReadOnlyList<QueueItemSummary>> ListAsync(Guid tenantId, string? kind, string? state, DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueItemDetail?> GetAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<QueueItemEventRecord>?> EventsAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReviewerPayload?> GetPayloadAsync(Guid tenantId, Guid itemId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClaimResult> ClaimAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReleaseResult> ReleaseAsync(Guid tenantId, Guid itemId, string actor, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, bool approveIdentityVerified, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DecideResult> DecideIdentityAsync(Guid tenantId, Guid itemId, string actor, string authSource, string decision, string rationale, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreateIdentityResult> CreateIdentityItemAsync(Guid tenantId, string producer, string kind, string subtype, Guid producerRef, string subjectRef, string payloadRef, string rulesetVersion, string bodySha256, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NeverResolver : IUserIdentityResolver
    {
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => throw new InvalidOperationException("identity check must not run");
    }

    private static (QueueService Service, CountingStore Store) Build()
    {
        var store = new CountingStore();
        var resolver = new RecordingUserIdentityResolver(new NeverResolver(), [], []);
        var check = new ApprovalIdentityCheck(new UserIdentityVerifier(resolver, new UserIdentityVerifierOptions()));
        return (new QueueService(store, QueueKindRegistry.LoadEmbedded(), check, new ManualTimeProvider(Now)), store);
    }

    [Fact]
    public async Task IRQ_03_malformed_cursor_is_invalid_and_never_reaches_the_store()
    {
        var (service, store) = Build();
        foreach (var bad in new[] { "", "not a cursor", "djE", new string('A', 200) })
        {
            Assert.IsType<PollResult.Invalid>(await service.PollDecisionsAsync(Producer(Alpha), bad, Ct));
        }

        Assert.Equal(0, store.PollCalls);
    }

    [Fact]
    public async Task IRQ_03_empty_page_returns_the_supplied_cursor_else_null()
    {
        var (service, _) = Build();
        var none = Assert.IsType<PollResult.Ok>(await service.PollDecisionsAsync(Producer(Alpha), null, Ct));
        Assert.Empty(none.Items);
        Assert.Null(none.NextCursor);

        var cursor = ProducerCursor.Encode(Now.UtcDateTime, Guid.NewGuid());
        var again = Assert.IsType<PollResult.Ok>(await service.PollDecisionsAsync(Producer(Alpha), cursor, Ct));
        Assert.Equal(cursor, again.NextCursor);
    }

    [Fact]
    public void IRQ_03_poll_item_has_exactly_the_eleven_d14_fields_and_no_reviewer_text()
    {
        var names = typeof(PollItem).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        string[] expected = ["DecidedAt", "DecidedBy", "Decision", "ItemId", "Kind", "PayloadRef", "ProducerRef", "RulesetVersion", "State", "Subtype", "SubjectRef"];
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), names);
        Assert.DoesNotContain(names, n => n.Contains("Rationale", StringComparison.OrdinalIgnoreCase) || n.Contains("ClaimedBy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IRQ_09_unknown_reason_is_invalid_and_never_reaches_the_store()
    {
        var (service, store) = Build();
        foreach (var bad in new string?[] { null, "", "Source_Changed", "because", "proposal_expired" })
        {
            Assert.IsType<WithdrawResult.InvalidRequest>(await service.WithdrawAsync(Producer(Alpha), Guid.NewGuid(), bad, Ct));
        }

        Assert.Equal(0, store.WithdrawCalls);
        foreach (var good in new[] { "superseded_by_rule", "source_changed", "ruleset_retired" })
        {
            Assert.IsType<WithdrawResult.Withdrawn>(await service.WithdrawAsync(Producer(Alpha), Guid.NewGuid(), good, Ct));
        }
    }

    // ---- store level: poll ----

    [Fact]
    public async Task IRQ_10_poll_returns_only_own_tenant_own_producer_accept_and_reject_oldest_first()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var second = await Decided(db, Alpha, Now.AddMinutes(2), "reject");
        var first = await Decided(db, Alpha, Now.AddMinutes(1), "accept");
        await Decided(db, Beta, Now.AddMinutes(1));
        await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid(), "claimed");
        await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid(), "withdrawn", withdrawnReason: "source_changed");
        var store = Store(db);

        var rows = await store.ListDecidedForProducerAsync(Alpha, "bluto", null, null, 100, Ct);

        Assert.Equal([first, second], rows.Select(r => r.ItemId).ToArray());
        Assert.Equal(["accept", "reject"], rows.Select(r => r.Decision).ToArray());
        Assert.All(rows, r => Assert.Equal("bluto", r.Producer));
        Assert.Empty(await store.ListDecidedForProducerAsync(Alpha, "other", null, null, 100, Ct));
        Assert.Single(await store.ListDecidedForProducerAsync(Beta, "bluto", null, null, 100, Ct));
    }

    [Fact]
    public async Task IRQ_10_deferred_then_open_is_absent_and_deferred_then_accepted_appears_once()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var deferredOpen = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var accepted = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        foreach (var item in new[] { deferredOpen, accepted })
        {
            Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct));
            Assert.IsType<DecideResult.Deferred>(await store.DecideIdentityAsync(Alpha, item, "stew-1", "stub", "defer", "later", Now, Ct));
        }

        Assert.Empty(await store.ListDecidedForProducerAsync(Alpha, "bluto", null, null, 100, Ct));
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, accepted, "stew-1", Now, Ct));
        Assert.IsType<DecideResult.Decided>(await store.DecideIdentityAsync(Alpha, accepted, "stew-1", "stub", "accept", "ok", Now, Ct));

        var rows = await store.ListDecidedForProducerAsync(Alpha, "bluto", null, null, 100, Ct);
        Assert.Equal(accepted, Assert.Single(rows).ItemId);
        Assert.Equal("accept", rows[0].Decision);
    }

    [Fact]
    public async Task IRQ_10_equal_decided_at_orders_deterministically_by_item_id_and_the_cursor_continues_past_it()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++) { ids.Add(await Decided(db, Alpha, Now)); }
        var store = Store(db);
        var expected = ids.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToArray();

        var all = await store.ListDecidedForProducerAsync(Alpha, "bluto", null, null, 100, Ct);
        Assert.Equal(expected, all.Select(r => r.ItemId).ToArray());

        var page = await store.ListDecidedForProducerAsync(Alpha, "bluto", null, null, 2, Ct);
        var cursor = ProducerCursor.Encode(page[^1].DecidedAt!.Value.UtcDateTime, page[^1].ItemId);
        Assert.True(ProducerCursor.TryDecode(cursor, out var at, out var id));
        var rest = await store.ListDecidedForProducerAsync(Alpha, "bluto", at, id, 100, Ct);
        Assert.Equal(expected.Skip(2).ToArray(), rest.Select(r => r.ItemId).ToArray());
    }

    [Fact]
    public async Task IRQ_10_service_pages_100_with_next_cursor_then_the_remainder()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        for (var i = 0; i < 101; i++) { await Decided(db, Alpha, Now.AddSeconds(i)); }
        var resolver = new RecordingUserIdentityResolver(new NeverResolver(), [], []);
        var service = new QueueService(Store(db), QueueKindRegistry.LoadEmbedded(), new ApprovalIdentityCheck(new UserIdentityVerifier(resolver, new UserIdentityVerifierOptions())), new ManualTimeProvider(Now));

        var page1 = Assert.IsType<PollResult.Ok>(await service.PollDecisionsAsync(Producer(Alpha), null, Ct));
        Assert.Equal(100, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);
        var page2 = Assert.IsType<PollResult.Ok>(await service.PollDecisionsAsync(Producer(Alpha), page1.NextCursor, Ct));
        Assert.Single(page2.Items);
        Assert.DoesNotContain(page2.Items[0].ItemId, page1.Items.Select(i => i.ItemId));
        var page3 = Assert.IsType<PollResult.Ok>(await service.PollDecisionsAsync(Producer(Alpha), page2.NextCursor, Ct));
        Assert.Empty(page3.Items);
        Assert.Equal(page2.NextCursor, page3.NextCursor);
    }

    [Fact]
    public async Task IRQ_10_rls_alone_hides_the_other_tenant_when_the_application_predicate_is_removed()
    {
        await using var db = await AblationDatabase.CreateAsync(fixture);
        await Decided(db, Alpha, Now);
        await Decided(db, Beta, Now);
        await using var ds = NpgsqlDataSource.Create(TestDatabase.WithMaxPoolSize(db.AppConnectionString, 1));

        var visible = await new TenantUnitOfWork(ds).ExecuteAsync(Alpha, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand("select count(*) from queue_item where producer = 'bluto' and state = 'decided'", conn, tx);
            return (long)(await cmd.ExecuteScalarAsync(Ct))!;
        }, Ct);

        Assert.Equal(1L, visible);
    }

    [Fact]
    public async Task IRQ_10_application_predicate_alone_hides_the_other_tenant_when_rls_is_disabled()
    {
        await using var db = await AblationDatabase.CreateAsync(fixture, "queue_item");
        var alphaItem = await Decided(db, Alpha, Now);
        await Decided(db, Beta, Now);

        var rows = await Store(db).ListDecidedForProducerAsync(Alpha, "bluto", null, null, 100, Ct);

        Assert.Equal(alphaItem, Assert.Single(rows).ItemId);
    }

    // ---- store level: withdraw ----

    [Fact]
    public async Task IRQ_09_withdraw_open_item_sets_state_reason_and_writes_one_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);

        var result = await store.WithdrawByProducerAsync(Alpha, "bluto", item, "source_changed", Ct);

        Assert.Equal(item, Assert.IsType<WithdrawResult.Withdrawn>(result).ItemId);
        Assert.Equal("withdrawn", await Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("source_changed", await Scalar<string>(db, "select withdrawn_reason from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'withdrawn' and from_state = 'open' and to_state = 'withdrawn' and actor = 'bluto'", ("i", item)));
    }

    [Fact]
    public async Task IRQ_09_withdraw_claimed_item_clears_the_claim_and_later_decide_and_release_are_withdrawn()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "stew-1", Now, Ct));

        Assert.IsType<WithdrawResult.Withdrawn>(await store.WithdrawByProducerAsync(Alpha, "bluto", item, "ruleset_retired", Ct));

        Assert.Null(await Scalar<string>(db, "select claimed_by from queue_item where item_id = @i", ("i", item)));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'withdrawn' and from_state = 'claimed'", ("i", item)));
        Assert.IsType<DecideResult.Withdrawn>(await store.DecideIdentityAsync(Alpha, item, "stew-1", "stub", "accept", "ok", Now, Ct));
        Assert.IsType<ReleaseResult.Withdrawn>(await store.ReleaseAsync(Alpha, item, "stew-1", Now, Ct));
    }

    [Fact]
    public async Task IRQ_09_withdraw_decided_is_closed_and_missing_foreign_and_other_kind_are_not_found()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var decided = await Decided(db, Alpha, Now);
        var open = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
                var otherTenant = await db.SeedIdentityItemAsync(Beta, Guid.NewGuid());
        var approval = await db.SeedQueueItemAsync(Alpha, await db.SeedProposalAsync(Alpha, "agent-1", "run-1"));
        var store = Store(db);

        Assert.IsType<WithdrawResult.Closed>(await store.WithdrawByProducerAsync(Alpha, "bluto", decided, "source_changed", Ct));
        Assert.IsType<WithdrawResult.NotFound>(await store.WithdrawByProducerAsync(Alpha, "bluto", Guid.NewGuid(), "source_changed", Ct));
        // The producer constraint pins identity_review to bluto, so a different producer name is the only "foreign producer" case.
        Assert.IsType<WithdrawResult.NotFound>(await store.WithdrawByProducerAsync(Alpha, "other", open, "source_changed", Ct));
        Assert.IsType<WithdrawResult.NotFound>(await store.WithdrawByProducerAsync(Alpha, "bluto", otherTenant, "source_changed", Ct));
        Assert.IsType<WithdrawResult.NotFound>(await store.WithdrawByProducerAsync(Alpha, "olive", approval, "source_changed", Ct));
        Assert.Equal("open", await Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", open)));
        Assert.Equal("open", await Scalar<string>(db, "select state from queue_item where item_id = @i", ("i", approval)));
    }

    [Fact]
    public async Task IRQ_09_repeat_withdraw_same_reason_is_ok_and_a_different_reason_is_closed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await db.SeedIdentityItemAsync(Alpha, Guid.NewGuid());
        var store = Store(db);
        Assert.IsType<WithdrawResult.Withdrawn>(await store.WithdrawByProducerAsync(Alpha, "bluto", item, "source_changed", Ct));

        Assert.IsType<WithdrawResult.AlreadyWithdrawn>(await store.WithdrawByProducerAsync(Alpha, "bluto", item, "source_changed", Ct));
        Assert.IsType<WithdrawResult.Closed>(await store.WithdrawByProducerAsync(Alpha, "bluto", item, "ruleset_retired", Ct));
        Assert.Equal(1L, await Scalar<long>(db, "select count(*) from queue_item_event where item_id = @i and event_type = 'withdrawn'", ("i", item)));
    }
}
