using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Olive.Host.Auth;
using Olive.Host.Startup;

namespace Olive.Tests;

public sealed class OidcConfigGuardTests
{
    private const string SecretSentinel = "secret-sentinel-4f1c9a";
    private const string AuthoritySentinel = "authority-sentinel-7d2b";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static (string, string?)[] Valid() =>
    [
        ("OLIVE_OIDC_AUTHORITY", "https://" + AuthoritySentinel + ".example/tenant"),
        ("OLIVE_OIDC_CLIENT_ID", "olive-client"),
        ("OLIVE_OIDC_CLIENT_SECRET", SecretSentinel),
    ];

    private static readonly OliveEnvironment Production = new("production");

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_complete_https_configuration_passes_in_production()
    {
        StartupGuards.CheckOidcConfiguration(Config(Valid()), Production);
    }

    [Theory]
    [InlineData("OLIVE_OIDC_AUTHORITY")]
    [InlineData("OLIVE_OIDC_CLIENT_ID")]
    [InlineData("OLIVE_OIDC_CLIENT_SECRET")]
    [Trait("Category", "Unit")]
    public void UI_01_missing_or_blank_setting_refuses_production(string missing)
    {
        foreach (var value in new string?[] { null, "", "  " })
        {
            var values = Valid().Select(v => v.Item1 == missing ? (v.Item1, value) : v).ToArray();
            var guard = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckOidcConfiguration(Config(values), Production));
            Assert.Equal("UI-01", guard.Rule);
            Assert.Contains(missing, guard.Message);
            Assert.DoesNotContain(SecretSentinel, guard.Message);
            Assert.DoesNotContain(AuthoritySentinel, guard.Message);
        }
    }

    [Theory]
    [InlineData("http://" + AuthoritySentinel + ".example/tenant")]
    [InlineData("not a url " + AuthoritySentinel)]
    [InlineData("//" + AuthoritySentinel + ".example/tenant")]
    [Trait("Category", "Unit")]
    public void UI_01_non_https_authority_refuses_production_without_echoing_it(string authority)
    {
        var values = Valid().Select(v => v.Item1 == "OLIVE_OIDC_AUTHORITY" ? (v.Item1, (string?)authority) : v).ToArray();
        var guard = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckOidcConfiguration(Config(values), Production));
        Assert.Equal("UI-01", guard.Rule);
        Assert.DoesNotContain(AuthoritySentinel, guard.Message);
        Assert.DoesNotContain(SecretSentinel, guard.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_registered_dev_sign_in_gate_refuses_production()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DevSignInGate>();
        var guard = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckOidcConfiguration(Config(Valid()), Production, services));
        Assert.Equal("UI-01", guard.Rule);
    }

    [Theory]
    [InlineData("ci")]
    [InlineData("local")]
    [InlineData("nonprod")]
    [Trait("Category", "Unit")]
    public void UI_01_non_production_needs_no_oidc_configuration(string env)
    {
        StartupGuards.CheckOidcConfiguration(Config(), new OliveEnvironment(env));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_read_returns_null_when_incomplete_and_wraps_the_secret()
    {
        Assert.Null(OidcConfig.Read(Config()));
        Assert.Null(OidcConfig.Read(Config(("OLIVE_OIDC_AUTHORITY", "https://x.example"), ("OLIVE_OIDC_CLIENT_ID", "id"))));
        var cfg = OidcConfig.Read(Config(Valid()));
        Assert.NotNull(cfg);
        Assert.DoesNotContain(SecretSentinel, cfg!.ToString());
        Assert.True(cfg.RequireHttpsMetadata);
    }

    [Theory]
    [InlineData("ci", "http://localhost:8080/entra", false)]
    [InlineData("local", "http://localhost:8080/entra", false)]
    [InlineData("nonprod", "http://localhost:8080/entra", true)]
    [InlineData("ci", "https://idp.example/entra", true)]
    [Trait("Category", "Unit")]
    public void UI_01_plain_http_metadata_is_allowed_only_for_http_authority_in_ci_or_local(string env, string authority, bool requireHttps)
    {
        var cfg = OidcConfig.Read(Config(("OLIVE_ENVIRONMENT", env), ("OLIVE_OIDC_AUTHORITY", authority), ("OLIVE_OIDC_CLIENT_ID", "id"), ("OLIVE_OIDC_CLIENT_SECRET", "s")));
        Assert.Equal(requireHttps, cfg!.RequireHttpsMetadata);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UI_01_freshness_must_be_a_positive_integer()
    {
        var cfg = OidcConfig.Read(Config(Valid().Append(("OLIVE_OIDC_FRESHNESS_MINUTES", "15")).ToArray()));
        Assert.Equal(TimeSpan.FromMinutes(15), cfg!.Freshness);
        var e = Assert.Throws<InvalidOperationException>(() => OidcConfig.Read(Config(Valid().Append(("OLIVE_OIDC_FRESHNESS_MINUTES", "-3")).ToArray())));
        Assert.DoesNotContain("-3", e.Message);
    }
}

[Trait("Category", "Integration")]
public sealed class OidcConfigGuardHostTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UI_01_production_host_without_oidc_authority_refuses_with_ui_01_after_other_guards_pass()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            extraConfig: new Dictionary<string, string?> { ["OLIVE_OIDC_AUTHORITY"] = "" },
            configureServices: s =>
            {
                StartupGuardTests.ProductionSafePorts(s);
                s.RemoveAll<Olive.Application.IKeyIntrospector>();
                s.AddSingleton<Olive.Application.IKeyIntrospector, ThrowingKeyIntrospector>();
            });
        var guard = await StartupGuardTests.TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Equal("UI-01", guard.Rule);
    }

    [Fact]
    public async Task UI_01_ci_host_with_no_oidc_configuration_starts_normally()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "ci");
        Assert.Null(await StartupGuardTests.TryStartAsync(factory));
    }
}
