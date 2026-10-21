using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Olive.Domain;

namespace Olive.Tests;

public sealed class SecretTypeTests
{
    private static string Sentinel() => Guid.NewGuid().ToString("N");

    public static IEnumerable<object[]> Factories() =>
    [
        [(Func<string, object>)(s => new SessionKey(s))],
        [(Func<string, object>)(s => new ServiceToken(s))],
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
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FND_06_expose_secret_returns_original()
    {
        var s = Sentinel();
        Assert.Equal(s, new SessionKey(s).ExposeSecret());
        Assert.Equal(s, new ServiceToken(s).ExposeSecret());
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
