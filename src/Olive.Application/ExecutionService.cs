using Olive.Domain;

namespace Olive.Application;

// Execution orchestrator (D-02, D-05..D-12). Business outcomes are returned, never thrown. Exception text is never read (D-09).
public sealed class ExecutionService(IExecutionStore store, IActionExecutor executor, IActionCatalogCheck catalogCheck, TimeProvider time)
{
    public async Task<ExecutionResult> ExecuteAsync(Guid tenantId, Guid proposalId, CancellationToken ct)
    {
        await using var attempt = await store.BeginAttemptAsync(tenantId, proposalId, ct);
        if (attempt is null) { return new ExecutionResult.LockTimeout(); }

        var snap = await attempt.LoadAsync(ct);
        if (snap is null) { return new ExecutionResult.NotFound(); }

        // D-02: a finished row means replay; nothing is written and the executor is not called.
        if (snap.Finished is not null) { return new ExecutionResult.Replayed(snap.Finished); }

        // D-08: started without finished means a prior attempt may have written to the target. Never write again.
        if (snap.Started is not null)
        {
            var row = await attempt.RecordFinishedAsync(new FinishedOutcome(false, ExecutionReasons.ExecutionInDoubt, null), ProposalStates.ExecutionFailed, time.GetUtcNow(), ct);
            return new ExecutionResult.Failed(row);
        }

        if (snap.State != ProposalStates.Approved) { return new ExecutionResult.NotApproved(); }

        var now = time.GetUtcNow();

        // Expiry is checked before the catalog (documented choice; D-05/D-06 do not order them).
        if (snap.ExpiresAt <= now && ProposalStates.CanTransition(snap.State, ProposalStates.Expired))
        {
            return new ExecutionResult.Failed(await attempt.RecordRefusalAsync(ExecutionReasons.ProposalExpired, ProposalStates.Expired, now, ct));
        }

        var check = catalogCheck.Check(snap.ActionType, snap.ParametersJson);
        if (!check.Executable || check.TargetSystem is null)
        {
            return new ExecutionResult.Failed(await attempt.RecordRefusalAsync(ExecutionReasons.CatalogInvalid, ProposalStates.ExecutionFailed, now, ct));
        }

        // D-12: started row committed, then the target write, then finished plus state change.
        await attempt.RecordStartedAsync(now, ct);

        ExecutionTargetResult target;
        try
        {
            target = await executor.ExecuteAsync(new ExecutionRequest(tenantId, proposalId, snap.ActionType, check.TargetSystem, snap.ParametersJson, now), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            var failed = await attempt.RecordFinishedAsync(new FinishedOutcome(false, ExecutionReasons.ExecutorFailed, null), ProposalStates.ExecutionFailed, time.GetUtcNow(), ct);
            return new ExecutionResult.Failed(failed);
        }

        var done = await attempt.RecordFinishedAsync(new FinishedOutcome(true, null, target.ResultRef), ProposalStates.Executed, time.GetUtcNow(), ct);
        return new ExecutionResult.Executed(done);
    }
}

public abstract record ExecutionResult
{
    public sealed record Executed(LedgerRow Row) : ExecutionResult;

    public sealed record Replayed(LedgerRow Row) : ExecutionResult;

    public sealed record Failed(LedgerRow Row) : ExecutionResult;

    public sealed record NotApproved : ExecutionResult;

    public sealed record NotFound : ExecutionResult;

    public sealed record LockTimeout : ExecutionResult;
}
