namespace Olive.Application;

public abstract record DirectoryLookup
{
    public sealed record Found(IReadOnlyList<string> Groups) : DirectoryLookup;
    public sealed record UnknownUser : DirectoryLookup;
    public sealed record Unavailable : DirectoryLookup;
    public sealed record CallerRejected(int Status) : DirectoryLookup;
    public sealed record Malformed : DirectoryLookup;
}

// D-02: read-only directory lookup. No session key and no service token by construction. onBehalfOf is opaque and verbatim (D-06).
[OutboundPort(OutboundDestination.Directory)]
public interface IDirectoryGroupResolver { Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct); }
