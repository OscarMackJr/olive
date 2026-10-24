using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Olive.Application;
using Olive.Persistence;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ExecutionConcurrencyTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Lines(OliveApiFactory factory)
    {
        if (!File.Exists(factory.ExecutorOutputPath)) { return []; }
        using var fs = new FileStream(factory.ExecutorOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Task<long> LedgerCount(TestDatabase db, Guid proposal, string? phase = null) =>
        DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and (@ph::text is null or phase = @ph)",
            ("p", proposal), ("ph", (object?)phase ?? DBNull.Value))!;

    // Approves through the API while the in-request attempt faults, leaving an approved proposal with no ledger rows.
    private static async Task<(Guid Proposal, FaultingExecutionStore Store, GateExecutor Gate)> DeferredAsync(OliveApiFactory factory, TestDatabase db, string run)
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, run);
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        var store = (FaultingExecutionStore)factory.Services.GetRequiredService<IExecutionStore>();
        store.Mode = ExecutionFault.ThrowOnBegin;
        Assert.Equal(System.Net.HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        Assert.Equal("approved", await DecisionHttp.ProposalState(db, proposal));
        var gate = (GateExecutor)factory.Services.GetRequiredService<IActionExecutor>();
        return (proposal, store, gate);
    }

    private static OliveApiFactory Build(TestDatabase db) => new(db, configureServices: s =>
    {
        s.Decorate<IExecutionStore>(real => new FaultingExecutionStore(real));
        s.Decorate<IActionExecutor>(real => new GateExecutor(real));
    });

    [Fact]
    public async Task EXE_02_concurrent_attempts_produce_one_write_and_first_result()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Build(db);
        var (proposal, _, gate) = await DeferredAsync(factory, db, "c1");
        var service = factory.Services.GetRequiredService<ExecutionService>();

        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct), Ct)).ToArray();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        gate.Release();
        var results = await Task.WhenAll(attempts);

        var executed = Assert.Single(results.OfType<ExecutionResult.Executed>());
        var replays = results.OfType<ExecutionResult.Replayed>().ToList();
        Assert.Equal(7, replays.Count);
        Assert.All(replays, r => Assert.Equal(executed.Row.ExecutionId, r.Row.ExecutionId));
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1L, await LedgerCount(db, proposal, "started"));
        Assert.Equal(1L, await LedgerCount(db, proposal, "finished"));
        Assert.Equal(1, Lines(factory).Length);
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and reason = 'execution_in_doubt'", ("p", proposal)));
        Assert.Equal("executed", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task EXE_02_waiter_times_out_without_in_doubt()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = Build(db);
        var (proposal, _, gate) = await DeferredAsync(factory, db, "c2");
        var service = factory.Services.GetRequiredService<ExecutionService>();

        var first = Task.Run(() => service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct), Ct);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var uow = factory.Services.GetRequiredService<TenantUnitOfWork>();
        var impatient = new ExecutionService(new PostgresExecutionStore(uow, TimeSpan.FromSeconds(1)), gate,
            factory.Services.GetRequiredService<IActionCatalogCheck>(), TimeProvider.System);
        var second = await impatient.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);
        Assert.IsType<ExecutionResult.LockTimeout>(second);

        gate.Release();
        Assert.IsType<ExecutionResult.Executed>(await first);
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and outcome = 'failed'", ("p", proposal)));
        Assert.Equal(1, Lines(factory).Length);
    }

    [Fact]
    public async Task EXE_02_unique_phase_key_backs_up_the_lock()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        var (item, proposal) = await DecisionHttp.SubmitAsync(factory, db, "c3");
        await DecisionHttp.ClaimAsync(factory, "alpha-sup-1", item);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await DecisionHttp.Decide(factory, "alpha-sup-1", item, "approve")).StatusCode);
        var uow = factory.Services.GetRequiredService<TenantUnitOfWork>();

        var e = await Assert.ThrowsAsync<PostgresException>(() => uow.ExecuteAsync(DecisionHttp.Alpha, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "insert into execution_ledger (tenant_id, execution_id, proposal_id, phase, execution_mode, recorded_at) values (@t, @e, @p, 'started', 'simulated', now())", conn, tx);
            cmd.Parameters.AddWithValue("t", DecisionHttp.Alpha);
            cmd.Parameters.AddWithValue("e", Guid.NewGuid());
            cmd.Parameters.AddWithValue("p", proposal);
            await cmd.ExecuteNonQueryAsync(Ct);
            return 0;
        }, Ct));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, e.SqlState);
    }
}
