namespace Olive.Domain;

// Static reason codes only. All match ^[a-z][a-z_]{0,63}$, the pattern of the proposal_refusal_reason_format constraint (0006).
public static class PolicyRefusalReasons
{
    public const string NotInCatalog = "not_in_catalog";
    public const string PurposeNotPermitted = "purpose_not_permitted";
    public const string EntitlementMissing = "entitlement_missing";
    public const string RateLimited = "rate_limited";
}
