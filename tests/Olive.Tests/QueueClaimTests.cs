using System.Net;
using System.Text.Json;
using Npgsql;
using Olive.Application;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class QueueClaimTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Alpha = TestPaths.TenantAlpha;
    private static readonly Guid Beta = TestPaths.TenantBeta;
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    internal static PostgresQueueStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(TestDatabase.WithMaxPoolSize(db.AppConnectionString, 32))));

    internal static async Task<(Guid Proposal, Guid Item)> SeedAwaiting(TestDatabase db, Guid tenant, string run)
    {
        var proposal = await db.SeedProposalAsync(tenant, "liam", run);
        var item = await db.SeedQueueItemAsync(tenant, proposal);
        await db.AdminExecAsync($"update proposal set state = 'awaiting_approval' where proposal_id = '{proposal}'");
        return (proposal, item);
    }

    internal static Task<T?> Scalar<T>(TestDatabase db, Guid tenant, string sql, params (string, object)[] args) =>
        ApiProposalTests.ReaderScalarAsync<T>(db, tenant, sql, args);

    [Fact]
    public async Task QUE_05_sixteen_parallel_claims_give_exactly_one_winner()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item) = await SeedAwaiting(db, Alpha, "c16");
        var store = Store(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => store.ClaimAsync(Alpha, item, "rev-" + i, Now, Ct)));

        var winner = Assert.Single(results.OfType<ClaimResult.Claimed>());
        Assert.Equal(15, results.OfType<ClaimResult.AlreadyClaimed>().Count());
        Assert.Equal(1L, await Scalar<long>(db, Alpha, "select count(*) from queue_item_event where item_id = @i and event_type = 'claimed'", ("i", item)));
        Assert.Equal(winner.Item.ClaimedBy, await Scalar<string>(db, Alpha, "select claimed_by from queue_item where item_id = @i", ("i", item)));
        Assert.Equal("claimed", winner.Item.State);
    }

    [Fact]
    public async Task QUE_05_claim_of_missing_or_cross_tenant_item_is_not_found()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item) = await SeedAwaiting(db, Alpha, "cx");
        var store = Store(db);

        Assert.IsType<ClaimResult.NotFound>(await store.ClaimAsync(Beta, item, "rev-1", Now, Ct));
        Assert.IsType<ClaimResult.NotFound>(await store.ClaimAsync(Alpha, Guid.NewGuid(), "rev-1", Now, Ct));
        Assert.IsType<ReleaseResult.NotFound>(await store.ReleaseAsync(Beta, item, "rev-1", Now, Ct));
    }

    [Fact]
    public async Task QUE_05_claim_of_decided_item_is_closed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item) = await SeedAwaiting(db, Alpha, "cd");
        await db.AdminExecAsync($"update queue_item set state = 'decided', claimed_by = 'r', claimed_at = now(), decision = 'approve', decided_by = 'r', decided_at = now(), rationale = 'ok', auth_source = 'stub' where item_id = '{item}'");
        var store = Store(db);

        Assert.IsType<ClaimResult.Closed>(await store.ClaimAsync(Alpha, item, "rev-1", Now, Ct));
        Assert.IsType<ReleaseResult.Closed>(await store.ReleaseAsync(Alpha, item, "r", Now, Ct));
    }

    [Fact]
    public async Task QUE_06_release_rules_at_store_level()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var (_, item) = await SeedAwaiting(db, Alpha, "r1");
        var store = Store(db);

        Assert.IsType<ReleaseResult.NotClaimed>(await store.ReleaseAsync(Alpha, item, "rev-1", Now, Ct));
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "rev-1", Now, Ct));
        Assert.IsType<ReleaseResult.NotClaimant>(await store.ReleaseAsync(Alpha, item, "rev-2", Now, Ct));
        Assert.Equal("claimed", await Scalar<string>(db, Alpha, "select state from queue_item where item_id = @i", ("i", item)));

        var released = Assert.IsType<ReleaseResult.Released>(await store.ReleaseAsync(Alpha, item, "rev-1", Now, Ct));
        Assert.Equal("open", released.Item.State);
        Assert.Null(released.Item.ClaimedBy);
        var events = await store.EventsAsync(Alpha, item, Ct);
        Assert.Equal(["created", "claimed", "released"], events!.Select(e => e.EventType).ToArray());
        Assert.IsType<ClaimResult.Claimed>(await store.ClaimAsync(Alpha, item, "rev-2", Now, Ct));
    }

    [Fact]
    public async Task QUE_05_five_reviewers_claim_over_http_with_one_winner()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        await ReviewerTenantTests.SubmitAsync(agent, "hc1");
        var ids = new[] { "alpha-sup-1", "alpha-sup-2", "alpha-norole", "alpha-self", "alpha-lookalike" };
        var clients = ids.Select(factory.CreateReviewerClient).ToArray();
        try
        {
            var item = Assert.Single(await ReviewerTenantTests.ListAsync(clients[0])).GetProperty("item_id").GetGuid();

            var responses = await Task.WhenAll(clients.Select(c => c.PostAsync($"/v1/queue/items/{item}/claim", null, Ct)));

            var ok = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToArray();
            Assert.Single(ok);
            Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct));
                Assert.Equal("already_claimed", doc.RootElement.GetProperty("reason").GetString());
            }

            using var won = JsonDocument.Parse(await ok[0].Content.ReadAsStringAsync(Ct));
            Assert.Equal("claimed", won.RootElement.GetProperty("state").GetString());
            var events = await clients[0].GetAsync($"/v1/queue/items/{item}/events", Ct);
            using var ev = JsonDocument.Parse(await events.Content.ReadAsStringAsync(Ct));
            Assert.Equal(1, ev.RootElement.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("event_type").GetString() == "claimed"));
        }
        finally
        {
            foreach (var c in clients) { c.Dispose(); }
        }
    }

    [Fact]
    public async Task D_04_unknown_item_agent_key_and_missing_header()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        using var alpha = factory.CreateReviewerClient("alpha-sup-1");
        var missing = Guid.NewGuid();

        foreach (var verb in new[] { "claim", "release" })
        {
            var r = await alpha.PostAsync($"/v1/queue/items/{missing}/{verb}", null, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            var body = await r.Content.ReadAsStringAsync(Ct);
            Assert.Contains("item_not_found", body);
            Assert.DoesNotContain(missing.ToString(), body);

            using var agent = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/v1/queue/items/{missing}/{verb}", null, Ct)).StatusCode);

            using var anon = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/v1/queue/items/{missing}/{verb}", null, Ct)).StatusCode);
        }
    }
}
