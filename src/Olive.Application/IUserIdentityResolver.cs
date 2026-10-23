namespace Olive.Application;

public abstract record UserIdentityLookup
{
    public sealed record Resolved(string Oid, string Tid) : UserIdentityLookup;
    public sealed record NotFound : UserIdentityLookup;
    public sealed record Ambiguous : UserIdentityLookup;
    public sealed record Unavailable : UserIdentityLookup;
    public sealed record Malformed : UserIdentityLookup;
}

// D-06 (Q14 ruling): read-only. No session key and no service token by construction. onBehalfOf is an opaque UPN-style string, passed verbatim,
// and resolves to exactly one Entra (oid, tid); UPN strings are never compared directly.
[OutboundPort(OutboundDestination.Directory)]
public interface IUserIdentityResolver { Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct); }
