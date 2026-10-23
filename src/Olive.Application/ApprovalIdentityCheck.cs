namespace Olive.Application;

public abstract record ApprovalIdentityVerdict
{
    public sealed record Allowed : ApprovalIdentityVerdict;
    public sealed record SelfApproval : ApprovalIdentityVerdict;
    public sealed record Unresolved : ApprovalIdentityVerdict;
    // Cause is a static code; ExceptionType is the exception type name, never its message.
    public sealed record Unavailable(string Cause, string? ExceptionType = null) : ApprovalIdentityVerdict;
}

// APR-02 / D-06: the only place the self-approval comparison lives. oid+tid equality of the resolved requester and the reviewer; UPNs are never compared.
// Only the approve path calls this (D-18: reject is exempt). Nothing here is written to any sink.
public sealed class ApprovalIdentityCheck(UserIdentityVerifier verifier)
{
    public async Task<ApprovalIdentityVerdict> CheckAsync(Guid tenantId, string onBehalfOf, string reviewerOid, string reviewerTid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(onBehalfOf))
        {
            return new ApprovalIdentityVerdict.Unresolved();
        }

        var (lookup, cause, exceptionType) = await verifier.ResolveAsync(tenantId, onBehalfOf, ct);
        switch (lookup)
        {
            case UserIdentityLookup.Resolved r:
                return string.Equals(r.Oid, reviewerOid, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Tid, reviewerTid, StringComparison.OrdinalIgnoreCase)
                    ? new ApprovalIdentityVerdict.SelfApproval()
                    : new ApprovalIdentityVerdict.Allowed();
            case UserIdentityLookup.NotFound:
            case UserIdentityLookup.Ambiguous:
            case UserIdentityLookup.Malformed:
                return new ApprovalIdentityVerdict.Unresolved();
            default:
                return new ApprovalIdentityVerdict.Unavailable(cause ?? "identity_resolver_unavailable", exceptionType);
        }
    }
}
