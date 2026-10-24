namespace Olive.Application;

public interface IExecutionStore
{
    // Takes the per-proposal lock. Null means the lock timed out.
    Task<IExecutionAttempt?> BeginAttemptAsync(Guid tenantId, Guid proposalId, CancellationToken ct);

    Task<IReadOnlyList<Guid>> ListApprovedAsync(Guid tenantId, CancellationToken ct);
}

// One locked attempt. RecordFinishedAsync commits the finished row, the proposal state change and the proposal_event in one
// transaction (D-07). RecordStartedAsync commits on its own so a crash after the target write leaves a started-only shape (D-12).
public interface IExecutionAttempt : IAsyncDisposable
{
    Task<ExecutionSnapshot?> LoadAsync(CancellationToken ct);

    Task<LedgerRow> RecordStartedAsync(DateTimeOffset now, CancellationToken ct);

    Task<LedgerRow> RecordFinishedAsync(FinishedOutcome outcome, string toState, DateTimeOffset now, CancellationToken ct);

    // Writes started and finished together; used when the executor must not be called (D-05, D-06, D-08).
    Task<LedgerRow> RecordRefusalAsync(string reason, string toState, DateTimeOffset now, CancellationToken ct);
}

public sealed record FinishedOutcome(bool Succeeded, string? Reason, string? ResultRef);

public sealed record LedgerRow(Guid ExecutionId, string Phase, string? Outcome, string? Reason, string? ResultRef, DateTimeOffset RecordedAt);

public sealed record ExecutionSnapshot(string State, string ActionType, string ParametersJson, DateTimeOffset ExpiresAt, LedgerRow? Started, LedgerRow? Finished);
