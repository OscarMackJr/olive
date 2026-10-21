using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

public sealed class StubKeyIntrospectorTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedTimeProvider Now = new(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero));

    private static Task<IntrospectionResult> Run(string key) =>
        StubKeyIntrospector.CreateDefault(Now).IntrospectAsync(new SessionKey(key), CancellationToken.None);

    private static Task<IntrospectionResult> RunJson(string json) =>
        new StubKeyIntrospector(new Dictionary<string, string> { ["k"] = json }, Now).IntrospectAsync(new SessionKey("k"), CancellationToken.None);

    private const string Good = """
        {"active":true,"key_id":"0a1b2c3d-0000-4000-8000-000000000001","principal_type":"agent","agent_id":"liam",
         "on_behalf_of":"a@example.test","purpose":"servicing","tenant_id":"5325ffe9-5727-4d88-9832-aebabb8d0538",
         "data_class_ceiling":"confidential-personal","expires_at":"2099-01-01T00:00:00Z"}
        """;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_liam_alpha_is_active_for_alpha()
    {
        var r = await Run("fixture-key-liam-alpha");
        Assert.True(r.Active);
        Assert.Equal("liam", r.Key!.AgentId);
        Assert.Equal(TestPaths.TenantAlpha, r.Key.TenantId);
        Assert.Equal("agent", r.Key.PrincipalType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_liam_beta_is_active_for_beta()
    {
        var r = await Run("fixture-key-liam-beta");
        Assert.Equal(TestPaths.TenantBeta, r.Key!.TenantId);
    }

    [Theory]
    [InlineData("fixture-key-user", "user")]
    [InlineData("fixture-key-application", "application")]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_non_agent_keys_carry_their_principal_type(string key, string type)
    {
        var r = await Run(key);
        Assert.True(r.Active);
        Assert.Equal(type, r.Key!.PrincipalType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_not_allowlisted_agent_is_mallory()
    {
        Assert.Equal("mallory", (await Run("fixture-key-agent-not-allowlisted")).Key!.AgentId);
    }

    [Theory]
    [InlineData("fixture-key-inactive")]
    [InlineData("some-unknown-key")]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_inactive_and_unknown_keys_are_inactive(string key)
    {
        var r = await Run(key);
        Assert.False(r.Active);
        Assert.Null(r.Key);
    }

    [Theory]
    [InlineData("\"agent\"", "\"service\"")]
    [InlineData("0a1b2c3d-0000-4000-8000-000000000001", "0A1B2C3D-0000-4000-8000-000000000001")]
    [InlineData("0a1b2c3d-0000-4000-8000-000000000001", "not-a-uuid")]
    [InlineData("5325ffe9-5727-4d88-9832-aebabb8d0538", "not-a-uuid")]
    [InlineData("\"agent_id\":\"liam\",", "")]
    [InlineData("\"on_behalf_of\":\"a@example.test\",", "")]
    [InlineData("\"purpose\":\"servicing\",", "")]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_malformed_active_response_fails_closed(string from, string to)
    {
        var ex = await Assert.ThrowsAsync<IntrospectionUnavailableException>(() => RunJson(Good.Replace(from, to)));
        Assert.Equal("malformed_introspection_response", ex.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_unparseable_fixture_fails_closed()
    {
        await Assert.ThrowsAsync<IntrospectionUnavailableException>(() => RunJson("{not json"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_expired_key_is_inactive()
    {
        var late = new FixedTimeProvider(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var r = await new StubKeyIntrospector(new Dictionary<string, string> { ["k"] = Good }, late)
            .IntrospectAsync(new SessionKey("k"), CancellationToken.None);
        Assert.False(r.Active);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IDN_01_stub_exception_never_contains_presented_key()
    {
        var secret = Guid.NewGuid().ToString("N");
        var stub = new StubKeyIntrospector(new Dictionary<string, string> { [secret] = "{bad" }, Now);
        var ex = await Assert.ThrowsAsync<IntrospectionUnavailableException>(() => stub.IntrospectAsync(new SessionKey(secret), CancellationToken.None));
        Assert.DoesNotContain(secret, ex.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IDN_01_stub_is_marked_non_production()
    {
        Assert.IsAssignableFrom<INonProductionOnly>(StubKeyIntrospector.CreateDefault(Now));
    }
}
