using Npgsql;
using Olive.Application;
using Olive.Domain;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class LifecycleTransitionTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HashSet<(string, string)> Expected =
    [
        ("proposed", "refused_policy"), ("proposed", "awaiting_approval"),
        ("awaiting_approval", "approved"), ("awaiting_approval", "rejected"), ("awaiting_approval", "expired"),
        ("approved", "executed"), ("approved", "execution_failed"), ("approved", "expired"),
    ];

    [Fact]
    public void POL_06_transition_matrix()
    {
        var count = 0;
        foreach (var from in ProposalStates.All)
        {
            foreach (var to in ProposalStates.All)
            {
                count++;
                Assert.Equal(Expected.Contains((from, to)), ProposalStates.CanTransition(from, to));
            }
        }

        Assert.Equal(64, count);
        Assert.False(ProposalStates.CanTransition("nope", "approved"));
        Assert.False(ProposalStates.CanTransition("proposed", "PROPOSED"));
    }

    [Fact]
    public void POL_06_terminal_states_have_no_outgoing_edges()
    {
        foreach (var t in new[] { "refused_policy", "rejected", "executed", "execution_failed", "expired" })
        {
            Assert.All(ProposalStates.All, to => Assert.False(ProposalStates.CanTransition(t, to)));
        }
    }

    private static NewProposal Sample(string run = "run-1") => new(
        "agent-1", "key-1", "user-1", "case_management", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "nono-1", run, "crm.case_note@1", "party://p1", "{\"a\":1}", "because", "trace://t1",
        TraceVerification: "fixture", TraceSchemaVersion: "ate/1.0");

    private static PostgresProposalStore Store(TestDatabase db) =>
        new(new TenantUnitOfWork(NpgsqlDataSource.Create(db.AppConnectionString)));

    private static async Task<List<(string From, string To, string Actor)>> TransitionEventsAsync(TestDatabase db, Guid id)
    {
        await using var ds = NpgsqlDataSource.Create(db.ReaderConnectionString);
        return await new TenantUnitOfWork(ds).ExecuteAsync(TestPaths.TenantAlpha, async (conn, tx) =>
        {
            var list = new List<(string, string, string)>();
            await using var cmd = new NpgsqlCommand("select from_state, to_state, actor from proposal_event where proposal_id = @p and event_type = 'transition'", conn, tx);
            cmd.Parameters.AddWithValue("p", id);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            }

            return list;
        }, Ct);
    }

    [Fact]
    public async Task POL_06_illegal_transition_throws_before_db()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct))!.Value;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(TestPaths.TenantAlpha, id, "proposed", "executed", "sys", Ct));
        Assert.Empty(await TransitionEventsAsync(db, id));
        Assert.Equal("proposed", (await store.GetAsync(TestPaths.TenantAlpha, id, Ct))!.State);
    }

    [Fact]
    public async Task POL_06_stale_from_state_returns_false_and_cross_tenant_returns_false_with_no_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct))!.Value;
        Assert.False(await store.TransitionAsync(TestPaths.TenantAlpha, id, "awaiting_approval", "approved", "sys", Ct));
        Assert.False(await store.TransitionAsync(TestPaths.TenantBeta, id, "proposed", "awaiting_approval", "sys", Ct));
        Assert.Empty(await TransitionEventsAsync(db, id));
    }

    [Fact]
    public async Task POL_06_success_writes_exactly_one_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct))!.Value;
        Assert.True(await store.TransitionAsync(TestPaths.TenantAlpha, id, "proposed", "awaiting_approval", "policy", Ct));
        Assert.Equal([("proposed", "awaiting_approval", "policy")], await TransitionEventsAsync(db, id));
        Assert.True(await store.TransitionAsync(TestPaths.TenantAlpha, id, "awaiting_approval", "approved", "reviewer", Ct));
        Assert.Equal(2, (await TransitionEventsAsync(db, id)).Count);
    }

    [Fact]
    public async Task POL_06_refused_policy_only_via_refusal_path()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var store = Store(db);
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct))!.Value;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(TestPaths.TenantAlpha, id, "proposed", "refused_policy", "sys", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TransitionAsync(TestPaths.TenantAlpha, id, "refused_policy", "proposed", "sys", Ct));
    }
}
