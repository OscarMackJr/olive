namespace Olive.Contracts;

// Stable machine-readable reason codes. Phase 9's OpenAPI documents these;
public static class ProblemReasons
{
    public const string KeyInactive = "key_inactive";
    public const string PrincipalNotPermitted = "principal_not_permitted";
    public const string AgentNotAllowlisted = "agent_not_allowlisted";
    public const string InvalidRequest = "invalid_request";
    public const string ProposalNotFound = "proposal_not_found";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string CatalogVersionNotFound = "catalog_version_not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string IntrospectionUnavailable = "introspection_unavailable";
    public const string SchemaViolation = "schema_violation";
    public const string NotInCatalog = "not_in_catalog";
    public const string TraceNotFound = "trace_not_found";
    public const string TraceVerificationUnavailable = "trace_verification_unavailable";
    public const string DirectoryUnavailable = "directory_unavailable";
    public const string ReviewerUnauthenticated = "reviewer_unauthenticated";
    public const string ReviewerTenantUnresolved = "reviewer_tenant_unresolved";
    public const string RoleNotPermitted = "role_not_permitted";
    public const string ItemNotFound = "item_not_found";
    public const string AlreadyClaimed = "already_claimed";
    public const string NotClaimant = "not_claimant";
    public const string ItemNotClaimed = "item_not_claimed";
    public const string ItemClosed = "item_closed";
    public const string ProposalExpired = "proposal_expired";
    public const string DecisionConflict = "decision_conflict";
    public const string SelfApprovalForbidden = "self_approval_forbidden";
    public const string ApprovalIdentityUnresolved = "approval_identity_unresolved";
    public const string IdentityResolutionUnavailable = "identity_resolution_unavailable";
    public const string KindNotSupported = "kind_not_supported";
    public const string RoleNotHeld = "role_not_held";
    public const string GroupsOverage = "groups_overage";
    public const string NoReviewerTenant = "no_reviewer_tenant";
    public const string TenantNotPermitted = "tenant_not_permitted";
    public const string ProducerNotRegistered = "producer_not_registered";
    public const string InternalError = "internal_error";
}
