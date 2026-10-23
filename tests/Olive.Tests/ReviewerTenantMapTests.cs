using Olive.Catalog;

namespace Olive.Tests;

public sealed class ReviewerTenantMapTests
{
    private static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    private static readonly Guid Beta = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");

    [Theory]
    [InlineData("")]
    [InlineData("tenants: []")]
    [InlineData("bogus: 1")]
    [InlineData("tenants:\n  - group: g\n    tenant_id: 5325ffe9-5727-4d88-9832-aebabb8d0538\n    extra: 1\n")]
    [InlineData("tenants:\n  - group: g\n    tenant_id: not-a-guid\n")]
    [InlineData("tenants:\n  - group: g\n    tenant_id: 5325ffe9-5727-4d88-9832-aebabb8d0538\n  - group: g\n    tenant_id: f92c9dba-50f1-4cad-9690-5d79f8ded335\n")]
    [Trait("Category", "Unit")]
    public void ReviewerTenant_invalid_documents_are_refused_with_static_message(string yaml)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ReviewerTenantMap.Parse(yaml));
        Assert.Equal("Reviewer tenant map is invalid.", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReviewerTenant_TenantsFor_returns_distinct_mapped_tenants()
    {
        var m = ReviewerTenantMap.LoadEmbedded();
        Assert.Equal([Alpha], m.TenantsFor(["tenant-alpha-reviewers", "tenant-gamma-reviewers", "role:case_supervisor"]));
        Assert.Empty(m.TenantsFor(["role:case_supervisor", "nope"]));
        Assert.Empty(m.TenantsFor([]));
        Assert.Equal(2, m.TenantsFor(["tenant-alpha-reviewers", "tenant-alpha-duplicate"]).Count);
        Assert.Contains(Beta, m.TenantsFor(["tenant-beta-reviewers"]));
    }
}
