using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class ExecutionServiceTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Proposal = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public List<string> Calls { get; } = [];
        public ExecutionSnapshot? Snapshot { get; set; }
        public bool Executable { get; set; } = true;
        public bool ExecutorThrows { get; set; }
        public bool FinishThrows { get; set; }
        public bool LockTimesOut { get; set; }
        public bool Disposed { get; set; }
        public string? FinishedReason { get; set; }
        public string? ToState { get; set; }

        public ExecutionService Service() => new(new Store(this), new Executor(this), new Check(this), new ManualTimeProvider(Now));

        public static ExecutionSnapshot Approved(DateTimeOffset? expires = null, LedgerRow? started = null, LedgerRow? finished = null) =>
            new(ProposalStates.Approved, "crm.case_note@1", "{\"case_ref\":\"c\",\"note_ref\":\"n\"}", expires ?? Now.AddHours(1), started, finished);

        public static LedgerRow Row(string phase, string? outcome = null, string? reason = null, string? resultRef = null) =>
            new(Guid.NewGuid(), phase, outcome, reason, resultRef, Now);
    }

    private sealed class Store(Rig rig) : IExecutionStore
    {
        public Task<IExecutionAttempt?> BeginAttemptAsync(Guid tenantId, Guid proposalId, CancellationToken ct) =>
            Task.FromResult<IExecutionAttempt?>(rig.LockTimesOut ? null : new Attempt(rig));

        public Task<IReadOnlyList<Guid>> ListApprovedAsync(Guid tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class Attempt(Rig rig) : IExecutionAttempt
    {
        public Task<ExecutionSnapshot?> LoadAsync(CancellationToken ct) => Task.FromResult(rig.Snapshot);

        public Task<LedgerRow> RecordStartedAsync(DateTimeOffset now, CancellationToken ct)
        {
            rig.Calls.Add("started");
            return Task.FromResult(Rig.Row("started"));
        }

        public Task<LedgerRow> RecordFinishedAsync(FinishedOutcome outcome, string toState, DateTimeOffset now, CancellationToken ct)
        {
            if (rig.FinishThrows) { throw new InvalidOperationException("boom"); }
            rig.Calls.Add("finished:" + (outcome.Succeeded ? "succeeded" : "failed"));
            rig.FinishedReason = outcome.Reason;
            rig.ToState = toState;
            return Task.FromResult(Rig.Row("finished", outcome.Succeeded ? "succeeded" : "failed", outcome.Reason, outcome.ResultRef));
        }

        public Task<LedgerRow> RecordRefusalAsync(string reason, string toState, DateTimeOffset now, CancellationToken ct)
        {
            rig.Calls.Add("refusal");
            rig.FinishedReason = reason;
            rig.ToState = toState;
            return Task.FromResult(Rig.Row("finished", "failed", reason));
        }

        public ValueTask DisposeAsync()
        {
            rig.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Executor(Rig rig) : IActionExecutor
    {
        public Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct)
        {
            rig.Calls.Add("executor");
            if (rig.ExecutorThrows) { throw new InvalidOperationException("SECRET-EXECUTOR-TEXT"); }
            return Task.FromResult(new ExecutionTargetResult("simulated:" + request.ProposalId));
        }
    }

    private sealed class Check(Rig rig) : IActionCatalogCheck
    {
        public CatalogCheckResult Check_(string a, string p) => new(rig.Executable, rig.Executable ? "nexus_crm" : null);

        CatalogCheckResult IActionCatalogCheck.Check(string actionId, string parametersJson) => Check_(actionId, parametersJson);
    }

    [Fact]
    public async Task EXE_01_clean_approved_proposal_runs_started_then_executor_then_finished_and_becomes_executed()
    {
        var rig = new Rig { Snapshot = Rig.Approved() };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        var executed = Assert.IsType<ExecutionResult.Executed>(result);
        Assert.Equal(["started", "executor", "finished:succeeded"], rig.Calls);
        Assert.Equal(ProposalStates.Executed, rig.ToState);
        Assert.Equal("simulated:" + Proposal, executed.Row.ResultRef);
        Assert.True(rig.Disposed);
    }

    [Fact]
    public async Task EXE_02_finished_row_replays_without_writing_or_calling_executor()
    {
        var finished = Rig.Row("finished", "succeeded", resultRef: "r");
        var rig = new Rig { Snapshot = Rig.Approved(started: Rig.Row("started"), finished: finished) with { State = ProposalStates.Executed } };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.Equal(finished, Assert.IsType<ExecutionResult.Replayed>(result).Row);
        Assert.Empty(rig.Calls);
        Assert.True(rig.Disposed);
    }

    [Fact]
    public async Task EXE_05_started_without_finished_becomes_execution_in_doubt_and_never_calls_executor()
    {
        var rig = new Rig { Snapshot = Rig.Approved(started: Rig.Row("started")) };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal(["finished:failed"], rig.Calls);
        Assert.Equal(ExecutionReasons.ExecutionInDoubt, rig.FinishedReason);
        Assert.Equal(ProposalStates.ExecutionFailed, rig.ToState);
        Assert.True(rig.Disposed);
    }

    [Fact]
    public async Task EXE_03_expired_proposal_is_refused_as_proposal_expired_and_never_calls_executor()
    {
        var rig = new Rig { Snapshot = Rig.Approved(expires: Now) };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal(["refusal"], rig.Calls);
        Assert.Equal(ExecutionReasons.ProposalExpired, rig.FinishedReason);
        Assert.Equal(ProposalStates.Expired, rig.ToState);
    }

    [Fact]
    public async Task EXE_03_catalog_invalid_is_refused_and_never_calls_executor()
    {
        var rig = new Rig { Snapshot = Rig.Approved(), Executable = false };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal(["refusal"], rig.Calls);
        Assert.Equal(ExecutionReasons.CatalogInvalid, rig.FinishedReason);
        Assert.Equal(ProposalStates.ExecutionFailed, rig.ToState);
    }

    [Fact]
    public async Task EXE_03_expiry_is_checked_before_catalog_when_both_apply()
    {
        var rig = new Rig { Snapshot = Rig.Approved(expires: Now.AddSeconds(-1)), Executable = false };
        await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.Equal(ExecutionReasons.ProposalExpired, rig.FinishedReason);
    }

    [Fact]
    public async Task EXE_05_executor_exception_becomes_executor_failed_without_storing_exception_text()
    {
        var rig = new Rig { Snapshot = Rig.Approved(), ExecutorThrows = true };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        var failed = Assert.IsType<ExecutionResult.Failed>(result);
        Assert.Equal(["started", "executor", "finished:failed"], rig.Calls);
        Assert.Equal(ExecutionReasons.ExecutorFailed, failed.Row.Reason);
        Assert.DoesNotContain("SECRET", failed.Row.Reason);
        Assert.Equal(ProposalStates.ExecutionFailed, rig.ToState);
    }

    [Fact]
    public async Task EXE_05_finished_write_failure_propagates_and_leaves_in_doubt_shape()
    {
        var rig = new Rig { Snapshot = Rig.Approved(), FinishThrows = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service().ExecuteAsync(Tenant, Proposal, Ct));
        Assert.Equal(["started", "executor"], rig.Calls);
        Assert.True(rig.Disposed);
    }

    [Fact]
    public async Task EXE_02_lock_timeout_is_returned_when_no_attempt_can_begin()
    {
        var rig = new Rig { LockTimesOut = true };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.LockTimeout>(result);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task EXE_01_unknown_proposal_is_not_found()
    {
        var rig = new Rig { Snapshot = null };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.NotFound>(result);
        Assert.True(rig.Disposed);
    }

    [Theory]
    [InlineData(ProposalStates.AwaitingApproval)]
    [InlineData(ProposalStates.Rejected)]
    [InlineData(ProposalStates.RefusedPolicy)]
    public async Task EXE_01_proposal_not_approved_without_ledger_rows_is_not_approved(string state)
    {
        var rig = new Rig { Snapshot = Rig.Approved() with { State = state } };
        var result = await rig.Service().ExecuteAsync(Tenant, Proposal, Ct);

        Assert.IsType<ExecutionResult.NotApproved>(result);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public void EXE_05_reasons_are_static_snake_case_constants()
    {
        foreach (var r in new[] { ExecutionReasons.CatalogInvalid, ExecutionReasons.ProposalExpired, ExecutionReasons.ExecutionInDoubt, ExecutionReasons.ExecutorFailed })
        {
            Assert.Matches("^[a-z][a-z_]{0,63}$", r);
        }
    }
}
