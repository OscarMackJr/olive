using System.Text.Json;
using Olive.Catalog;

namespace Olive.Tests;

public sealed class SubmissionValidatorTests
{
    private static readonly SubmissionValidator Validator = new(CatalogLoader.LoadEmbedded(Path.Combine(TestPaths.Root, "contracts", "vendored", "vocabulary.yaml")));

    private static string Body(string action = "crm.case_note@1", string party = "\"party://p1\"", string parameters = "{\"case_ref\":\"payload://c1\",\"note_ref\":\"payload://n1\"}", string extra = "") =>
        "{\"run_id\":\"r1\",\"action_type\":\"" + action + "\",\"party_ref\":" + party + ",\"parameters\":" + parameters
        + ",\"justification\":\"j\",\"trace_ref\":\"trace://t1\"" + extra + "}";

    private static SubmissionOutcome Check(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return Validator.Validate(doc.RootElement);
    }

    private static void AssertViolation(SubmissionOutcome o, string pointer, string keyword)
    {
        var v = Assert.IsType<SubmissionOutcome.Violation>(o);
        Assert.Equal(new SchemaViolation(pointer, keyword), v.Detail);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_10_envelope_contract_loads_eagerly()
    {
        SubmissionValidator.EnsureContractLoaded();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_10_missing_envelope_contract_is_static_invalid_operation()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SubmissionValidator.LoadContract(typeof(SubmissionValidator).Assembly, "contracts/missing-secretvalue.json"));
        Assert.Equal("Contract schema is missing or invalid.", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_valid_body_returns_ok_with_entry()
    {
        var ok = Assert.IsType<SubmissionOutcome.Ok>(Check(Body()));
        Assert.Equal("crm.case_note@1", ok.Entry.ActionId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_missing_trace_ref_is_required_violation_at_root() =>
        AssertViolation(Check("{\"run_id\":\"r\",\"action_type\":\"crm.case_note@1\",\"party_ref\":\"p\",\"parameters\":{},\"justification\":\"j\"}"), "", "required");

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_null_optional_is_a_type_violation() =>
        AssertViolation(Check(Body(extra: ",\"nono_session_id\":null")), "/nono_session_id", "type");

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_array_parameters_is_a_type_violation() =>
        AssertViolation(Check(Body(parameters: "[]")), "/parameters", "type");

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_extra_top_level_field_reports_additionalProperties() =>
        AssertViolation(Check(Body(extra: ",\"zzz\":1")), "/zzz", "additionalProperties");

    [Fact]
    [Trait("Category", "Unit")]
    public void API_04_padded_value_is_accepted_untouched_and_empty_run_id_fails()
    {
        Assert.IsType<SubmissionOutcome.Ok>(Check(Body(party: "\" x \"")));
        AssertViolation(Check(Body().Replace("\"run_id\":\"r1\"", "\"run_id\":\"\"")), "/run_id", "minLength");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_unknown_action_is_not_in_catalog_not_a_violation() =>
        Assert.IsType<SubmissionOutcome.NotInCatalog>(Check(Body(action: "nope@1")));

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_parameter_schema_failures_carry_prefixed_pointer()
    {
        AssertViolation(Check(Body(parameters: "{\"case_ref\":\"c\",\"note_ref\":\"n\",\"extra\":1}")), "/parameters/extra", "additionalProperties");
        AssertViolation(Check(Body(parameters: "{\"case_ref\":5,\"note_ref\":\"n\"}")), "/parameters/case_ref", "type");
        AssertViolation(Check(Body(parameters: "{\"case_ref\":\"c\"}")), "/parameters", "required");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_envelope_failure_wins_over_catalog_miss_and_miss_wins_over_parameters()
    {
        AssertViolation(Check(Body(action: "nope@1", extra: ",\"zzz\":1")), "/zzz", "additionalProperties");
        Assert.IsType<SubmissionOutcome.NotInCatalog>(Check(Body(action: "nope@1", parameters: "{\"bad\":1}")));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void API_05_violation_never_contains_the_submitted_value()
    {
        var o = Check(Body(parameters: "{\"case_ref\":\"super-secret-value\",\"note_ref\":5}"));
        var v = Assert.IsType<SubmissionOutcome.Violation>(o);
        Assert.DoesNotContain("super-secret-value", v.Detail.Pointer + v.Detail.Keyword);
        var o2 = Check(Body(party: "5").Replace("\"j\"", "\"super-secret-value\""));
        var v2 = Assert.IsType<SubmissionOutcome.Violation>(o2);
        Assert.DoesNotContain("super-secret-value", v2.Detail.Pointer + v2.Detail.Keyword);
    }
}
