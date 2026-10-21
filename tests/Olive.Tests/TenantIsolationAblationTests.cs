using Npgsql;
using Olive.Persistence;

namespace Olive.Tests;

[CollectionDefinition("DbAblation", DisableParallelization = true)]
public sealed class DbAblationCollection;

[Collection("DbAblation")]
[Trait("Category", "Integration")]
public sealed class TenantIsolationAblationTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] UnsetStates = ["42704", "22P02"];

    private static NpgsqlDataSource Pool1(TestDatabase db) =>
        NpgsqlDataSource.Create(TestDatabase.WithMaxPoolSize(db.AppConnectionString, 1));

    private static Task<T> As<T>(NpgsqlDataSource ds, Guid tenant, string sql, params (string, object)[] args) =>
        new TenantUnitOfWork(ds).ExecuteAsync(tenant, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (k, v) in args)
            {
                cmd.Parameters.AddWithValue(k, v);
            }

            var r = await cmd.ExecuteScalarAsync(Ct);
            return r is null or DBNull ? default! : (T)r;
        }, Ct);

    private static Task<int> NonQueryAs(NpgsqlDataSource ds, Guid tenant, string sql, params (string, object)[] args) =>
        new TenantUnitOfWork(ds).ExecuteAsync(tenant, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (k, v) in args)
            {
                cmd.Parameters.AddWithValue(k, v);
            }

            return await cmd.ExecuteNonQueryAsync(Ct);
        }, Ct);

    private async Task<(TestDatabase Db, NpgsqlDataSource Ds, Guid AlphaId, Guid BetaId)> Setup(params string[] disableRls)
    {
        var db = await AblationDatabase.CreateAsync(fixture, disableRls);
        var ds = Pool1(db);
        var a = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        var b = await db.SeedProposalAsync(TestPaths.TenantBeta, "agent-1", "run-1");
        return (db, ds, a, b);
    }

    [Fact]
    public async Task DB_06_unset_tenant_raises_on_fresh_connection()
    {
        var (db, ds, _, _) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("select count(*) from proposal", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync(Ct));
        Assert.Contains(ex.SqlState, UnsetStates);
    }

    [Fact]
    public async Task DB_06_unset_tenant_raises_on_reused_pooled_connection()
    {
        var (db, ds, alphaId, _) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        Assert.Equal(1L, await As<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal"));
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("select count(*) from proposal where proposal_id = @p", conn);
        cmd.Parameters.AddWithValue("p", alphaId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync(Ct));
        Assert.Contains(ex.SqlState, UnsetStates);
    }

    [Fact]
    public async Task DB_06_predicate_removed_rls_blocks_cross_tenant_read()
    {
        var (db, ds, _, betaId) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        Assert.Equal(0L, await As<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal where proposal_id = @p", ("p", betaId)));
        Assert.Equal(0L, await As<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal_event where proposal_id = @p", ("p", betaId)));
    }

    [Fact]
    public async Task DB_06_predicate_removed_rls_blocks_cross_tenant_write()
    {
        var (db, ds, _, betaId) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        Assert.Equal(0, await NonQueryAs(ds, TestPaths.TenantAlpha, "update proposal set state = 'expired' where proposal_id = @p", ("p", betaId)));
        Assert.Equal("proposed", await As<string>(ds, TestPaths.TenantBeta, "select state from proposal where proposal_id = @p", ("p", betaId)));

        var ex1 = await Assert.ThrowsAsync<PostgresException>(() => NonQueryAs(ds, TestPaths.TenantAlpha,
            "insert into proposal (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, run_id) values (@t, @p, 'a', 'k', 'u', 'x', now(), 'r2')",
            ("t", TestPaths.TenantBeta), ("p", Guid.NewGuid())));
        Assert.Equal("42501", ex1.SqlState);

        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => NonQueryAs(ds, TestPaths.TenantAlpha,
            "insert into proposal_event (tenant_id, event_id, proposal_id, event_type, to_state, actor) values (@t, @e, @p, 'x', 'proposed', 'a')",
            ("t", TestPaths.TenantBeta), ("e", Guid.NewGuid()), ("p", betaId)));
        Assert.Equal("42501", ex2.SqlState);
    }

    [Fact]
    public async Task DB_06_rls_disabled_ablation_is_real_and_predicate_blocks_cross_tenant_read()
    {
        var (db, ds, _, betaId) = await Setup("proposal", "proposal_event");
        await using var _d = db;
        await using var _s = ds;
        // Proves the ablation: without RLS the unfiltered query leaks the beta row.
        Assert.Equal(1L, await As<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal where proposal_id = @p", ("p", betaId)));
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        Assert.Null(await store.GetAsync(TestPaths.TenantAlpha, betaId, Ct));
        Assert.NotNull(await store.GetAsync(TestPaths.TenantBeta, betaId, Ct));
    }

    [Fact]
    public async Task DB_06_rls_disabled_predicate_blocks_cross_tenant_write()
    {
        var (db, ds, _, betaId) = await Setup("proposal", "proposal_event");
        await using var _d = db;
        await using var _s = ds;
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        Assert.False(await store.TransitionAsync(TestPaths.TenantAlpha, betaId, "proposed", "expired", "sys", Ct));
        Assert.Equal("proposed", await As<string>(ds, TestPaths.TenantBeta, "select state from proposal where proposal_id = @p", ("p", betaId)));
    }

    [Fact]
    public async Task DB_06_app_role_cannot_disable_rls()
    {
        var (db, ds, _, _) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            NonQueryAs(ds, TestPaths.TenantAlpha, "alter table proposal disable row level security"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task DB_06_test_roles_are_neither_superuser_nor_bypassrls()
    {
        var (db, ds, _, _) = await Setup();
        await using var _d = db;
        await using var _s = ds;
        Assert.Equal("olive_app", await As<string>(ds, TestPaths.TenantAlpha, "select current_user"));
        Assert.Equal(0L, await As<long>(ds, TestPaths.TenantAlpha, "select count(*) from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)"));
        await using var reader = NpgsqlDataSource.Create(TestDatabase.WithMaxPoolSize(db.ReaderConnectionString, 1));
        Assert.Equal("olive_reader", await As<string>(reader, TestPaths.TenantAlpha, "select current_user"));
        Assert.Equal(0L, await As<long>(reader, TestPaths.TenantAlpha, "select count(*) from pg_roles where rolname = current_user and (rolsuper or rolbypassrls)"));
    }
}
