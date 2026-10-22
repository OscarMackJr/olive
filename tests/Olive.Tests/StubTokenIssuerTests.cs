using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

public sealed class StubTokenIssuerTests
{
    private static readonly ManualTimeProvider Now = new(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));

    private static Task<TokenIssueResult> Issue(string key) =>
        StubTokenIssuer.CreateDefault(Now).IssueAsync(new SessionKey(key), CancellationToken.None);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_liam_keys_get_a_grant_with_tenant_agent_key_id_and_bounded_expiry()
    {
        foreach (var (key, tenant, keyId) in new[]
        {
            ("fixture-key-liam-alpha", TestPaths.TenantAlpha, "0a1b2c3d-0000-4000-8000-000000000001"),
            ("fixture-key-liam-beta", TestPaths.TenantBeta, "0a1b2c3d-0000-4000-8000-000000000002"),
        })
        {
            var grant = Assert.IsType<TokenIssueResult.Issued>(await Issue(key)).Grant;
            Assert.Equal(tenant, grant.TenantId);
            Assert.Equal("liam", grant.DelegatedAgentId);
            Assert.Equal(keyId, grant.KeyId);
            Assert.Equal(Now.GetUtcNow().AddMinutes(15), grant.ExpiresAt);
            Assert.True(grant.ExpiresAt <= DateTimeOffset.Parse("2099-01-01T00:00:00Z"));
            Assert.Equal("[REDACTED]", grant.Token.ToString());
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_expiry_never_exceeds_key_expiry()
    {
        var json = """
            {"active":true,"key_id":"0a1b2c3d-0000-4000-8000-000000000001","principal_type":"agent","agent_id":"liam",
             "on_behalf_of":"a@example.test","purpose":"servicing","tenant_id":"5325ffe9-5727-4d88-9832-aebabb8d0538",
             "data_class_ceiling":"confidential-personal","expires_at":"2026-10-21T00:05:00Z"}
            """;
        var issuer = new StubTokenIssuer(new StubKeyIntrospector(new Dictionary<string, string> { ["k"] = json }, Now), Now);
        var grant = Assert.IsType<TokenIssueResult.Issued>(await issuer.IssueAsync(new SessionKey("k"), CancellationToken.None)).Grant;
        Assert.Equal(Now.GetUtcNow().AddMinutes(5), grant.ExpiresAt);
    }

    [Theory]
    [InlineData("fixture-key-inactive")]
    [InlineData("unknown-key")]
    [InlineData("fixture-key-user")]
    [InlineData("fixture-key-application")]
    [Trait("Category", "Unit")]
    public async Task IDN_05_inactive_or_non_registered_keys_are_refused(string key)
    {
        Assert.IsType<TokenIssueResult.Refused>(await Issue(key));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_non_registered_tenant_is_refused()
    {
        var json = """
            {"active":true,"key_id":"0a1b2c3d-0000-4000-8000-000000000001","principal_type":"agent","agent_id":"liam",
             "on_behalf_of":"a@example.test","purpose":"servicing","tenant_id":"11111111-1111-4111-8111-111111111111",
             "data_class_ceiling":"confidential-personal","expires_at":"2099-01-01T00:00:00Z"}
            """;
        var issuer = new StubTokenIssuer(new StubKeyIntrospector(new Dictionary<string, string> { ["k"] = json }, Now), Now);
        Assert.IsType<TokenIssueResult.Refused>(await issuer.IssueAsync(new SessionKey("k"), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_05_two_issuances_produce_different_token_values()
    {
        var a = Assert.IsType<TokenIssueResult.Issued>(await Issue("fixture-key-liam-alpha")).Grant.Token.ExposeSecret();
        var b = Assert.IsType<TokenIssueResult.Issued>(await Issue("fixture-key-liam-alpha")).Grant.Token.ExposeSecret();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
    }
}
