namespace Olive.Application;

// The authenticated reviewer as later plans see it. TenantId comes only from the server-side group map (D-08).
public sealed record ReviewerContext(Guid TenantId, string Oid, string Tid, IReadOnlyList<string> Groups, string AuthSource);
