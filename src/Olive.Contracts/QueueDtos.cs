using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olive.Contracts;

// Reference-only (IR-1 v1). No member carries proposal content or free reviewer text (D-14).
public sealed record QueueItemDto(
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("subtype")] string Subtype,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("producer_ref")] Guid ProducerRef,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("subject_ref")] string SubjectRef,
    [property: JsonPropertyName("payload_ref")] string PayloadRef,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("age_seconds")] long AgeSeconds,
    [property: JsonPropertyName("claimed_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClaimedBy = null,
    [property: JsonPropertyName("expires_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("decision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decision = null,
    [property: JsonPropertyName("decided_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DecidedBy = null,
    [property: JsonPropertyName("decided_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? DecidedAt = null,
    [property: JsonPropertyName("auth_source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AuthSource = null,
    [property: JsonPropertyName("withdrawn_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WithdrawnReason = null,
    [property: JsonPropertyName("ruleset_version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RulesetVersion = null);

// Request body for the decision route. Only these two members are accepted; the rationale is write-only (never echoed).
public sealed record DecisionRequest(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("rationale")] string Rationale);

public sealed record QueueItemListResponse([property: JsonPropertyName("items")] IReadOnlyList<QueueItemDto> Items);

// D-05: the only place proposal content leaves the system for a reviewer. Exactly these members.
public sealed record ReviewerPayloadResponse(
    [property: JsonPropertyName("action_type")] string? ActionType,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters,
    [property: JsonPropertyName("justification")] string? Justification,
    [property: JsonPropertyName("on_behalf_of")] string OnBehalfOf,
    [property: JsonPropertyName("expires_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("trace_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TraceRef);

// QUE-09: no rationale member exists on the history DTO.
public sealed record QueueItemEventDto(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("from_state")] string? FromState,
    [property: JsonPropertyName("to_state")] string ToState,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Detail = null);

public sealed record QueueItemEventsResponse([property: JsonPropertyName("events")] IReadOnlyList<QueueItemEventDto> Events);

// Slim create response: no claimed_by or any reviewer identity may reach the producer.
public sealed record CreateItemResponse([property: JsonPropertyName("item_id")] Guid ItemId, [property: JsonPropertyName("state")] string State);
