using Olive.Application;

namespace Olive.Host.Endpoints;

// Shared by the JSON API and the reviewer pages (UI-04, Phase 6 D-01): execution starts only after the approval transaction has
// committed, on its own connections. CancellationToken.None keeps a client disconnect from leaving an in-doubt attempt. A fault never
// changes the decide response (D-04) and is logged statically (D-09).
internal static class ApprovalExecution
{
    public static async Task RunAsync(DecisionResult result, ExecutionService execution, ILoggerFactory loggerFactory)
    {
        var item = result switch
        {
            DecisionResult.Decided d => d.Item,
            DecisionResult.AlreadyDecidedSame d => d.Item,
            _ => null,
        };
        if (item is null || item.Decision != "approve" || item.ProposalId is not { } proposalId) { return; }

        // WR-03: identifiers are logged as structured fields (they are neither secrets nor bodies); exception text never is.
        var log = loggerFactory.CreateLogger("Olive.Host.Execution");
        try
        {
            var outcome = await execution.ExecuteAsync(item.TenantId, proposalId, CancellationToken.None);
            if (outcome is ExecutionResult.LockTimeout)
            {
                log.LogWarning("Simulated execution lock wait timed out after approval. proposal_id={ProposalId} tenant_id={TenantId}", proposalId, item.TenantId);
            }
        }
        catch (Exception)
        {
            log.LogError("Simulated execution failed after approval. proposal_id={ProposalId} tenant_id={TenantId}", proposalId, item.TenantId);
        }
    }
}
