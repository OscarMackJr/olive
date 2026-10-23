using Olive.Domain;

namespace Olive.Tests;

public sealed class PolicyEvaluatorTests
{
    private static readonly string[] Required = ["servicing", "collections"];

    [Fact]
    public void POL_01_purpose_in_required_list_passes()
    {
        var v = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "collections");
        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
    }

    [Theory]
    [InlineData("marketing")]
    [InlineData("Servicing")]
    [InlineData("servicing ")]
    public void POL_01_purpose_not_listed_gives_purpose_not_permitted_ordinal_case_sensitive(string purpose)
    {
        var v = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, purpose);
        Assert.False(v.Allowed);
        Assert.Equal(PolicyRefusalReasons.PurposeNotPermitted, v.Reason);
    }

    [Fact]
    public void POL_01_null_or_empty_required_purpose_is_refused()
    {
        Assert.False(PolicyEvaluator.EvaluateCatalogAndPurpose(true, null, "servicing").Allowed);
        Assert.False(PolicyEvaluator.EvaluateCatalogAndPurpose(true, [], "servicing").Allowed);
    }

    [Fact]
    public void POL_04_detail_for_refusal_is_exact()
    {
        var v = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "marketing");
        Assert.Equal("{\"outcome\":\"refused\",\"reason\":\"purpose_not_permitted\",\"checks\":[{\"name\":\"catalog\",\"result\":\"pass\"},{\"name\":\"purpose\",\"result\":\"fail\"}]}", v.ToDetailJson());
    }

    [Fact]
    public void POL_04_detail_for_unknown_action_omits_later_checks()
    {
        var v = PolicyEvaluator.EvaluateCatalogAndPurpose(false, null, "servicing");
        Assert.Equal(PolicyRefusalReasons.NotInCatalog, v.Reason);
        Assert.Equal("{\"outcome\":\"refused\",\"reason\":\"not_in_catalog\",\"checks\":[{\"name\":\"catalog\",\"result\":\"fail\"}]}", v.ToDetailJson());
    }

    [Fact]
    public void POL_04_detail_for_allowed_lists_both_checks_with_null_reason()
    {
        var v = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "servicing");
        Assert.Equal("{\"outcome\":\"allowed\",\"reason\":null,\"checks\":[{\"name\":\"catalog\",\"result\":\"pass\"},{\"name\":\"purpose\",\"result\":\"pass\"}]}", v.ToDetailJson());
    }

    [Fact]
    public void POL_04_detail_never_contains_caller_supplied_strings()
    {
        var json = PolicyEvaluator.EvaluateCatalogAndPurpose(true, ["SENTINEL-REQ"], "SENTINEL-PURPOSE").ToDetailJson();
        Assert.DoesNotContain("SENTINEL", json);
    }

    [Fact]
    public void POL_04_verdict_can_be_extended_and_failure_short_circuits()
    {
        var ok = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "servicing").Then(PolicyEvaluator.Entitlement, false, PolicyRefusalReasons.EntitlementMissing);
        Assert.Equal(PolicyRefusalReasons.EntitlementMissing, ok.Reason);
        var after = ok.Then(PolicyEvaluator.Rate, false, PolicyRefusalReasons.RateLimited);
        Assert.Equal(PolicyRefusalReasons.EntitlementMissing, after.Reason);
        Assert.Equal(3, after.Checks.Count);
    }
}
