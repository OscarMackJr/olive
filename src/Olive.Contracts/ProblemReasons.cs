namespace Olive.Contracts;

// Stable machine-readable reason codes. Phase 9's OpenAPI documents these; Phase 2 replaces duplicate_run with idempotency_conflict.
public static class ProblemReasons
{
    public const string KeyInactive = "key_inactive";
    public const string PrincipalNotPermitted = "principal_not_permitted";
    public const string AgentNotAllowlisted = "agent_not_allowlisted";
    public const string InvalidRequest = "invalid_request";
    public const string ProposalNotFound = "proposal_not_found";
    public const string DuplicateRun = "duplicate_run";
    public const string CatalogVersionNotFound = "catalog_version_not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string IntrospectionUnavailable = "introspection_unavailable";
    public const string InternalError = "internal_error";
}
