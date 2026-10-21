using Olive.Domain;

namespace Olive.Application;

// D-14: no introspection cache in Phase 1; every call introspects.
// POPEYE_v0.4.2 section 2.2 constraint for the future cache: positives at most 60 s and never past expires_at; negatives never cached.
public interface IKeyIntrospector { Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct); }
