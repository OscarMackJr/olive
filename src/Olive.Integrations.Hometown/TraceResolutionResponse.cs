using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olive.Application;
using Olive.Domain;

namespace Olive.Integrations.Hometown;

// Strict parser for the hometown trace-resolution response: exactly seven fields, correct kinds, nothing else.
// Anything off returns Malformed and never throws (fail closed).
public static partial class TraceResolutionResponse
{
    private static readonly string[] Fields = ["trace_id", "schema_version", "run_id", "tenant_id", "agent_id", "proposal_hash", "created_at"];

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("^ate/[0-9]+\\.[0-9]+$")]
    private static partial Regex SchemaVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    public static TraceLookup Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TraceLookup.Malformed();
            }

            // Each allowed field exactly once: a duplicate key is malformed, because which duplicate wins is parser-dependent (WR-03).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (Array.IndexOf(Fields, p.Name) < 0 || !seen.Add(p.Name))
                {
                    return new TraceLookup.Malformed();
                }
            }

            if (seen.Count != Fields.Length)
            {
                return new TraceLookup.Malformed();
            }

            if (!Str(root, "trace_id", false, out var traceId)
                || !Str(root, "schema_version", false, out var schemaVersion) || !SchemaVersionPattern().IsMatch(schemaVersion!)
                || !Str(root, "run_id", true, out var runId)
                || !Str(root, "tenant_id", false, out var tenantText) || !UuidPattern().IsMatch(tenantText!)
                || !Str(root, "agent_id", true, out var agentId)
                || !Str(root, "proposal_hash", true, out var hash) || (hash is not null && !HashPattern().IsMatch(hash))
                || !Str(root, "created_at", false, out var createdText)
                || !DateTimeOffset.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt)
                || !Guid.TryParse(tenantText, out var tenantId))
            {
                return new TraceLookup.Malformed();
            }

            return new TraceLookup.Found(new TraceFacts(traceId!, schemaVersion!, runId, tenantId, agentId, hash, createdAt), TraceVerificationSources.Fixture);
        }
        catch (JsonException)
        {
            return new TraceLookup.Malformed();
        }
    }

    private static bool Str(JsonElement root, string name, bool nullable, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var v))
        {
            return false;
        }

        if (v.ValueKind == JsonValueKind.Null)
        {
            return nullable;
        }

        if (v.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(v.GetString()))
        {
            return false;
        }

        value = v.GetString();
        return true;
    }
}
