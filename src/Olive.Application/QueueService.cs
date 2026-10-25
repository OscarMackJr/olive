using Olive.Domain;

namespace Olive.Application;

// Reviewer-facing queue application service (D-01). Shared by the Host JSON API and the Phase 7 pages. Business outcomes are
// result values, never exceptions. Time comes from the injected provider.
public sealed class QueueService(IQueueStore store, IQueueKindRegistry registry, ApprovalIdentityCheck identityCheck, TimeProvider time)
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
        return new QueueListResult.Ok(rows.Select(r => new QueueItemView(r.TenantId, r.ItemId, r.Kind, r.Subtype, r.Producer, r.ProducerRef, r.State, r.SubjectRef, r.PayloadRef, r.ClaimedBy, r.CreatedAt, AgeSeconds(r.CreatedAt, now), r.ExpiresAt, null, null, null, null, null, null, r.RulesetVersion)).ToList());
    }

    public async Task<QueueItemView?> GetAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        var d = await store.GetAsync(reviewer.TenantId, itemId, ct);
        if (d is null) { return null; }
        return new QueueItemView(d.TenantId, d.ItemId, d.Kind, d.Subtype, d.Producer, d.ProducerRef, d.State, d.SubjectRef, d.PayloadRef, d.ClaimedBy, d.CreatedAt, AgeSeconds(d.CreatedAt, time.GetUtcNow()), d.ExpiresAt, d.Decision, d.DecidedBy, d.DecidedAt, d.AuthSource, d.WithdrawnReason, d.ProposalId, d.RulesetVersion);
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

    // claimed_by holds the Entra oid (A4). Role is deliberately not required to claim or release.
    public Task<ClaimResult> ClaimAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewer.Oid)) { throw new ArgumentException("A reviewer oid is required.", nameof(reviewer)); }
        return store.ClaimAsync(reviewer.TenantId, itemId, reviewer.Oid, time.GetUtcNow(), ct);
    }

    public Task<ReleaseResult> ReleaseAsync(ReviewerContext reviewer, Guid itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewer.Oid)) { throw new ArgumentException("A reviewer oid is required.", nameof(reviewer)); }
        return store.ReleaseAsync(reviewer.TenantId, itemId, reviewer.Oid, time.GetUtcNow(), ct);
    }

    public const int MaxRationaleLength = 4000;

    // D-09/D-06/D-18. The pre-read only decides which checks to run; the store re-validates everything under the row lock.
    // The rationale is passed to the store once and is never logged, returned or placed in a result.
    public async Task<DecisionResult> DecideAsync(ReviewerContext reviewer, Guid itemId, string? decision, string? rationale, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewer.Oid)) { throw new ArgumentException("A reviewer oid is required.", nameof(reviewer)); }

        var item = await store.GetAsync(reviewer.TenantId, itemId, ct);
        if (item is null) { return new DecisionResult.NotFound(); }
        if (item.Kind != QueueStates.ActionApproval || !registry.TryGet(item.Kind, out var definition)) { return new DecisionResult.KindNotSupported(); }
        if (decision is null || !definition.Decisions.Contains(decision, StringComparer.Ordinal)) { return new DecisionResult.InvalidRequest(); }
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > MaxRationaleLength || rationale.Contains('\0')) { return new DecisionResult.InvalidRequest(); }
        if (!reviewer.Groups.Contains(definition.RequiredRole, StringComparer.Ordinal)) { return new DecisionResult.RoleNotPermitted(); }

        var now = time.GetUtcNow();
        var live = item.State == QueueStates.Claimed
            && string.Equals(item.ClaimedBy, reviewer.Oid, StringComparison.Ordinal)
            && (item.ExpiresAt is null || item.ExpiresAt > now);
        var identityVerified = false;
        if (decision == QueueStates.Approve && live)
        {
            var payload = await store.GetPayloadAsync(reviewer.TenantId, itemId, ct);
            if (payload is null) { return new DecisionResult.NotFound(); }
            var verdict = await identityCheck.CheckAsync(reviewer.TenantId, payload.OnBehalfOf, reviewer.Oid, reviewer.Tid, ct);
            switch (verdict)
            {
                case ApprovalIdentityVerdict.SelfApproval: return new DecisionResult.SelfApproval();
                case ApprovalIdentityVerdict.Unresolved: return new DecisionResult.Unresolved();
                case ApprovalIdentityVerdict.Unavailable: return new DecisionResult.Unavailable();
            }

            identityVerified = true;
        }

        var result = await store.DecideAsync(reviewer.TenantId, itemId, reviewer.Oid, reviewer.AuthSource, decision, rationale, identityVerified, now, ct);
        return result switch
        {
            DecideResult.Decided d => new DecisionResult.Decided(d.Item),
            DecideResult.AlreadyDecidedSame d => new DecisionResult.AlreadyDecidedSame(d.Item),
            DecideResult.Conflict => new DecisionResult.Conflict(),
            DecideResult.NotClaimant => new DecisionResult.NotClaimant(),
            DecideResult.NotClaimed => new DecisionResult.NotClaimed(),
            DecideResult.Closed => new DecisionResult.Closed(),
            DecideResult.ProposalExpired => new DecisionResult.ProposalExpired(),
            DecideResult.NotFound => new DecisionResult.NotFound(),
            _ => throw new InvalidOperationException("Unknown decide result."),
        };
    }

    public QueueItemView ToView(QueueItemDetail d) => new(d.TenantId, d.ItemId, d.Kind, d.Subtype, d.Producer, d.ProducerRef, d.State, d.SubjectRef, d.PayloadRef, d.ClaimedBy, d.CreatedAt, AgeSeconds(d.CreatedAt, time.GetUtcNow()), d.ExpiresAt, d.Decision, d.DecidedBy, d.DecidedAt, d.AuthSource, d.WithdrawnReason, d.ProposalId, d.RulesetVersion);

    private static readonly System.Text.RegularExpressions.Regex RulesetVersionPattern = new("^[A-Za-z0-9._-]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Sha256Pattern = new("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // IRQ-01: producer and tenant come only from the verified ProducerContext. Every failure is a static InvalidRequest (no value echoed).
    public async Task<CreateResult> CreateAsync(ProducerContext producer, CreateItemRequest request, string bodySha256, CancellationToken ct)
    {
        var invalid = new CreateResult.InvalidRequest();
        if (!registry.Kinds.Contains(request.Kind, StringComparer.Ordinal)
            || !producer.KindSubtypes.TryGetValue(request.Kind, out var subtypes)
            || !subtypes.Contains(request.Subtype)) { return invalid; }
        if (!Guid.TryParseExact(request.ProducerRef, "D", out var producerRef) || producerRef.ToString("D") != request.ProducerRef) { return invalid; }
        if (request.SubjectRef != "bluto:candidate/" + request.ProducerRef || request.PayloadRef != "bluto:review-evidence/" + request.ProducerRef) { return invalid; }
        if (!RulesetVersionPattern.IsMatch(request.RulesetVersion) || !Sha256Pattern.IsMatch(bodySha256)) { return invalid; }

        var result = await store.CreateIdentityItemAsync(producer.TenantId, producer.Producer, request.Kind, request.Subtype, producerRef,
            request.SubjectRef, request.PayloadRef, request.RulesetVersion, bodySha256, time.GetUtcNow(), ct);
        return result switch
        {
            CreateIdentityResult.Created c => new CreateResult.Created(c.ItemId, c.State),
            CreateIdentityResult.Replay r => new CreateResult.Replay(r.ItemId, r.State),
            CreateIdentityResult.Conflict => new CreateResult.Conflict(),
            _ => throw new InvalidOperationException("Unknown create result."),
        };
    }

    private static long AgeSeconds(DateTimeOffset created, DateTimeOffset now) => Math.Max(0, (long)(now - created).TotalSeconds);
}

