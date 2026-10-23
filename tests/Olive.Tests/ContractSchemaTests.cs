using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class ContractSchemaTests
{
    private static readonly string[] Folders =
    [
        "contracts/proposal/v1", "contracts/catalog/v1", "contracts/queue-item/v1", "contracts/execution-record/v0", "contracts/trace-resolution", "contracts/identity", "contracts/reviewer",
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

    [Theory]
    [InlineData("contracts/proposal/v1/proposal-submit-response.schema.json", "contracts/proposal/v1/proposal-submit-response.refused.valid.json", true)]
    [InlineData("contracts/proposal/v1/proposal-submit-response.schema.json", "contracts/proposal/v1/proposal-submit-response.policy-refused.valid.json", true)]
    [InlineData("contracts/proposal/v1/proposal.schema.json", "contracts/proposal/v1/proposal.policy-refused.valid.json", true)]
    [InlineData("contracts/proposal/v1/proposal-submit-response.schema.json", "contracts/proposal/v1/proposal-submit-response.badreason.invalid.json", false)]
    [InlineData("contracts/proposal/v1/proposal.schema.json", "contracts/proposal/v1/proposal.refused.valid.json", true)]
    [InlineData("contracts/proposal/v1/proposal.schema.json", "contracts/proposal/v1/proposal.badtrace.invalid.json", false)]
    [Trait("Category", "Unit")]
    public void TRC_03_refusal_and_trace_examples_are_additive_and_validated(string schemaRel, string exampleRel, bool shouldPass)
    {
        var violation = Check(schemaRel, File.ReadAllText(Path.Combine(TestPaths.Root, exampleRel)));
        Assert.Equal(shouldPass, violation is null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void POL_02_directory_fixtures_validate_and_a_missing_tenant_id_is_rejected()
    {
        const string schema = "contracts/directory/directory-groups.schema.json";
        var files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(TestPaths.Root, "contracts/directory/index.json")))!;
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            Assert.Null(Check(schema, File.ReadAllText(Path.Combine(TestPaths.Root, "contracts/directory", f))));
        }

        Assert.NotNull(Check(schema, "{\"on_behalf_of\":\"u@example.test\",\"groups\":[]}"));
        Assert.NotNull(Check(schema, "{\"tenant_id\":\"5325ffe9-5727-4d88-9832-aebabb8d0538\",\"on_behalf_of\":\"u\",\"groups\":[],\"x\":1}"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_03_reason_and_trace_fields_are_optional_in_the_schemas()
    {
        foreach (var schema in new[] { "proposal-submit-response", "proposal" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, $"contracts/proposal/v1/{schema}.schema.json")));
            var required = doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.DoesNotContain("refusal_reason", required);
            Assert.DoesNotContain("trace_verification", required);
            Assert.DoesNotContain("trace_schema_version", required);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_01_accepted_create_body_serializes_only_proposal_id_and_state()
    {
        var json = JsonSerializer.Serialize(new Olive.Contracts.ProposalCreatedResponse(Guid.NewGuid(), "proposed"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["proposal_id", "state"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TRC_03_refused_create_body_adds_refusal_reason_only()
    {
        var json = JsonSerializer.Serialize(new Olive.Contracts.ProposalCreatedResponse(Guid.NewGuid(), "refused_policy", "trace_not_found"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["proposal_id", "state", "refusal_reason"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Null(Check("contracts/proposal/v1/proposal-submit-response.schema.json", json));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_proposal_folder_has_the_three_v1_schemas()
    {
        var names = Directory.GetFiles(Path.Combine(TestPaths.Root, "contracts/proposal/v1"), "*.schema.json")
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["proposal-request.schema.json", "proposal-submit-response.schema.json", "proposal.schema.json"], names);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("28800", true)]
    [InlineData("1.0", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("1.5", false)]
    [InlineData("\"10\"", false)]
    [Trait("Category", "Unit")]
    public void POL_07_request_schema_expires_in_seconds_is_integer_minimum_1(string value, bool ok)
    {
        var body = $$"""{"run_id":"r","action_type":"crm.case_note@1","party_ref":"p","parameters":{},"justification":"j","trace_ref":"t","expires_in_seconds":{{value}}}""";
        Assert.Equal(ok, Check("contracts/proposal/v1/proposal-request.schema.json", body) is null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void POL_07_proposal_schema_accepts_optional_expires_at() =>
        Assert.Null(Check("contracts/proposal/v1/proposal.schema.json", File.ReadAllText(Path.Combine(TestPaths.Root, "contracts/proposal/v1/proposal.valid.json"))));

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
