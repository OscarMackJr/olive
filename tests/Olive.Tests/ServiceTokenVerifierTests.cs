using System.Text.Json;
using Olive.Application;
using Olive.Catalog;
using Olive.Domain;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

public sealed class ServiceTokenVerifierTests
{
    private static readonly ManualTimeProvider Now = new(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));

    private static Task<ServiceTokenResult> Run(string token) =>
        StubServiceTokenVerifier.CreateDefault(Now).VerifyAsync(new InboundBearerToken(token), CancellationToken.None);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_bluto_alpha_token_is_an_active_application_for_alpha()
    {
        var r = await Run("fixture-service-token-bluto-alpha");
        Assert.True(r.Active);
        Assert.Equal("application", r.Caller!.PrincipalType);
        Assert.Equal("bluto-stub", r.Caller.ClientId);
        Assert.Equal(TestPaths.TenantAlpha, r.Caller.TenantId);
        Assert.Null(r.Caller.DelegatedAgentId);
        Assert.True(r.Caller.ExpiresAt > Now.GetUtcNow());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_bluto_beta_token_carries_the_beta_tenant()
    {
        var r = await Run("fixture-service-token-bluto-beta");
        Assert.Equal(TestPaths.TenantBeta, r.Caller!.TenantId);
        Assert.Equal("bluto-stub", r.Caller.ClientId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_unknown_client_token_names_a_client_absent_from_the_producer_registry()
    {
        var r = await Run("fixture-service-token-unknown-client");
        Assert.True(r.Active);
        Assert.Equal("unregistered-client", r.Caller!.ClientId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_unknown_tenant_token_names_a_tenant_absent_from_the_reviewer_tenant_map()
    {
        var r = await Run("fixture-service-token-unknown-tenant");
        Assert.True(r.Active);
        Assert.DoesNotContain(r.Caller!.TenantId, ReviewerTenantMap.LoadEmbedded().AllTenants);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_delegated_agent_token_carries_a_delegated_agent_id()
    {
        var r = await Run("fixture-service-token-delegated-agent");
        Assert.NotNull(r.Caller!.DelegatedAgentId);
    }

    [Theory]
    [InlineData("fixture-service-token-inactive")]
    [InlineData("fixture-service-token-expired")]
    [InlineData("not-a-known-token")]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_inactive_expired_and_unknown_tokens_are_inactive(string token)
    {
        var r = await Run(token);
        Assert.False(r.Active);
        Assert.Null(r.Caller);
    }

    [Theory]
    [InlineData("fixture-service-token-malformed")]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_malformed_fixture_fails_closed(string token) =>
        await Assert.ThrowsAsync<IntrospectionUnavailableException>(() => Run(token));

    [Theory]
    [InlineData("{\"active\":true,\"principal_type\":\"robot\",\"client_id\":\"c\",\"tenant_id\":\"5325ffe9-5727-4d88-9832-aebabb8d0538\",\"expires_at\":\"2099-01-01T00:00:00Z\"}")]
    [InlineData("{\"active\":true,\"principal_type\":\"application\",\"client_id\":\"c\",\"tenant_id\":\"nope\",\"expires_at\":\"2099-01-01T00:00:00Z\"}")]
    [InlineData("{\"active\":\"yes\"}")]
    [InlineData("{\"principal_type\":\"application\"}")]
    [InlineData("[1]")]
    [InlineData("{not json")]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_bad_shapes_throw_unavailable(string json)
    {
        var stub = new StubServiceTokenVerifier(new Dictionary<string, string> { ["t"] = json }, Now);
        await Assert.ThrowsAsync<IntrospectionUnavailableException>(() => stub.VerifyAsync(new InboundBearerToken("t"), CancellationToken.None));
    }

    [Theory]
    [InlineData("fixture-key-liam-alpha", "agent")]
    [InlineData("fixture-key-user", "user")]
    [Trait("Category", "Unit")]
    public async Task IRQ_04_visible_agent_and_user_keys_resolve_as_their_principal_types(string token, string type)
    {
        var r = await Run(token);
        Assert.True(r.Active);
        Assert.Equal(type, r.Caller!.PrincipalType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_05_stub_service_token_verifier_is_non_production_only() =>
        Assert.True(typeof(INonProductionOnly).IsAssignableFrom(typeof(StubServiceTokenVerifier)));

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_service_token_fixtures_validate_against_the_schema_except_the_malformed_one()
    {
        var dir = Path.Combine(TestPaths.Root, "contracts", "service-token");
        var schema = SchemaValidator.Compile(File.ReadAllText(Path.Combine(dir, "service-token-response.schema.json")));
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "index.json")))!;
        Assert.Equal(10, index.Count);
        foreach (var file in index.Values.Distinct())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, file)));
            var violation = schema.Validate(doc.RootElement);
            if (file == "malformed.json") { Assert.NotNull(violation); }
            else { Assert.Null(violation); }
        }
    }
}