public abstract record QueueListResult
{
    public sealed record Ok(IReadOnlyList<QueueItemView> Items) : QueueListResult;

    public sealed record Invalid : QueueListResult;
}

// Reference-only view; no rationale, parameters, justification or on_behalf_of member exists.
public sealed record QueueItemView(Guid TenantId, Guid ItemId, string Kind, string Subtype, string Producer, Guid ProducerRef, string State, string SubjectRef, string PayloadRef, string? ClaimedBy, DateTimeOffset CreatedAt, long AgeSeconds, DateTimeOffset? ExpiresAt, string? Decision, string? DecidedBy, DateTimeOffset? DecidedAt, string? AuthSource, string? WithdrawnReason, Guid? ProposalId = null, string? RulesetVersion = null);

public abstract record PayloadResult
{
    public sealed record Found(ReviewerPayload Payload) : PayloadResult;

    public sealed record NotFound : PayloadResult;

    public sealed record RoleNotPermitted : PayloadResult;
}

public abstract record DecisionResult
{
    public sealed record Decided(QueueItemDetail Item) : DecisionResult;

    public sealed record AlreadyDecidedSame(QueueItemDetail Item) : DecisionResult;

    public sealed record Conflict : DecisionResult;

    public sealed record NotClaimant : DecisionResult;

    public sealed record NotClaimed : DecisionResult;

    public sealed record Closed : DecisionResult;

    public sealed record ProposalExpired : DecisionResult;

    public sealed record NotFound : DecisionResult;

    public sealed record RoleNotPermitted : DecisionResult;

    public sealed record SelfApproval : DecisionResult;

    public sealed record Unresolved : DecisionResult;

    public sealed record Unavailable : DecisionResult;

    public sealed record KindNotSupported : DecisionResult;

    public sealed record InvalidRequest : DecisionResult;
}

public abstract record EventsResult
{
    public sealed record Found(IReadOnlyList<QueueItemEventRecord> Events) : EventsResult;

    public sealed record NotFound : EventsResult;
}

public sealed record CreateItemRequest(string Kind, string Subtype, string ProducerRef, string SubjectRef, string PayloadRef, string RulesetVersion);

public abstract record CreateResult
{
    public sealed record Created(Guid ItemId, string State) : CreateResult;

    public sealed record Replay(Guid ItemId, string State) : CreateResult;

    public sealed record Conflict : CreateResult;

    public sealed record InvalidRequest : CreateResult;
}
