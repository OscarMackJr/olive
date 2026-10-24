using Npgsql;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public class ExecutionLedgerTests(PostgresFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection conn)
    {
        await conn.OpenAsync(Ct);
        var tx = await conn.BeginTransactionAsync(Ct);
        await using var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx);
        set.Parameters.AddWithValue("t", TestPaths.TenantAlpha.ToString());
        await set.ExecuteNonQueryAsync(Ct);
        return tx;
    }

    private static async Task<string> SqlStateAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await using var tx = await BeginTenantAsync(conn);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        return ex.SqlState;
    }

    private static string Insert(Guid proposalId, string phase, string outcome = "null", string reason = "null", string resultRef = "null", string mode = "simulated") =>
        "insert into execution_ledger (tenant_id, execution_id, proposal_id, phase, execution_mode, outcome, reason, result_ref, recorded_at) " +
        $"values ('{TestPaths.TenantAlpha}', gen_random_uuid(), '{proposalId}', '{phase}', '{mode}', {outcome}, {reason}, {resultRef}, now())";

    private static async Task InsertOkAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await using var tx = await BeginTenantAsync(conn);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(Ct));
        await tx.CommitAsync(Ct);
    }

    [Fact]
    public async Task EXE_02_ledger_rejects_duplicate_phase_for_one_proposal()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await InsertOkAsync(db.AppConnectionString, Insert(p, "started"));
        Assert.Equal("23505", await SqlStateAsync(db.AppConnectionString, Insert(p, "started")));
    }

    [Fact]
    public async Task EXE_04_ledger_rejects_non_simulated_mode()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Insert(p, "started", mode: "live")));
    }

    [Fact]
    public async Task EXE_02_ledger_shape_check_rejects_finished_failed_without_reason()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Insert(p, "finished", outcome: "'failed'")));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Insert(p, "finished", outcome: "'succeeded'")));
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Insert(p, "started", outcome: "'succeeded'", resultRef: "'r'")));
    }

    [Fact]
    public async Task ledger_reason_must_be_static_code_pattern()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        Assert.Equal("23514", await SqlStateAsync(db.AppConnectionString, Insert(p, "finished", outcome: "'failed'", reason: "'Bad Reason!'")));
        await InsertOkAsync(db.AppConnectionString, Insert(p, "finished", outcome: "'failed'", reason: "'simulated_failure'"));
    }

    [Fact]
    public async Task EXE_02_olive_app_cannot_update_or_delete_ledger_rows()
    {
        await using var db = await fx.CreateDatabaseAsync();
        var p = await db.SeedProposalAsync(TestPaths.TenantAlpha, "agent-1", "run-1");
        await InsertOkAsync(db.AppConnectionString, Insert(p, "started"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "update execution_ledger set reason = 'x'"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "delete from execution_ledger"));
        Assert.Equal("42501", await SqlStateAsync(db.AppConnectionString, "truncate execution_ledger"));
    }
}
