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
    [property: JsonPropertyName("withdrawn_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WithdrawnReason = null);

public sealed record QueueItemListResponse([property: JsonPropertyName("items")] IReadOnlyList<QueueItemDto> Items);
