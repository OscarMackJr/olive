using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Olive.Application;
using Olive.Domain;

namespace Olive.Tests;

public sealed class SecretTypeTests
{
    private static string Sentinel() => Guid.NewGuid().ToString("N");

    public static IEnumerable<object[]> Factories() =>
    [
        [(Func<string, object>)(s => new SessionKey(s))],
        [(Func<string, object>)(s => new ServiceToken(s))],
        [(Func<string, object>)(s => new OidcClientSecret(s))],
        [(Func<string, object>)(s => new InboundBearerToken(s))],
    ];

    [Theory]
    [MemberData(nameof(Factories))]
    [Trait("Category", "Unit")]
    public void FND_06_secret_never_renders_in_any_format(Func<string, object> make)
    {
        var s = Sentinel();
        var k = make(s);
        var renders = new[]
        {
            k.ToString()!,
            $"{k}",
            string.Format("{0}", k),
            string.Format("{0:G}", k),
            string.Format("{0:x}", k),
            JsonSerializer.Serialize(k, k.GetType()),
            JsonSerializer.Serialize(new { Key = k }),
        };
        foreach (var r in renders)
        {
            Assert.DoesNotContain(s, r);
            Assert.Contains("[REDACTED]", r);
        }

        Assert.Equal("[REDACTED]", k.ToString());
        Assert.Equal("[REDACTED]", $"{k}");
    }

    [Theory]
    [MemberData(nameof(Factories))]
    [Trait("Category", "Unit")]
    public void FND_06_debugger_display_is_redacted(Func<string, object> make)
    {
        var attr = make(Sentinel()).GetType().GetCustomAttribute<DebuggerDisplayAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("[REDACTED]", attr!.Value);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    [Trait("Category", "Unit")]
    public void FND_06_secret_types_are_sealed_non_records(Func<string, object> make)
    {
        var t = make("x").GetType();
        Assert.True(t.IsSealed);
        Assert.Null(t.GetMethod("<Clone>$"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_06_deserializing_a_session_key_throws()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SessionKey>("\"abc\""));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<OidcClientSecret>("\"abc\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [Trait("Category", "Unit")]
    public void FND_06_empty_secret_is_rejected_with_static_message(string? value)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SessionKey(value!));
        Assert.Equal("A session key must not be empty.", ex.Message);
        var ex2 = Assert.Throws<ArgumentException>(() => new ServiceToken(value!));
        Assert.Equal("A service token must not be empty.", ex2.Message);
        var ex3 = Assert.Throws<ArgumentException>(() => new OidcClientSecret(value!));
        Assert.Equal("An OIDC client secret must not be empty.", ex3.Message);
        var ex4 = Assert.Throws<ArgumentException>(() => new InboundBearerToken(value!));
        Assert.Equal("An inbound bearer token must not be empty.", ex4.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_06_expose_secret_returns_original()
    {
        var s = Sentinel();
        Assert.Equal(s, new SessionKey(s).ExposeSecret());
        Assert.Equal(s, new ServiceToken(s).ExposeSecret());
        Assert.Equal(s, new OidcClientSecret(s).ExposeSecret());
        Assert.Equal(s, new InboundBearerToken(s).ExposeSecret());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_inbound_bearer_token_try_read_returns_the_token_for_a_well_formed_header()
    {
        Assert.Equal("x", InboundBearerToken.TryRead("Bearer x")!.ExposeSecret());
        Assert.Equal("x", InboundBearerToken.TryRead("bearer x")!.ExposeSecret());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic x")]
    [InlineData("Bearer a b")]
    [Trait("Category", "Unit")]
    public void IRQ_04_inbound_bearer_token_try_read_rejects_malformed_headers(string? header) =>
        Assert.Null(InboundBearerToken.TryRead(header));

    [Fact]
    [Trait("Category", "Unit")]
    public void IRQ_04_inbound_bearer_token_try_read_rejects_an_oversize_header() =>
        Assert.Null(InboundBearerToken.TryRead("Bearer " + new string('a', 4096)));

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_06_service_token_grant_never_renders_token()
    {
        var s = Sentinel();
        var g = new ServiceTokenGrant(new ServiceToken(s), Guid.NewGuid(), "agent", "key", DateTimeOffset.UnixEpoch);
        var issued = new TokenIssueResult.Issued(g);
        var renders = new[]
        {
            g.ToString(),
            $"{g}",
            string.Format("{0}", g),
            string.Format("{0:G}", g),
            JsonSerializer.Serialize(new { Grant = g }),
            issued.ToString(),
        };
        foreach (var r in renders)
        {
            Assert.DoesNotContain(s, r);
        }

        Assert.Equal("[REDACTED ServiceTokenGrant]", g.ToString());
        Assert.Equal("[REDACTED ServiceTokenGrant]", $"{g}");
        Assert.True(typeof(ServiceTokenGrant).IsSealed);
        Assert.Equal("[REDACTED]", typeof(ServiceTokenGrant).GetCustomAttribute<DebuggerDisplayAttribute>()!.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_06_state_and_principal_constants_match_vocabulary()
    {
        Assert.Equal(
            ["proposed", "refused_policy", "awaiting_approval", "approved", "rejected", "executed", "execution_failed", "expired"],
            ProposalStates.All);
        Assert.Equal(["user", "application", "agent"], PrincipalTypes.All);
    }
}
