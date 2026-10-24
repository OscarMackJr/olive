using System.Security.Claims;
using Olive.Catalog;
using Olive.Integrations.Entra;

namespace Olive.Tests;

public sealed class EntraClaimsMapperTests
{
    private const string Oid = "11111111-2222-3333-4444-555555555555";
    private const string Tid = "99999999-8888-7777-6666-555555555555";
    private static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");

    private static readonly ReviewerTenantMap Tenants = ReviewerTenantMap.LoadEmbedded();
    private static readonly IReadOnlyDictionary<string, string> Allow = RoleAllowlist.FromRegistry(QueueKindRegistry.LoadEmbedded());

    private static List<Claim> Base(params (string Type, string Value)[] extra)
    {
        var claims = new List<Claim> { new("oid", Oid), new("tid", Tid), new("preferred_username", "r@example.test") };
        claims.AddRange(extra.Select(e => new Claim(e.Type, e.Value)));
        return claims;
    }

    private static EntraMapResult Map(List<Claim> claims, string? expectedTid = null) => EntraClaimsMapper.Map(claims, Tenants, Allow, expectedTid);

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_allowlist_is_derived_from_the_registry()
    {
        Assert.Equal("role:case_supervisor", Allow["case_supervisor"]);
        Assert.Equal("role:identity_steward", Allow["identity_steward"]);
        Assert.Equal(2, Allow.Count);
    }

    [Theory]
    [InlineData("oid")]
    [InlineData("tid")]
    [Trait("Category", "Unit")]
    public void UI_01_oid_and_tid_are_required(string missing)
    {
        var claims = Base().Where(c => c.Type != missing).ToList();
        var e = Assert.Throws<InvalidOperationException>(() => Map(claims));
        Assert.Equal("The token is missing a required identity claim.", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_malformed_tid_message_carries_no_claim_value()
    {
        var sentinel = "sentinel-" + Guid.NewGuid().ToString("N");
        var claims = Base().Where(c => c.Type != "tid").Append(new Claim("tid", sentinel)).ToList();
        var e = Assert.Throws<InvalidOperationException>(() => Map(claims));
        Assert.DoesNotContain(sentinel, e.Message);
        Assert.DoesNotContain(sentinel, e.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_wrong_tid_is_refused_and_matching_tid_accepted()
    {
        Assert.Throws<InvalidOperationException>(() => Map(Base(("groups", "tenant-alpha-reviewers")), "00000000-0000-0000-0000-000000000001"));
        Assert.Equal(Alpha, Map(Base(("groups", "tenant-alpha-reviewers")), Tid).TenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_allowlisted_role_becomes_a_role_group()
    {
        var r = Map(Base(("groups", "tenant-alpha-reviewers"), ("roles", "case_supervisor")));
        Assert.Contains("role:case_supervisor", r.Identity.Groups);
        Assert.Equal(Oid, r.Identity.Oid);
        Assert.Equal(Oid, r.Identity.ReviewerId);
        Assert.Equal(Tid, r.Identity.Tid);
        Assert.Equal("r@example.test", r.Identity.Upn);
        Assert.Null(r.NoTenantReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_role_prefixed_group_values_are_dropped()
    {
        var r = Map(Base(("groups", "tenant-alpha-reviewers"), ("groups", "role:case_supervisor")));
        Assert.DoesNotContain("role:case_supervisor", r.Identity.Groups);
        var arrayForm = Map(Base(("groups", "[\"tenant-alpha-reviewers\",\"role:case_supervisor\"]")));
        Assert.DoesNotContain("role:case_supervisor", arrayForm.Identity.Groups);
        Assert.Equal(Alpha, arrayForm.TenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_unlisted_role_is_dropped()
    {
        var r = Map(Base(("groups", "tenant-alpha-reviewers"), ("roles", "global_admin"), ("roles", "role:case_supervisor")));
        Assert.DoesNotContain(r.Identity.Groups, g => g.StartsWith("role:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_overage_by_hasgroups_gives_no_tenant()
    {
        var r = Map(Base(("hasgroups", "true"), ("groups", "tenant-alpha-reviewers")));
        Assert.Null(r.TenantId);
        Assert.Equal("groups_overage", r.NoTenantReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_overage_by_claim_names_gives_no_tenant()
    {
        var r = Map(Base(("_claim_names", "{\"groups\":\"src1\"}")));
        Assert.Null(r.TenantId);
        Assert.Equal("groups_overage", r.NoTenantReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_malformed_claim_names_is_not_overage_and_never_throws()
    {
        var r = Map(Base(("_claim_names", "{not json"), ("groups", "tenant-alpha-reviewers")));
        Assert.Equal(Alpha, r.TenantId);
        var other = Map(Base(("_claim_names", "{\"other\":\"x\"}"), ("groups", "tenant-alpha-reviewers")));
        Assert.Equal(Alpha, other.TenantId);
    }

    [Theory]
    [InlineData("unmapped-group")]
    [InlineData("tenant-alpha-reviewers,tenant-beta-reviewers")]
    [Trait("Category", "Unit")]
    public void UI_01_zero_or_several_tenants_give_no_reviewer_tenant(string groups)
    {
        var r = Map(Base(groups.Split(',').Select(g => ("groups", g)).ToArray()));
        Assert.Null(r.TenantId);
        Assert.Equal("no_reviewer_tenant", r.NoTenantReason);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_two_groups_of_one_tenant_resolve_to_that_tenant()
    {
        var r = Map(Base(("groups", "tenant-alpha-reviewers"), ("groups", "tenant-gamma-reviewers")));
        Assert.Equal(Alpha, r.TenantId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_auth_time_is_read_or_zero()
    {
        Assert.Equal(1700000000, Map(Base(("auth_time", "1700000000"))).AuthTime);
        Assert.Equal(0, Map(Base()).AuthTime);
        Assert.Equal(0, Map(Base(("auth_time", "junk"))).AuthTime);
    }
}
