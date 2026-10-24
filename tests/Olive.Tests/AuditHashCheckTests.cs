using System.Text.Json;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class AuditHashCheckTests
{
    private const string Action = "crm.case_note@1";
    private const string Party = "party://p1";
    private const string Parameters = "{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"}";
    private const string Justification = "Customer asked for a follow-up note.";

    private static AuditRecord Build(string action = Action, string party = Party, string parameters = Parameters, string justification = Justification)
    {
        // The stored hash comes from the original body; the record under test may carry changed or re-spaced fields.
        using var doc = JsonDocument.Parse("{\"action_type\":\"" + Action + "\",\"party_ref\":\"" + Party + "\",\"parameters\":" + Parameters + ",\"justification\":\"" + Justification + "\"}");
        var hash = Jcs.ProposalHash(doc.RootElement);
        return new AuditRecord(Guid.NewGuid(), action, party, parameters, justification, new string('a', 64), hash, "verified", null);
    }

    [Fact]
    public void UI_05_stored_fields_recompute_to_the_stored_hash_even_with_jsonb_spacing()
    {
        Assert.True(AuditHashCheck.Matches(Build()));
        Assert.True(AuditHashCheck.Matches(Build(parameters: "{\"note_ref\": \"payload://n1\", \"case_ref\": \"payload://c1\"}")));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("party")]
    [InlineData("justification")]
    [InlineData("parameter")]
    [InlineData("malformed")]
    public void UI_05_any_changed_or_malformed_field_does_not_match(string which)
    {
        var record = which switch
        {
            "action" => Build(action: "crm.case_note@2"),
            "party" => Build(party: "party://p2"),
            "justification" => Build(justification: Justification.Replace("follow-up", "follow-Up")),
            "parameter" => Build(parameters: Parameters.Replace("n1", "n2")),
            "malformed" => Build(parameters: "{not json"),
            _ => throw new InvalidOperationException(),
        };
        Assert.False(AuditHashCheck.Matches(record));
    }
}
