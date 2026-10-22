using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class ContractSchemaTests
{
    private static readonly string[] Folders =
    [
        "contracts/proposal/v1", "contracts/catalog/v1", "contracts/queue-item/v0", "contracts/execution-record/v0",
    ];

    public static TheoryData<string> SchemaFiles()
    {
        var data = new TheoryData<string>();
        foreach (var folder in Folders)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.Root, folder), "*.schema.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(TestPaths.Root, file).Replace('\\', '/'));
            }
        }

        return data;
    }

    private static SchemaViolation? Check(string schemaRel, string instance)
    {
        var validator = SchemaValidator.Compile(File.ReadAllText(Path.Combine(TestPaths.Root, schemaRel)));
        using var doc = JsonDocument.Parse(instance);
        return validator.Validate(doc.RootElement);
    }

    [Theory]
    [MemberData(nameof(SchemaFiles))]
    [Trait("Category", "Unit")]
    public void API_05_every_schema_has_paired_examples_the_real_validator_accepts_and_rejects(string schemaRel)
    {
        var stem = schemaRel[..^".schema.json".Length];
        var valid = File.ReadAllText(Path.Combine(TestPaths.Root, stem + ".valid.json"));
        var invalid = File.ReadAllText(Path.Combine(TestPaths.Root, stem + ".invalid.json"));
        Assert.Null(Check(schemaRel, valid));
        Assert.NotNull(Check(schemaRel, invalid));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_proposal_folder_has_the_three_v1_schemas()
    {
        var names = Directory.GetFiles(Path.Combine(TestPaths.Root, "contracts/proposal/v1"), "*.schema.json")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["proposal-request.schema.json", "proposal-submit-response.schema.json", "proposal.schema.json"], names);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_full_phase1_body_validates_against_request_schema() =>
        Assert.Null(Check("contracts/proposal/v1/proposal-request.schema.json", ApiProposalTests.FullBody));

    [Theory]
    [InlineData("""{"action_type":"crm.case_note@1","party_ref":"p","parameters":{},"justification":"j","trace_ref":"t"}""", "/", "required")]
    [InlineData("""{"run_id":"r","action_type":"crm.case_note@1","party_ref":"p","parameters":{},"justification":"j","trace_ref":"t","nono_session_id":null}""", "/nono_session_id", "type")]
    [InlineData("""{"run_id":"r","action_type":"crm.case_note@1","party_ref":"p","parameters":{},"justification":"j","trace_ref":"t","extra":1}""", "/", "additionalProperties")]
    [InlineData("""{"run_id":"r","action_type":"crm.case_note@1","party_ref":"p","parameters":[],"justification":"j","trace_ref":"t"}""", "/parameters", "type")]
    [Trait("Category", "Unit")]
    public void API_05_request_schema_rejects_bad_envelopes(string body, string pointer, string keyword)
    {
        var v = Check("contracts/proposal/v1/proposal-request.schema.json", body);
        Assert.NotNull(v);
        Assert.Equal(keyword, v!.Keyword);
        Assert.Equal(pointer, v.Pointer == "" ? "/" : v.Pointer);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_format_assertion_is_on_for_proposal_id()
    {
        var v = Check("contracts/proposal/v1/proposal-submit-response.schema.json", """{"proposal_id":"not-a-uuid","state":"proposed"}""");
        Assert.Equal(new SchemaViolation("/proposal_id", "format"), v);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_legacy_contract_directories_no_longer_exist()
    {
        Assert.False(Directory.Exists(Path.Combine(TestPaths.Root, "contracts", "schemas")));
        Assert.False(Directory.Exists(Path.Combine(TestPaths.Root, "contracts", "examples")));
    }
}
