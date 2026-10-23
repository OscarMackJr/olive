using Olive.Application;
using Olive.Integrations.Entra;

namespace Olive.Tests;

public sealed class UserIdentityResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string Oid = "aaaaaaaa-0000-4000-8000-000000000004";
    private const string Tid = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    private sealed class Fake(Func<CancellationToken, Task<UserIdentityLookup>> f) : IUserIdentityResolver
    {
        public int Calls;
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) { Calls++; return f(ct); }
    }

    private static (ApprovalIdentityCheck Check, Fake Fake) Make(Func<CancellationToken, Task<UserIdentityLookup>> f, TimeSpan? timeout = null)
    {
        var fake = new Fake(f);
        return (new ApprovalIdentityCheck(new UserIdentityVerifier(fake, new UserIdentityVerifierOptions(timeout ?? TimeSpan.FromSeconds(5)))), fake);
    }

    private static Task<UserIdentityLookup> Ok(UserIdentityLookup l) => Task.FromResult(l);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_same_oid_and_tid_is_self_approval()
    {
        var (check, _) = Make(_ => Ok(new UserIdentityLookup.Resolved(Oid, Tid)));
        Assert.IsType<ApprovalIdentityVerdict.SelfApproval>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_oid_comparison_ignores_case()
    {
        var (check, _) = Make(_ => Ok(new UserIdentityLookup.Resolved(Oid.ToUpperInvariant(), Tid.ToUpperInvariant())));
        Assert.IsType<ApprovalIdentityVerdict.SelfApproval>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_different_oid_or_different_tid_is_allowed()
    {
        var (check, _) = Make(_ => Ok(new UserIdentityLookup.Resolved(Oid, Tid)));
        Assert.IsType<ApprovalIdentityVerdict.Allowed>(await check.CheckAsync(Tenant, "u@example.test", "bbbbbbbb-0000-4000-8000-000000000009", Tid, default));
        Assert.IsType<ApprovalIdentityVerdict.Allowed>(await check.CheckAsync(Tenant, "u@example.test", Oid, "ffffffff-ffff-4fff-8fff-ffffffffffff", default));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("notfound")]
    [InlineData("ambiguous")]
    [InlineData("malformed")]
    public async Task APR_02_unresolvable_lookups_are_unresolved(string kind)
    {
        UserIdentityLookup l = kind switch { "notfound" => new UserIdentityLookup.NotFound(), "ambiguous" => new UserIdentityLookup.Ambiguous(), _ => new UserIdentityLookup.Malformed() };
        var (check, _) = Make(_ => Ok(l));
        Assert.IsType<ApprovalIdentityVerdict.Unresolved>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_unavailable_lookup_is_unavailable()
    {
        var (check, _) = Make(_ => Ok(new UserIdentityLookup.Unavailable()));
        Assert.IsType<ApprovalIdentityVerdict.Unavailable>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_throwing_resolver_is_unavailable_without_the_exception_message()
    {
        var (check, _) = Make(_ => throw new HttpRequestException("identity-fault-secret-detail"));
        var v = Assert.IsType<ApprovalIdentityVerdict.Unavailable>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
        Assert.Equal("identity_resolver_exception", v.Cause);
        Assert.Equal(nameof(HttpRequestException), v.ExceptionType);
        Assert.DoesNotContain("identity-fault-secret-detail", v.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_hanging_resolver_times_out_to_unavailable()
    {
        var (check, _) = Make(async ct => { await Task.Delay(Timeout.Infinite, ct); return new UserIdentityLookup.Unavailable(); }, TimeSpan.FromMilliseconds(50));
        var v = Assert.IsType<ApprovalIdentityVerdict.Unavailable>(await check.CheckAsync(Tenant, "u@example.test", Oid, Tid, default));
        Assert.Equal("identity_resolver_timeout", v.Cause);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task APR_02_blank_on_behalf_of_is_unresolved_without_calling_the_port(string blank)
    {
        var (check, fake) = Make(_ => Ok(new UserIdentityLookup.Resolved(Oid, Tid)));
        Assert.IsType<ApprovalIdentityVerdict.Unresolved>(await check.CheckAsync(Tenant, blank, Oid, Tid, default));
        Assert.Equal(0, fake.Calls);
    }

    private static readonly Guid Alpha = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_default_fixture_resolves_the_analyst_to_one_oid_and_tid()
    {
        var r = FixtureUserIdentityResolver.CreateDefault();
        var found = Assert.IsType<UserIdentityLookup.Resolved>(await r.ResolveAsync(Alpha, "analyst.alpha@example.test", default));
        Assert.Equal(Oid, found.Oid);
        Assert.Equal(Tid, found.Tid);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_fixture_reports_ambiguous_notfound_and_cross_tenant_as_notfound()
    {
        var r = FixtureUserIdentityResolver.CreateDefault();
        Assert.IsType<UserIdentityLookup.Ambiguous>(await r.ResolveAsync(Alpha, "ambiguous.alpha@example.test", default));
        Assert.IsType<UserIdentityLookup.NotFound>(await r.ResolveAsync(Alpha, "nobody@example.test", default));
        Assert.IsType<UserIdentityLookup.NotFound>(await r.ResolveAsync(Guid.NewGuid(), "analyst.alpha@example.test", default));
        Assert.IsType<UserIdentityLookup.Malformed>(await r.ResolveAsync(Alpha, " ", default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task APR_02_end_to_end_fixture_lookalike_upn_with_different_oid_is_allowed_and_same_oid_is_refused()
    {
        var r = FixtureUserIdentityResolver.CreateDefault();
        var check = new ApprovalIdentityCheck(new UserIdentityVerifier(r, new UserIdentityVerifierOptions()));
        Assert.IsType<ApprovalIdentityVerdict.SelfApproval>(await check.CheckAsync(Alpha, "analyst.alpha@example.test", Oid, Tid, default));
        Assert.IsType<ApprovalIdentityVerdict.Allowed>(await check.CheckAsync(Alpha, "analyst.alpha@example.test", "cccccccc-0000-4000-8000-000000000001", Tid, default));
        Assert.IsType<ApprovalIdentityVerdict.Unresolved>(await check.CheckAsync(Alpha, "ambiguous.alpha@example.test", Oid, Tid, default));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void APR_02_fixture_is_non_production_only_and_the_port_is_an_outbound_port()
    {
        Assert.True(typeof(INonProductionOnly).IsAssignableFrom(typeof(FixtureUserIdentityResolver)));
        Assert.NotNull(typeof(IUserIdentityResolver).GetCustomAttributes(typeof(OutboundPortAttribute), false).SingleOrDefault());
    }
}
