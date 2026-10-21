using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olive.Contracts;

public sealed record ProposalCreatedResponse(
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("state")] string State);

public sealed record ProposalResponse(
    [property: JsonPropertyName("proposal_id")] Guid ProposalId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("tenant_id")] Guid TenantId,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("on_behalf_of")] string OnBehalfOf,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("key_expires_at")] DateTimeOffset KeyExpiresAt,
    [property: JsonPropertyName("nono_session_id")] string? NonoSessionId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("action_type")] string? ActionType,
    [property: JsonPropertyName("party_ref")] string? PartyRef,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters,
    [property: JsonPropertyName("justification")] string? Justification,
    [property: JsonPropertyName("trace_ref")] string? TraceRef,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);
