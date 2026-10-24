using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Olive.Application;

namespace Olive.Tests;

// EXE-06: the whole core-value chain is one SELECT keyed by the execution record (D-14, D-15).
[Trait("Category", "Integration")]
public sealed class ExecutionChainViewTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The exact single SELECT; the application tenant predicate stays alongside RLS. Reused by the Beat 2 demo.
    public const string ChainSelect = "select * from execution_chain where tenant_id = @tenant_id and execution_id = @execution_id";

    internal static async Task<List<Dictionary<string, object?>>> QueryChainAsync(string connectionString, Guid setTenant, Guid queryTenant, Guid executionId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await using (var set = new NpgsqlCommand("select set_config('olive.tenant_id', @t, true)", conn, tx))
        {
            set.Parameters.AddWithValue("t", setTenant.ToString());
            await set.ExecuteNonQueryAsync(Ct);
        }

        await using var cmd = new NpgsqlCommand(ChainSelect, conn, tx);
        cmd.Parameters.AddWithValue("tenant_id", queryTenant);
        cmd.Parameters.AddWithValue("execution_id", executionId);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) { row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i); }
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<(Guid Proposal, Guid Execution)> ApproveAsync(OliveApiFactory factory, TestDatabase db, string run, string rationale)
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, run, expiresIn: 3600);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve", rationale)).StatusCode);
        var execution = await DecisionHttp.Scalar<Guid>(db, "select execution_id from execution_ledger where proposal_id = @p and phase = 'started'", ("p", proposal));
        Assert.NotEqual(Guid.Empty, execution);
        return (proposal, execution);
    }

    [Fact]
    public async Task EXE_06_one_select_returns_the_whole_chain()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        const string rationale = "Verified the case note against the source record.";
        var (proposal, execution) = await ApproveAsync(factory, db, "x6a", rationale);

        var rows = await QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution);

        var row = Assert.Single(rows);
        Assert.Equal("olive:proposal/" + proposal, row["proposal_ref"]);
        Assert.Equal("liam", row["agent_id"]);
        Assert.False(string.IsNullOrEmpty((string?)row["on_behalf_of"]));
        Assert.Equal("executed", row["proposal_state"]);
        Assert.NotNull(row["trace_verification"]);
        Assert.NotNull(row["trace_schema_version"]);
        Assert.NotNull(row["proposal_hash"]);

        using var policy = JsonDocument.Parse(row["policy_decision"]!.ToString()!);
        Assert.Equal("allowed", policy.RootElement.GetProperty("outcome").GetString());
        var policyText = policy.RootElement.GetRawText();
        foreach (var check in new[] { "catalog", "purpose", "entitlement", "rate" }) { Assert.Contains(check, policyText); }

        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", row["decided_by"]);
        Assert.Equal("stub", row["auth_source"]);
        Assert.Equal(rationale, row["rationale"]);
        Assert.NotNull(row["decided_at"]);

        Assert.Equal("simulated", row["execution_mode"]);
        Assert.Equal("succeeded", row["outcome"]);
        Assert.False(string.IsNullOrEmpty((string?)row["result_ref"]));
        Assert.NotNull(row["started_at"]);
        Assert.NotNull(row["finished_at"]);
    }

    [Fact]
    public async Task EXE_06_view_has_no_parameter_or_justification_columns()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (_, execution) = await ApproveAsync(factory, db, "x6b", DecisionHttp.Rationale);

        var row = Assert.Single(await QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution));

        foreach (var forbidden in new[] { "parameters", "justification", "canonical_body" }) { Assert.DoesNotContain(forbidden, row.Keys); }
    }

    [Fact]
    public async Task EXE_06_cross_tenant_reader_sees_zero_rows()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (_, execution) = await ApproveAsync(factory, db, "x6c", DecisionHttp.Rationale);

        Assert.Empty(await QueryChainAsync(db.ReaderConnectionString, TestPaths.TenantBeta, DecisionHttp.Alpha, execution));
        Assert.Empty(await QueryChainAsync(db.ReaderConnectionString, TestPaths.TenantBeta, TestPaths.TenantBeta, execution));
    }

    [Fact]
    public async Task EXE_06_olive_app_can_select_the_view()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (_, execution) = await ApproveAsync(factory, db, "x6d", DecisionHttp.Rationale);

        Assert.Single(await QueryChainAsync(db.AppConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution));
    }

    [Fact]
    public async Task EXE_06_view_is_keyed_by_the_started_row()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (proposal, execution) = await ApproveAsync(factory, db, "x6e", DecisionHttp.Rationale);

        var finished = await DecisionHttp.Scalar<Guid>(db, "select execution_id from execution_ledger where proposal_id = @p and phase = 'finished'", ("p", proposal));
        var row = Assert.Single(await QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution));
        Assert.Equal(execution, row["execution_id"]);
        Assert.Empty(await QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, finished));
    }

    [Fact]
    public async Task EXE_06_in_doubt_attempt_shows_started_without_finish()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        FaultingExecutionStore? store = null;
        await using var factory = new OliveApiFactory(db, configureServices: s => s.Decorate<IExecutionStore>(real => store = new FaultingExecutionStore(real)));
        _ = factory.Services.GetRequiredService<ExecutionService>();
        store!.Mode = ExecutionFault.ThrowOnFinish;

        var (proposal, execution) = await ApproveAsync(factory, db, "x6f", DecisionHttp.Rationale);

        var row = Assert.Single(await QueryChainAsync(db.ReaderConnectionString, DecisionHttp.Alpha, DecisionHttp.Alpha, execution));
        Assert.Equal(execution, row["execution_id"]);
        Assert.Equal("olive:proposal/" + proposal, row["proposal_ref"]);
        Assert.Null(row["outcome"]);
        Assert.Null(row["finished_at"]);
        Assert.NotNull(row["started_at"]);
    }
}
