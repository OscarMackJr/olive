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

    private const string Ent = "ent:crm.case_note.write";
    private static PolicyVerdict Prior() => PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "servicing");

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void POL_02_entitlement_requires_both_sides(bool user, bool agent, bool allowed)
    {
        string[] groups = user ? [Ent, "other"] : ["other"];
        var agentSet = agent ? new HashSet<string> { Ent } : new HashSet<string> { "other" };
        var v = PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, agentSet, groups);
        Assert.Equal(allowed, v.Allowed);
        if (!allowed)
        {
            Assert.Equal(PolicyRefusalReasons.EntitlementMissing, v.Reason);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void POL_02_blank_required_entitlement_is_refused(string? required)
    {
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), required, new HashSet<string> { "" , Ent }, ["", Ent]).Allowed);
    }

    [Fact]
    public void POL_02_null_or_empty_groups_and_empty_agent_entitlements_are_refused()
    {
        var agent = new HashSet<string> { Ent };
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, agent, null).Allowed);
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, agent, []).Allowed);
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, new HashSet<string>(), [Ent]).Allowed);
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, null, [Ent]).Allowed);
    }

    [Fact]
    public void POL_02_group_names_compare_ordinal_case_sensitive()
    {
        Assert.False(PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, new HashSet<string> { Ent }, [Ent.ToUpperInvariant()]).Allowed);
    }

    [Fact]
    public void POL_02_detail_checks_pass_and_fail_stops_before_rate()
    {
        var pass = PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, new HashSet<string> { Ent }, [Ent]);
        Assert.Equal("pass", pass.Checks[^1].Result);
        Assert.Equal("entitlement", pass.Checks[^1].Name);
        var fail = PolicyEvaluator.EvaluateEntitlement(Prior(), Ent, new HashSet<string> { Ent }, ["x"]);
        Assert.Equal("fail", fail.Checks[^1].Result);
        Assert.DoesNotContain("rate", fail.ToDetailJson());
        Assert.DoesNotContain(Ent, fail.ToDetailJson());
    }

    [Fact]
    public void POL_02_failed_prior_verdict_is_not_overridden()
    {
        var refused = PolicyEvaluator.EvaluateCatalogAndPurpose(true, Required, "marketing");
        var v = PolicyEvaluator.EvaluateEntitlement(refused, Ent, new HashSet<string> { Ent }, [Ent]);
        Assert.False(v.Allowed);
        Assert.Equal(PolicyRefusalReasons.PurposeNotPermitted, v.Reason);
    }
}
