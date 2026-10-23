using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Olive.Application;
using Olive.Domain;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class TenantUnitOfWorkTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NewProposal Sample(string agent = "agent-1", string run = "run-1") => new(
        agent, "key-1", "user-1", "case_management", new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
        "nono-1", run, "crm.case_note@1", "party://p1", "{\"a\":1,\"b\":[true]}", "because", "trace://t1");

    private static NpgsqlDataSource Pool1(TestDatabase db) =>
        NpgsqlDataSource.Create(TestDatabase.WithMaxPoolSize(db.AppConnectionString, 1));

    private static async Task<T> Scalar<T>(NpgsqlDataSource ds, Guid tenant, string sql, params (string, object)[] args) =>
        await new TenantUnitOfWork(ds).ExecuteAsync(tenant, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (k, v) in args)
            {
                cmd.Parameters.AddWithValue(k, v);
            }

            return (T)(await cmd.ExecuteScalarAsync(Ct))!;
        }, Ct);

    [Fact]
    public async Task DB_05_tenant_context_is_transaction_local_and_switches_on_reused_connection()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var uow = new TenantUnitOfWork(ds);
        Assert.Equal(TestPaths.TenantAlpha.ToString("D"), await Scalar<string>(ds, TestPaths.TenantAlpha, "select current_setting('olive.tenant_id')"));
        Assert.Equal(TestPaths.TenantBeta.ToString("D"), await Scalar<string>(ds, TestPaths.TenantBeta, "select current_setting('olive.tenant_id')"));
        await using var conn = await ds.OpenConnectionAsync(Ct);
        await using var cmd = new NpgsqlCommand("select current_setting('olive.tenant_id', true)", conn);
        var after = await cmd.ExecuteScalarAsync(Ct);
        Assert.True(after is null or DBNull || (string)after == "");
        Assert.NotNull(uow);
    }

    [Fact]
    public async Task DB_05_empty_tenant_is_rejected_before_opening_a_connection()
    {
        await using var ds = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=1");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new TenantUnitOfWork(ds).ExecuteAsync(Guid.Empty, (_, _) => Task.FromResult(0), Ct));
    }

    [Fact]
    public async Task DB_05_insert_stores_proposed_state_with_submitted_event()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        var id = await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct);
        Assert.NotNull(id);

        Assert.Equal("proposed", await Scalar<string>(ds, TestPaths.TenantAlpha, "select state from proposal where tenant_id = @t and proposal_id = @p", ("t", TestPaths.TenantAlpha), ("p", id!.Value)));
        Assert.Equal(1L, await Scalar<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal_event where tenant_id = @t and proposal_id = @p and event_type = 'submitted' and from_state is null and to_state = 'proposed' and actor = 'agent-1'", ("t", TestPaths.TenantAlpha), ("p", id.Value)));
    }

    [Fact]
    public async Task DB_05_duplicate_run_returns_null_and_writes_nothing_but_other_tenant_succeeds()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        Assert.NotNull(await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct));
        Assert.Null(await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct));
        Assert.Equal(1L, await Scalar<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal where tenant_id = @t", ("t", TestPaths.TenantAlpha)));
        Assert.Equal(1L, await Scalar<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal_event where tenant_id = @t", ("t", TestPaths.TenantAlpha)));
        Assert.NotNull(await store.InsertAsync(TestPaths.TenantBeta, Sample(), Ct));
    }

    [Fact]
    public async Task DB_05_get_round_trips_every_field()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        var input = Sample();
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, input, Ct))!.Value;
        var r = await store.GetAsync(TestPaths.TenantAlpha, id, Ct);
        Assert.NotNull(r);
        Assert.Equal(id, r.ProposalId);
        Assert.Equal(TestPaths.TenantAlpha, r.TenantId);
        Assert.Equal("proposed", r.State);
        Assert.Equal(input.AgentId, r.AgentId);
        Assert.Equal(input.KeyId, r.KeyId);
        Assert.Equal(input.OnBehalfOf, r.OnBehalfOf);
        Assert.Equal(input.Purpose, r.Purpose);
        Assert.Equal(input.KeyExpiresAt.UtcDateTime, r.KeyExpiresAt.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, r.KeyExpiresAt.Offset);
        Assert.Equal("nono-1", r.NonoSessionId);
        Assert.Equal("run-1", r.RunId);
        Assert.Equal(input.ActionType, r.ActionType);
        Assert.Equal(input.PartyRef, r.PartyRef);
        Assert.Equal(input.Justification, r.Justification);
        Assert.Equal(input.TraceRef, r.TraceRef);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(input.ParametersJson!).RootElement, JsonDocument.Parse(r.ParametersJson!).RootElement));
    }

    [Fact]
    public async Task DB_05_optional_fields_round_trip_as_null()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        var input = new NewProposal("agent-1", "key-1", "user-1", "case_management", DateTimeOffset.Parse("2030-01-01T00:00:00Z"), null, "run-n", null, null, null, null, null);
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, input, Ct))!.Value;
        var r = (await store.GetAsync(TestPaths.TenantAlpha, id, Ct))!;
        Assert.Null(r.NonoSessionId);
        Assert.Null(r.ActionType);
        Assert.Null(r.PartyRef);
        Assert.Null(r.ParametersJson);
        Assert.Null(r.Justification);
        Assert.Null(r.TraceRef);
    }

    [Fact]
    public async Task DB_05_get_is_null_for_other_tenant_and_unknown_id()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        var betaId = (await store.InsertAsync(TestPaths.TenantBeta, Sample(), Ct))!.Value;
        Assert.Null(await store.GetAsync(TestPaths.TenantAlpha, betaId, Ct));
        Assert.Null(await store.GetAsync(TestPaths.TenantAlpha, Guid.NewGuid(), Ct));
        Assert.NotNull(await store.GetAsync(TestPaths.TenantBeta, betaId, Ct));
    }

    [Fact]
    public async Task DB_05_transition_is_compare_and_set_and_tenant_scoped()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var store = new PostgresProposalStore(new TenantUnitOfWork(ds));
        var id = (await store.InsertAsync(TestPaths.TenantAlpha, Sample(), Ct))!.Value;
        var betaId = (await store.InsertAsync(TestPaths.TenantBeta, Sample(), Ct))!.Value;

        Assert.False(await store.TransitionAsync(TestPaths.TenantAlpha, id, "approved", "executed", "sys", Ct));
        Assert.False(await store.TransitionAsync(TestPaths.TenantAlpha, betaId, "proposed", "awaiting_approval", "sys", Ct));
        Assert.True(await store.TransitionAsync(TestPaths.TenantAlpha, id, "proposed", "awaiting_approval", "sys", Ct));
        Assert.False(await store.TransitionAsync(TestPaths.TenantAlpha, id, "proposed", "awaiting_approval", "sys", Ct));
        Assert.Equal("awaiting_approval", (await store.GetAsync(TestPaths.TenantAlpha, id, Ct))!.State);
        Assert.Equal("proposed", (await store.GetAsync(TestPaths.TenantBeta, betaId, Ct))!.State);
        Assert.Equal(1L, await Scalar<long>(ds, TestPaths.TenantAlpha, "select count(*) from proposal_event where tenant_id = @t and proposal_id = @p and event_type = 'transition' and from_state = 'proposed' and to_state = 'awaiting_approval' and actor = 'sys'", ("t", TestPaths.TenantAlpha), ("p", id)));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void DB_05_sql_constants_carry_tenant_predicate()
    {
        var consts = typeof(ProposalSql).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToList();
        Assert.NotEmpty(consts);
        foreach (var f in consts)
        {
            var sql = (string)f.GetRawConstantValue()!;
            Assert.True(sql.Contains("@tenant_id", StringComparison.Ordinal), f.Name + " lacks @tenant_id");
            if (f.Name is "Get" or "UpdateState")
            {
                Assert.True(sql.Contains("tenant_id = @tenant_id", StringComparison.Ordinal), f.Name + " lacks the tenant predicate");
            }
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void DB_05_only_allowed_files_open_connections()
    {
        var src = Path.Combine(TestPaths.Root, "src");
        string[] allowed = ["TenantUnitOfWork.cs", "MigrationRunner.cs", "DatabaseSafetyChecks.cs"];
        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f =>
            {
                var t = File.ReadAllText(f);
                return t.Contains("OpenConnectionAsync(", StringComparison.Ordinal)
                       || t.Contains("OpenConnection(", StringComparison.Ordinal)
                       || t.Contains("new NpgsqlConnection(", StringComparison.Ordinal);
            })
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task DB_05_state_constants_match_check_constraint()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var ds = Pool1(db);
        var def = await Scalar<string>(ds, TestPaths.TenantAlpha,
            "select pg_get_constraintdef(c.oid) from pg_constraint c where c.conrelid = 'proposal'::regclass and c.contype = 'c' and pg_get_constraintdef(c.oid) like '%state%' and pg_get_constraintdef(c.oid) like '%proposed%'");
        var values = Regex.Matches(def, "'([a-z_]+)'").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(ProposalStates.All.OrderBy(x => x).ToList(), values);
    }
}
