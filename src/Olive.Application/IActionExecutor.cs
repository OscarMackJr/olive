namespace Olive.Application;

// D-13: the only seam to a target system. The simulated executor implements it now; a real one would implement the same port later.
// The request carries allow-listed data only (D-10): no session key, token, rationale or justification is representable.
public interface IActionExecutor
{
    Task<ExecutionTargetResult> ExecuteAsync(ExecutionRequest request, CancellationToken ct);
}

public sealed record ExecutionRequest(Guid TenantId, Guid ProposalId, string ActionId, string TargetSystem, string ParametersJson, DateTimeOffset ExecutedAt);

public sealed record ExecutionTargetResult(string ResultRef);
