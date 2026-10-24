using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Olive.Application;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class ExecutionFailureTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public required OliveApiFactory Factory { get; init; }
        public required ManualTimeProvider Clock { get; init; }
        public FaultingExecutionStore Store { get; set; } = null!;
        public SwitchableCatalogCheck Catalog { get; set; } = null!;
        public CountingExecutor Counter { get; set; } = null!;
    }

    private static Rig Build(TestDatabase db, bool throwingExecutor = false)
    {
        var n = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(new DateTimeOffset(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second, TimeSpan.Zero));
        FaultingExecutionStore? store = null;
        SwitchableCatalogCheck? catalog = null;
        CountingExecutor? counter = null;
        var factory = new OliveApiFactory(db, configureServices: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.Decorate<IExecutionStore>(real => store = new FaultingExecutionStore(real));
            s.Decorate<IActionCatalogCheck>(real => catalog = new SwitchableCatalogCheck(real));
            s.Decorate<IActionExecutor>(real => counter = new CountingExecutor(throwingExecutor ? new ThrowingExecutor() : real));
        });
        // Singletons are built on first use; resolve once so the captured instances exist.
        _ = factory.Services.GetRequiredService<ExecutionService>();
        return new Rig { Factory = factory, Clock = clock, Store = store!, Catalog = catalog!, Counter = counter! };
    }

    private static string[] Lines(OliveApiFactory factory)
    {
        if (!File.Exists(factory.ExecutorOutputPath)) { return []; }
        using var fs = new FileStream(factory.ExecutorOutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<(Guid Item, Guid Proposal, HttpResponseMessage Response)> ApproveAsync(Rig rig, TestDatabase db, string run, ExecutionFault fault)
    {
        var (item, proposal) = await DecisionHttp.SubmitAsync(rig.Factory, db, run, expiresIn: 3600);
        await DecisionHttp.ClaimAsync(rig.Factory, "alpha-sup-1", item);
        rig.Store.Mode = fault;
        var response = await DecisionHttp.Decide(rig.Factory, "alpha-sup-1", item, "approve");
        return (item, proposal, response);
    }

    private static Task<long> Count(TestDatabase db, string where, Guid proposal) =>
        DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and " + where, ("p", proposal))!;

    private static Task<long> Events(TestDatabase db, Guid proposal, string to) =>
        DecisionHttp.Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p and from_state = 'approved' and to_state = @t", ("p", proposal), ("t", to))!;

    private static async Task AssertNoSentinelAsync(Rig rig, TestDatabase db, Guid proposal, string sentinel)
    {
        Assert.DoesNotContain(sentinel, rig.Factory.Logs.Text);
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from execution_ledger where proposal_id = @p and strpos(coalesce(reason, '') || coalesce(result_ref, ''), @s) > 0", ("p", proposal), ("s", sentinel)));
        Assert.Equal(0L, await DecisionHttp.Scalar<long>(db, "select count(*) from proposal_event where proposal_id = @p and strpos(detail::text, @s) > 0", ("p", proposal), ("s", sentinel)));
    }

    [Fact]
    public async Task EXE_01_decide_stays_200_when_execution_faults()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db);
        await using var _f = rig.Factory;
        var (_, proposal, response) = await ApproveAsync(rig, db, "f1", ExecutionFault.ThrowOnBegin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(FaultingExecutionStore.Sentinel, text);
        Assert.Contains("\"state\":\"decided\"", text);
        Assert.Equal("approved", await DecisionHttp.ProposalState(db, proposal));
        await AssertNoSentinelAsync(rig, db, proposal, FaultingExecutionStore.Sentinel);
        Assert.Empty(Lines(rig.Factory));
    }

    [Fact]
    public async Task EXE_03_expired_by_execution_time_moves_to_expired()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db);
        await using var _f = rig.Factory;
        var (_, proposal, _) = await ApproveAsync(rig, db, "f2", ExecutionFault.ThrowOnBegin);
        rig.Clock.Advance(TimeSpan.FromHours(2));

        var result = await rig.Factory.Services.GetRequiredService<ExecutionService>().ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);

        var failed = Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal("proposal_expired", failed.Row.Reason);
        Assert.Equal("expired", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await Count(db, "phase = 'finished' and outcome = 'failed' and reason = 'proposal_expired'", proposal));
        Assert.Equal(1L, await Events(db, proposal, "expired"));
        Assert.Empty(Lines(rig.Factory));
        Assert.Equal(0, rig.Counter.Calls);
    }

    [Fact]
    public async Task EXE_03_invalid_catalog_at_execution_is_terminal_execution_failed()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db);
        await using var _f = rig.Factory;
        var (_, proposal, _) = await ApproveAsync(rig, db, "f3", ExecutionFault.ThrowOnBegin);
        rig.Catalog.Executable = false;

        var result = await rig.Factory.Services.GetRequiredService<ExecutionService>().ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);

        var failed = Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal("catalog_invalid", failed.Row.Reason);
        Assert.Equal("execution_failed", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await Count(db, "phase = 'finished' and outcome = 'failed' and reason = 'catalog_invalid'", proposal));
        Assert.Equal(1L, await Events(db, proposal, "execution_failed"));
        Assert.Empty(Lines(rig.Factory));
        Assert.Equal(0, rig.Counter.Calls);
    }

    [Fact]
    public async Task EXE_03_expiry_is_checked_before_catalog()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db);
        await using var _f = rig.Factory;
        var (_, proposal, _) = await ApproveAsync(rig, db, "f4", ExecutionFault.ThrowOnBegin);
        rig.Clock.Advance(TimeSpan.FromHours(2));
        rig.Catalog.Executable = false;

        var result = await rig.Factory.Services.GetRequiredService<ExecutionService>().ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);

        Assert.Equal("proposal_expired", Assert.IsType<ExecutionResult.Failed>(result).Row.Reason);
        Assert.Equal("expired", await DecisionHttp.ProposalState(db, proposal));
    }

    [Fact]
    public async Task EXE_05_fault_between_ledger_write_and_state_update_resolves_in_doubt()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db);
        await using var _f = rig.Factory;
        var (_, proposal, response) = await ApproveAsync(rig, db, "f5", ExecutionFault.ThrowOnFinish);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("approved", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await Count(db, "phase = 'started'", proposal));
        Assert.Equal(0L, await Count(db, "phase = 'finished'", proposal));
        Assert.Single(Lines(rig.Factory));
        Assert.Equal(1, rig.Counter.Calls);

        var service = rig.Factory.Services.GetRequiredService<ExecutionService>();
        var second = await service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);

        var failed = Assert.IsType<ExecutionResult.Failed>(second);
        Assert.Equal("execution_in_doubt", failed.Row.Reason);
        Assert.Equal("execution_failed", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await Count(db, "phase = 'finished' and outcome = 'failed' and reason = 'execution_in_doubt'", proposal));
        Assert.Single(Lines(rig.Factory));
        Assert.Equal(1, rig.Counter.Calls);

        var third = await service.ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);
        Assert.Equal(failed.Row.ExecutionId, Assert.IsType<ExecutionResult.Replayed>(third).Row.ExecutionId);
        Assert.Single(Lines(rig.Factory));
        await AssertNoSentinelAsync(rig, db, proposal, FaultingExecutionStore.Sentinel);
    }

    [Fact]
    public async Task EXE_05_executor_exception_is_terminal_execution_failed_with_static_reason()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var rig = Build(db, throwingExecutor: true);
        await using var _f = rig.Factory;
        var (_, proposal, response) = await ApproveAsync(rig, db, "f6", ExecutionFault.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(ThrowingExecutor.Sentinel, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("execution_failed", await DecisionHttp.ProposalState(db, proposal));
        Assert.Equal(1L, await Count(db, "phase = 'started'", proposal));
        Assert.Equal(1L, await Count(db, "phase = 'finished' and outcome = 'failed' and reason = 'executor_failed'", proposal));
        Assert.Equal(1L, await Events(db, proposal, "execution_failed"));
        Assert.Empty(Lines(rig.Factory));
        await AssertNoSentinelAsync(rig, db, proposal, ThrowingExecutor.Sentinel);

        var again = await rig.Factory.Services.GetRequiredService<ExecutionService>().ExecuteAsync(DecisionHttp.Alpha, proposal, Ct);
        Assert.IsType<ExecutionResult.Replayed>(again);
        Assert.Equal(1, rig.Counter.Calls);
    }
}
