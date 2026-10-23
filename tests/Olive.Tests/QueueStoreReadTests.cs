using Npgsql;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueStoreReadTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static PostgresQueueStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    private static async Task<Guid> SeedItem(TestDatabase db, Guid tenant, string run)
    {
        var proposal = await db.SeedProposalAsync(tenant, "liam", run);
        return await db.SeedQueueItemAsync(tenant, proposal);
    }

    private static string Ts(DateTimeOffset t) => "'" + t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + "+00'";

    [Fact]
    public async Task QUE_04_list_returns_only_the_calling_tenants_items()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var mine = await SeedItem(db, Alpha, "a1");
        var theirs = await SeedItem(db, Beta, "b1");
        var store = Store(db);

        var list = await store.ListAsync(Alpha, null, null, Now, 50, Ct);
        Assert.Equal([mine], list.Select(i => i.ItemId).ToArray());
        Assert.All(list, i => Assert.Equal(Alpha, i.TenantId));
        Assert.Null(await store.GetAsync(Alpha, theirs, Ct));
        Assert.NotNull(await store.GetAsync(Beta, theirs, Ct));
    }

    [Fact]
    public async Task QUE_04_list_omits_expired_open_and_claimed_items_with_equality_expired_and_writes_nothing()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var openExpired = await SeedItem(db, Alpha, "e1");
        var claimedExpired = await SeedItem(db, Alpha, "e2");
        var equal = await SeedItem(db, Alpha, "e3");
        var live = await SeedItem(db, Alpha, "e4");
        var noExpiry = await SeedItem(db, Alpha, "e5");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now.AddSeconds(-1))} where item_id = '{openExpired}'");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now.AddSeconds(-1))}, state = 'claimed', claimed_by = 'rev-1', claimed_at = now() where item_id = '{claimedExpired}'");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now)} where item_id = '{equal}'");
        await db.AdminExecAsync($"update queue_item set expires_at = {Ts(Now.AddSeconds(1))} where item_id = '{live}'");
        var store = Store(db);

        var ids = (await store.ListAsync(Alpha, null, null, Now, 50, Ct)).Select(i => i.ItemId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { live, noExpiry }, ids);

        // No write: the expired item is still open and no event was added.
        Assert.Equal("open", await ApiProposalTests.ReaderScalarAsync<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", openExpired)));
        Assert.Equal(5, await ApiProposalTests.ReaderScalarAsync<long>(db, Alpha, "select count(*) from queue_item_event"));
    }

    [Fact]
    public async Task QUE_04_list_is_oldest_first_and_honours_kind_state_and_limit()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var third = await SeedItem(db, Alpha, "o3");
        var first = await SeedItem(db, Alpha, "o1");
        var second = await SeedItem(db, Alpha, "o2");
        await db.AdminExecAsync($"update queue_item set created_at = now() - interval '3 hours' where item_id = '{first}'");
        await db.AdminExecAsync($"update queue_item set created_at = now() - interval '2 hours' where item_id = '{second}'");
        await db.AdminExecAsync($"update queue_item set created_at = now() - interval '1 hours', state = 'claimed', claimed_by = 'rev-1', claimed_at = now() where item_id = '{third}'");
        var store = Store(db);

        Assert.Equal([first, second, third], (await store.ListAsync(Alpha, null, null, Now, 50, Ct)).Select(i => i.ItemId).ToArray());
        Assert.Equal([first, second], (await store.ListAsync(Alpha, "action_approval", "open", Now, 50, Ct)).Select(i => i.ItemId).ToArray());
        Assert.Equal([third], (await store.ListAsync(Alpha, null, "claimed", Now, 50, Ct)).Select(i => i.ItemId).ToArray());
        Assert.Empty(await store.ListAsync(Alpha, "identity_review", null, Now, 50, Ct));
        Assert.Equal([first, second], (await store.ListAsync(Alpha, null, null, Now, 2, Ct)).Select(i => i.ItemId).ToArray());
    }

    [Fact]
    public async Task QUE_09_events_are_ordered_by_seq_and_null_for_missing_or_cross_tenant_item()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var item = await SeedItem(db, Alpha, "ev1");
        // Inserted later with an earlier created_at: seq, not created_at, defines the order.
        await db.AdminExecAsync(
            $"insert into queue_item_event (tenant_id, event_id, item_id, event_type, from_state, to_state, actor, created_at) " +
            $"values ('{Alpha}', gen_random_uuid(), '{item}', 'released', 'claimed', 'open', 'rev-1', now() - interval '1 day')");
        var store = Store(db);

        var events = await store.EventsAsync(Alpha, item, Ct);
        Assert.NotNull(events);
        Assert.Equal(["created", "released"], events.Select(e => e.EventType).ToArray());
        Assert.True(events[0].Seq < events[1].Seq);
        Assert.Null(await store.EventsAsync(Alpha, Guid.NewGuid(), Ct));
        Assert.Null(await store.EventsAsync(Beta, item, Ct));
    }

    [Fact]
    public async Task QUE_04_payload_is_returned_in_tenant_and_null_cross_tenant()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var proposal = await db.SeedProposalAsync(Alpha, "liam", "pl1");
        var item = await db.SeedQueueItemAsync(Alpha, proposal);
        await db.AdminExecAsync(
            $"update proposal set action_type = 'crm.case_note@1', parameters = '{{\"case_ref\":\"payload://c1\"}}'::jsonb, justification = 'because', trace_ref = 'trace://t1' where proposal_id = '{proposal}'");
        var store = Store(db);

        var payload = await store.GetPayloadAsync(Alpha, item, Ct);
        Assert.NotNull(payload);
        Assert.Equal(item, payload.ItemId);
        Assert.Equal("action_approval", payload.Kind);
        Assert.Equal("crm.case_note@1", payload.ActionType);
        Assert.Contains("payload://c1", payload.ParametersJson);
        Assert.Equal("because", payload.Justification);
        Assert.Equal("user-1", payload.OnBehalfOf);
        Assert.Equal("trace://t1", payload.TraceRef);
        Assert.Null(await store.GetPayloadAsync(Beta, item, Ct));
        Assert.Null(await store.GetAsync(Beta, item, Ct));
    }
}
