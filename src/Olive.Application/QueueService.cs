using Olive.Domain;

namespace Olive.Application;

// Reviewer-facing queue application service (D-01). Shared by the Host JSON API and the Phase 7 pages. Business outcomes are
// result values, never exceptions. Time comes from the injected provider.
public sealed class QueueService(IQueueStore store, IQueueKindRegistry registry, TimeProvider time)
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    private static readonly string[] States = [QueueStates.Open, QueueStates.Claimed, QueueStates.Decided, QueueStates.Withdrawn];

    public async Task<QueueListResult> ListAsync(ReviewerContext reviewer, string? kind, string? state, int? limit, CancellationToken ct)
    {
        if (kind is not null && !registry.Kinds.Contains(kind, StringComparer.Ordinal)) { return new QueueListResult.Invalid(); }
        var effectiveState = state ?? QueueStates.Open;
        if (!States.Contains(effectiveState, StringComparer.Ordinal)) { return new QueueListResult.Invalid(); }
        if (limit is < 1 or > MaxLimit) { return new QueueListResult.Invalid(); }

        var now = time.GetUtcNow();
        var rows = await store.ListAsync(reviewer.TenantId, kind, effectiveState, now, limit ?? DefaultLimit, ct);
        return new QueueListResult.Ok(rows.Select(r => new QueueItemView(r.TenantId, r.ItemId, r.Kind, r.Subtype, r.Producer, r.ProducerRef, r.State, r.SubjectRef, r.PayloadRef, r.ClaimedBy, r.CreatedAt, AgeSeconds(r.CreatedAt, now), r.ExpiresAt, null, null, null, null, null)).ToList());
    }

    public async Task<QueueItemView?> GetAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        var d = await store.GetAsync(reviewer.TenantId, itemId, ct);
        if (d is null) { return null; }
        return new QueueItemView(d.TenantId, d.ItemId, d.Kind, d.Subtype, d.Producer, d.ProducerRef, d.State, d.SubjectRef, d.PayloadRef, d.ClaimedBy, d.CreatedAt, AgeSeconds(d.CreatedAt, time.GetUtcNow()), d.ExpiresAt, d.Decision, d.DecidedBy, d.DecidedAt, d.AuthSource, d.WithdrawnReason);
    }

    // D-19: the required role is read from the kind definition in the registry, never written in code.
    public async Task<PayloadResult> GetPayloadAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        var item = await store.GetAsync(reviewer.TenantId, itemId, ct);
        if (item is null) { return new PayloadResult.NotFound(); }
        if (!registry.TryGet(item.Kind, out var definition) || !reviewer.Groups.Contains(definition.RequiredRole, StringComparer.Ordinal))
        {
            return new PayloadResult.RoleNotPermitted();
        }

        var payload = await store.GetPayloadAsync(reviewer.TenantId, itemId, ct);
        return payload is null ? new PayloadResult.NotFound() : new PayloadResult.Found(payload);
    }

    public async Task<EventsResult> EventsAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        var events = await store.EventsAsync(reviewer.TenantId, itemId, ct);
        return events is null ? new EventsResult.NotFound() : new EventsResult.Found(events);
    }

    private static long AgeSeconds(DateTimeOffset created, DateTimeOffset now) => Math.Max(0, (long)(now - created).TotalSeconds);
}

public abstract record QueueListResult
{
    public sealed record Ok(IReadOnlyList<QueueItemView> Items) : QueueListResult;

    public sealed record Invalid : QueueListResult;
}

// Reference-only view; no rationale, parameters, justification or on_behalf_of member exists.
public sealed record QueueItemView(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, long AgeSeconds, DateTimeOffset? ExpiresAt, string? Decision, string? DecidedBy, DateTimeOffset? DecidedAt, string? AuthSource, string? WithdrawnReason);

public abstract record PayloadResult
{
    public sealed record Found(ReviewerPayload Payload) : PayloadResult;

    public sealed record NotFound : PayloadResult;

    public sealed record RoleNotPermitted : PayloadResult;
}

public abstract record EventsResult
{
    public sealed record Found(IReadOnlyList<QueueItemEventRecord> Events) : EventsResult;

    public sealed record NotFound : EventsResult;
}
