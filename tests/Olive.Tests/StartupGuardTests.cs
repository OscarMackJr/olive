using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Olive.Application;
using Olive.Host.Startup;
using Olive.Integrations.Popeye;

namespace Olive.Tests;

[Trait("Category", "Integration")]
public sealed class StartupGuardTests(PostgresFixture fixture)
{
    internal static StartupGuardException? FindGuard(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is StartupGuardException g) { return g; }
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    var found = FindGuard(inner);
                    if (found is not null) { return found; }
                }
            }
        }

        return null;
    }

    // Starts the host and returns the guard exception it refused with (null when it started).
    internal static async Task<StartupGuardException?> TryStartAsync(OliveApiFactory factory)
    {
        try
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
            return null;
        }
        catch (Exception e)
        {
            return FindGuard(e) ?? throw new InvalidOperationException("Host failed for a non-guard reason.", e);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("staging")]
    [InlineData("Production")]
    public async Task FND_04_invalid_environment_refuses_start_without_echoing_value(string? value)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: value);
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-04", guard.Message);
        if (!string.IsNullOrEmpty(value)) { Assert.DoesNotContain(value, guard.Message); }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("ci")]
    [InlineData("nonprod")]
    public async Task FND_04_valid_environment_starts_and_serves_401(string value)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: value);
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task FND_04_migrator_url_in_configuration_refuses_start_without_echoing_value()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        const string secret = "postgresql://olive_migrator:s3cret@h/db";
        await using var factory = new OliveApiFactory(db, extraConfig: new Dictionary<string, string?> { ["OLIVE_MIGRATOR_URL"] = secret });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("D-19", guard.Message);
        Assert.DoesNotContain("s3cret", guard.Message);
        Assert.DoesNotContain("postgresql", guard.Message);
    }

    [Fact]
    public async Task FND_04_empty_migrator_url_still_refuses_start()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, extraConfig: new Dictionary<string, string?> { ["OLIVE_MIGRATOR_URL"] = "" });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Equal("D-19", guard.Rule);
    }

    [Fact]
    public async Task FND_05_production_with_default_stub_registration_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false);
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_with_stub_instance_registration_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector>(StubKeyIntrospector.CreateDefault(TimeProvider.System)); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_with_stub_factory_registration_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector>(_ => StubKeyIntrospector.CreateDefault(TimeProvider.System)); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_with_stub_type_registration_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); s.AddSingleton<StubKeyIntrospector>(); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    // The type is not visible in the descriptor of the factory registration below, only the resolved instance is.
    [Fact]
    public async Task FND_05_production_hidden_behind_factory_of_olive_service_type_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { s.RemoveAll<IKeyIntrospector>(); s.Add(new ServiceDescriptor(typeof(IKeyIntrospector), _ => new WrappingFactoryStub(), ServiceLifetime.Singleton)); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_stub_behind_decorator_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector>(_ => new DecoratingIntrospector(new DecoratingIntrospector(StubKeyIntrospector.CreateDefault(TimeProvider.System)))); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_keyed_stub_factory_registration_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>();
                s.AddKeyedSingleton<IKeyIntrospector>("fixture", (_, _) => StubKeyIntrospector.CreateDefault(TimeProvider.System));
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_stub_factory_under_non_olive_service_type_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>();
                s.AddSingleton<TimeProvider>(_ => new NonProductionTimeProvider());
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    // The factory wires the recording stubs for the Phase 3 ports; production positive controls swap in non-stub doubles.
    internal static void ProductionSafePorts(IServiceCollection s, bool keepDevReviewerScheme = false)
    {
        s.RemoveAll<ITokenIssuer>();
        s.AddSingleton<ITokenIssuer>(new FakeTokenIssuer());
        s.RemoveAll<ITraceResolver>();
        s.AddSingleton<ITraceResolver>(new FakeTraceResolver());
        s.RemoveAll<IDirectoryGroupResolver>();
        s.AddSingleton<IDirectoryGroupResolver>(new FaultingDirectoryResolver(new ProductionDirectoryStub(), ResolverFault.Unavailable));
        s.RemoveAll<IUserIdentityResolver>();
        s.AddSingleton<IUserIdentityResolver>(new FaultingUserIdentityResolver(new ProductionIdentityStub(), ResolverFault.Unavailable));
        // The picker gate is non-production only (FND-05); a production-safe host does not carry it.
        s.RemoveAll<Olive.Host.Auth.DevSignInGate>();
        s.RemoveAll<IReviewerIdentitySource>();
        s.AddSingleton<IReviewerIdentitySource>(new ProductionReviewerStub());
        if (!keepDevReviewerScheme)
        {
            // Positive controls model a production host that has no dev reviewer scheme (Q17).
            s.PostConfigure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
            {
                o.SchemeMap.Remove(Olive.Host.Auth.DevReviewerAuthenticationHandler.SchemeName);
                ((ICollection<Microsoft.AspNetCore.Authentication.AuthenticationSchemeBuilder>)o.Schemes)
                    .Remove(o.Schemes.First(b => b.Name == Olive.Host.Auth.DevReviewerAuthenticationHandler.SchemeName));
            });
        }
    }

    private sealed class ProductionReviewerStub : IReviewerIdentitySource
    {
        public ReviewerIdentity? Find(string reviewerId) => null;

        public IReadOnlyList<ReviewerIdentity> All() => [];
    }

    [Fact]
    public async Task FND_05_production_with_fixture_reviewer_source_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                ProductionSafePorts(s);
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>();
                s.RemoveAll<IReviewerIdentitySource>();
                s.AddSingleton<IReviewerIdentitySource>(_ => Olive.Integrations.Entra.FixtureReviewerIdentitySource.CreateDefault());
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_refuses_while_the_dev_reviewer_scheme_exists()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                ProductionSafePorts(s, keepDevReviewerScheme: true);
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>();
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("Q17", guard.Message);
    }

    [Fact]
    public async Task FND_05_development_start_with_dev_reviewer_scheme_succeeds()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "ci");
        Assert.Null(await TryStartAsync(factory));
    }

    private sealed class ProductionIdentityStub : IUserIdentityResolver
    {
        public Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => Task.FromResult<UserIdentityLookup>(new UserIdentityLookup.NotFound());
    }

    private sealed class ProductionDirectoryStub : IDirectoryGroupResolver
    {
        public Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => Task.FromResult<DirectoryLookup>(new DirectoryLookup.UnknownUser());
    }

    private sealed class DecoratingDirectory(IDirectoryGroupResolver inner) : IDirectoryGroupResolver, IComponentDecorator
    {
        public object Inner => inner;

        public Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct) => inner.ResolveAsync(tenantId, onBehalfOf, ct);
    }

    [Fact]
    public async Task POL_02_production_refuses_fixture_directory_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); s.RemoveAll<IDirectoryGroupResolver>(); s.AddSingleton<IDirectoryGroupResolver>(_ => Olive.Integrations.Entra.FixtureDirectoryGroupResolver.CreateDefault()); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task POL_02_production_refuses_fixture_directory_resolver_behind_decorator()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); s.RemoveAll<IDirectoryGroupResolver>(); s.AddSingleton<IDirectoryGroupResolver>(_ => new DecoratingDirectory(Olive.Integrations.Entra.FixtureDirectoryGroupResolver.CreateDefault())); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task POL_02_non_production_host_resolves_directory_services()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "ci");
        Assert.Null(await TryStartAsync(factory));
        Assert.NotNull(factory.Services.GetService<IDirectoryGroupResolver>());
        Assert.NotNull(factory.Services.GetService<DirectoryVerifier>());
    }

    [Fact]
    public async Task APR_02_production_with_fixture_user_identity_resolver_refuses()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                ProductionSafePorts(s);
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector>(_ => new DecoratingIntrospector(new ThrowingKeyIntrospector()));
                s.RemoveAll<IUserIdentityResolver>();
                s.AddSingleton<IUserIdentityResolver>(_ => Olive.Integrations.Entra.FixtureUserIdentityResolver.CreateDefault());
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task FND_05_production_decorator_over_production_component_starts()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector>(_ => new DecoratingIntrospector(new ThrowingKeyIntrospector())); });
        Assert.Null(await TryStartAsync(factory));
    }

    [Fact]
    public async Task FND_05_production_positive_control_without_stub_starts()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); });
        Assert.Null(await TryStartAsync(factory));
    }

    [Fact]
    public async Task TRC_09_production_refuses_fixture_trace_resolver()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); s.RemoveAll<ITraceResolver>(); s.AddSingleton<ITraceResolver>(Olive.Integrations.Hometown.FixtureTraceResolver.CreateDefault()); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task TRC_09_production_refuses_fixture_resolver_behind_caching_decorator()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s =>
            {
                ProductionSafePorts(s);
                s.RemoveAll<IKeyIntrospector>();
                s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>();
                s.RemoveAll<ITraceResolver>();
                s.AddSingleton<ITraceResolver>(_ => new CachingTraceResolver(Olive.Integrations.Hometown.FixtureTraceResolver.CreateDefault()));
            });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task IDN_05_production_refuses_stub_token_issuer()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "production", replaceIntrospector: false,
            configureServices: s => { ProductionSafePorts(s); s.RemoveAll<IKeyIntrospector>(); s.AddSingleton<IKeyIntrospector, ThrowingKeyIntrospector>(); s.RemoveAll<ITokenIssuer>(); s.AddSingleton<ITokenIssuer>(_ => StubTokenIssuer.CreateDefault(TimeProvider.System)); });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Contains("FND-05", guard.Message);
    }

    [Fact]
    public async Task TRC_09_non_production_environment_starts_with_fixture_resolver_and_stub_issuer()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "ci", replaceIntrospector: false);
        Assert.Null(await TryStartAsync(factory));
    }

    [Fact]
    public async Task FND_05_ci_with_stub_starts()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: "ci", replaceIntrospector: false);
        Assert.Null(await TryStartAsync(factory));
    }

    [Fact]
    public async Task FND_05_guard_order_environment_before_other_guards()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, environment: null,
            extraConfig: new Dictionary<string, string?> { ["OLIVE_MIGRATOR_URL"] = "x" });
        var guard = await TryStartAsync(factory);
        Assert.Equal("FND-04", guard?.Rule);
    }

    // OLIVE_TRACE_TIMEOUT_MS is read from builder.Configuration before Build(); OliveApiFactory's extraConfig (ConfigureAppConfiguration)
    // only lands after Build(), so the value is passed through UseSetting, which reaches the entry point as host configuration.
    private sealed class TraceTimeoutFactory(TestDatabase db, string value, string setting = "OLIVE_TRACE_TIMEOUT_MS") : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            NoSharedPostgres.Assert(Olive.Persistence.PostgresUrl.ToNpgsql(db.AppUrl));
            builder.UseSetting("OLIVE_ENVIRONMENT", "ci");
            builder.UseSetting("OLIVE_POSTGRES_URL", db.AppUrl);
            builder.UseSetting(setting, value);
            builder.UseSetting("OLIVE_EXECUTOR_OUTPUT_PATH", OutputPath);
        }

        private string OutputPath { get; } = Path.Combine(Path.GetTempPath(), "olive-exec-" + Guid.NewGuid().ToString("N") + ".jsonl");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { try { File.Delete(OutputPath); } catch (IOException) { } }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task TRC_07_invalid_trace_timeout_refuses_start(string value)
    {
        // T-03-19: Program.cs throws a plain InvalidOperationException (not a StartupGuardException), so TryStartAsync does not apply.
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new TraceTimeoutFactory(db, value);
        Exception? failure = null;
        try
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        }
        catch (Exception e)
        {
            failure = e;
        }

        Assert.NotNull(failure);
        Assert.Null(FindGuard(failure));
        InvalidOperationException? refusal = null;
        for (var e = failure; e is not null && refusal is null; e = e.InnerException)
        {
            if (e is InvalidOperationException ioe && ioe.Message == "OLIVE_TRACE_TIMEOUT_MS must be a positive integer.") { refusal = ioe; }
        }

        Assert.NotNull(refusal);
        Assert.DoesNotContain(value, refusal.Message);
    }

    // Positive control: proves the same seam really reaches Program.cs, so the refusal above cannot pass vacuously.
    [Fact]
    public async Task TRC_07_valid_trace_timeout_is_applied_at_start()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new TraceTimeoutFactory(db, "250");
        using var client = factory.CreateClient();
        Assert.Equal(TimeSpan.FromMilliseconds(250), factory.Services.GetRequiredService<TraceVerifierOptions>().Timeout);
        // WR-03: tuning the trace timeout leaves the directory timeout at its own default.
        Assert.Equal(TimeSpan.FromSeconds(5), factory.Services.GetRequiredService<DirectoryVerifierOptions>().Timeout);
    }

    [Fact]
    public async Task POL_02_valid_directory_timeout_is_applied_at_start_and_leaves_trace_timeout_alone()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new TraceTimeoutFactory(db, "750", "OLIVE_DIRECTORY_TIMEOUT_MS");
        using var client = factory.CreateClient();
        Assert.Equal(TimeSpan.FromMilliseconds(750), factory.Services.GetRequiredService<DirectoryVerifierOptions>().Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), factory.Services.GetRequiredService<TraceVerifierOptions>().Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public async Task POL_02_invalid_directory_timeout_refuses_start(string value)
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new TraceTimeoutFactory(db, value, "OLIVE_DIRECTORY_TIMEOUT_MS");
        Exception? failure = null;
        try
        {
            using var client = factory.CreateClient();
            await client.GetAsync("/v1/proposals/" + Guid.NewGuid(), TestContext.Current.CancellationToken);
        }
        catch (Exception e)
        {
            failure = e;
        }

        Assert.NotNull(failure);
        Assert.Null(FindGuard(failure));
        InvalidOperationException? refusal = null;
        for (var e = failure; e is not null && refusal is null; e = e.InnerException)
        {
            if (e is InvalidOperationException ioe && ioe.Message == "OLIVE_DIRECTORY_TIMEOUT_MS must be a positive integer.") { refusal = ioe; }
        }

        Assert.NotNull(refusal);
        Assert.DoesNotContain(value, refusal.Message);
    }

    [Fact]
    public void FND_03_tampered_vocabulary_copy_is_refused()
    {
        var real = Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml");
        var tmp = Path.Combine(Path.GetTempPath(), "olive-vocab-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllBytes(tmp, [.. File.ReadAllBytes(real), (byte)'\n']);
            var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckVendoredVocabulary(tmp));
            Assert.Contains("FND-03", e.Message);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void FND_03_missing_vocabulary_is_refused()
    {
        var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckVendoredVocabulary(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))));
        Assert.Contains("FND-03", e.Message);
    }

    [Fact]
    public void FND_03_real_vocabulary_copy_passes_and_exists_in_output()
    {
        var real = Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml");
        Assert.True(File.Exists(real));
        StartupGuards.CheckVendoredVocabulary(real);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EXE_04_guard_fails_when_output_path_missing()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Equal("EXE-04", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckExecutorOutput(empty)).Rule);
        var blank = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OLIVE_EXECUTOR_OUTPUT_PATH"] = "  " }).Build();
        Assert.Equal("EXE-04", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckExecutorOutput(blank)).Rule);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EXE_04_guard_fails_when_output_path_is_a_directory()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OLIVE_EXECUTOR_OUTPUT_PATH"] = Path.GetTempPath() }).Build();
        Assert.Equal("EXE-04", Assert.Throws<StartupGuardException>(() => StartupGuards.CheckExecutorOutput(cfg)).Rule);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EXE_04_guard_message_never_contains_the_path()
    {
        var path = Path.GetTempPath();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OLIVE_EXECUTOR_OUTPUT_PATH"] = path }).Build();
        var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckExecutorOutput(cfg));
        Assert.DoesNotContain(path, e.Message);
        Assert.DoesNotContain(path, e.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void EXE_04_guard_accepts_a_writable_new_file_and_writes_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "olive-guard-" + Guid.NewGuid().ToString("N"), "out.jsonl");
        try
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OLIVE_EXECUTOR_OUTPUT_PATH"] = path }).Build();
            StartupGuards.CheckExecutorOutput(cfg);
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public async Task EXE_04_startup_logs_simulated_mode()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        Assert.Null(await TryStartAsync(factory));
        Assert.Contains("execution_mode=simulated", factory.Logs.Text);
    }

    private const string DriftedRegistry = "kinds:\n  - kind: action_approval\n    subtypes: [proposal]\n    decisions: [approve, reject]\n    required_role: role:other_role\n    ref_schemes: [olive]\n    expires: true\n";
    private const string NoActionApproval = "kinds:\n  - kind: identity_review\n    subtypes: [candidate_pair]\n    decisions: [accept]\n    required_role: role:identity_steward\n    ref_schemes: [bluto]\n";

    private static IServiceProvider QueueKindProvider(Olive.Application.IQueueKindRegistry registry)
    {
        var s = new ServiceCollection();
        s.AddSingleton(registry);
        s.AddSingleton<Olive.Application.IReviewerTenantMap>(_ => Olive.Catalog.ReviewerTenantMap.LoadEmbedded());
        s.AddSingleton(_ => Olive.Catalog.CatalogLoader.LoadEmbedded(Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml")));
        return s.BuildServiceProvider();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_shipped_registry_and_catalog_pass_the_guard() =>
        StartupGuards.CheckQueueKinds(QueueKindProvider(Olive.Catalog.QueueKindRegistry.LoadEmbedded()));

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_role_drift_refuses_with_static_message()
    {
        var e = Assert.Throws<StartupGuardException>(() => StartupGuards.CheckQueueKinds(QueueKindProvider(Olive.Catalog.QueueKindRegistry.Parse(DriftedRegistry))));
        Assert.Contains("QUE-02", e.Message);
        Assert.DoesNotContain("role:other_role", e.Message);
        Assert.DoesNotContain("role:case_supervisor", e.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_missing_action_approval_kind_refuses() =>
        Assert.Contains("QUE-02", Assert.Throws<StartupGuardException>(() =>
            StartupGuards.CheckQueueKinds(QueueKindProvider(Olive.Catalog.QueueKindRegistry.Parse(NoActionApproval)))).Message);

    [Fact]
    [Trait("Category", "Unit")]
    public void QUE_02_unresolvable_registry_refuses() =>
        Assert.Contains("QUE-02", Assert.Throws<StartupGuardException>(() =>
            StartupGuards.CheckQueueKinds(new ServiceCollection().BuildServiceProvider())).Message);

    [Fact]
    public async Task QUE_02_host_starts_with_shipped_registry()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db);
        Assert.Null(await TryStartAsync(factory));
    }

    [Fact]
    public async Task QUE_02_host_refuses_start_on_role_drift()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, configureServices: s =>
        {
            s.RemoveAll<Olive.Application.IQueueKindRegistry>();
            s.AddSingleton<Olive.Application.IQueueKindRegistry>(Olive.Catalog.QueueKindRegistry.Parse(DriftedRegistry));
        });
        var guard = await TryStartAsync(factory);
        Assert.NotNull(guard);
        Assert.Equal("QUE-02", guard.Rule);
    }

    private sealed class WrappingFactoryStub: IKeyIntrospector, INonProductionOnly
    {
        public Task<IntrospectionResult> IntrospectAsync(Olive.Domain.SessionKey key, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    // Not INonProductionOnly itself; the guard must see through it to what it wraps.
    private sealed class DecoratingIntrospector(IKeyIntrospector inner) : IKeyIntrospector, IComponentDecorator
    {
        public object Inner => inner;

        public Task<IntrospectionResult> IntrospectAsync(Olive.Domain.SessionKey key, CancellationToken ct) => inner.IntrospectAsync(key, ct);
    }

    private sealed class NonProductionTimeProvider : TimeProvider, INonProductionOnly;
}
