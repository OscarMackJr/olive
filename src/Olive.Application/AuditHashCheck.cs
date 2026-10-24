using System.Text.Json;
using Olive.Domain;

namespace Olive.Application;

// D-13: recompute proposal_hash from the four stored fields. Fails closed: any parse or shape problem is a mismatch, never an exception.
public static class AuditHashCheck
{
    public static bool Matches(AuditRecord r)
    {
        try
        {
            using var parameters = JsonDocument.Parse(r.ParametersJson);
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("action_type", r.ActionType);
                w.WriteString("party_ref", r.PartyRef);
                w.WritePropertyName("parameters");
                parameters.RootElement.WriteTo(w);
                w.WriteString("justification", r.Justification);
                w.WriteEndObject();
            }

            using var root = JsonDocument.Parse(stream.ToArray());
            return string.Equals(Jcs.ProposalHash(root.RootElement), r.ProposalHash, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
