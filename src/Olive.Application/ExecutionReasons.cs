namespace Olive.Application;

// D-09: failure reasons are static constants. Exception text and parameter values are never stored. Each matches ^[a-z][a-z_]{0,63}$.
public static class ExecutionReasons
{
    public const string CatalogInvalid = "catalog_invalid";
    public const string ProposalExpired = "proposal_expired";
    public const string ExecutionInDoubt = "execution_in_doubt";
    public const string ExecutorFailed = "executor_failed";
}
