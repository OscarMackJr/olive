using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Olive.Application;
using Olive.Domain;
using Olive.Integrations.Entra;
using Olive.Integrations.Hometown;
using Olive.Integrations.Popeye;
using Olive.Persistence;

namespace Olive.Tests;

public enum TraceMode { AutoFixture, Explicit }

public sealed class OliveApiFactory(TestDatabase db, IKeyIntrospector? introspector = null, IDictionary<string, string?>? extraConfig = null, string? environment = "ci", Action<IServiceCollection>? configureServices = null, bool replaceIntrospector = true, TraceMode traceMode = TraceMode.AutoFixture, ITraceResolver? innerResolver = null, ITokenIssuer? innerIssuer = null, IDirectoryGroupResolver? innerDirectory = null)
    : WebApplicationFactory<Program>
{
    public InMemoryLogCapture Logs { get; } = new();

    public List<string> Events { get; } = [];

    public RecordingKeyIntrospector Introspector { get; private set; } = null!;

    public FixtureTraceResolver FixtureResolver { get; } = new();

    public FixtureDirectoryGroupResolver FixtureDirectory { get; } = FixtureDirectoryGroupResolver.CreateDefault();

    public RecordingDirectoryResolver Directory { get; private set; } = null!;

    public RecordingAlarm Alarms { get; } = new();

    public List<OutboundCall> OutboundCalls { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production so the auto-added developer exception page can never appear.
        builder.UseEnvironment(Environments.Production);
        var url = db.AppUrl;
        NoSharedPostgres.Assert(PostgresUrl.ToNpgsql(url));
        var config = new Dictionary<string, string?> { ["OLIVE_ENVIRONMENT"] = environment, ["OLIVE_POSTGRES_URL"] = url };
        if (extraConfig is not null)
        {
            foreach (var kv in extraConfig) { config[kv.Key] = kv.Value; }
        }

        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
        // Program.cs reads the trace timeout before Build(), which ConfigureAppConfiguration does not reach.
        if (config.TryGetValue("OLIVE_TRACE_TIMEOUT_MS", out var traceTimeout))
        {
            builder.UseSetting("OLIVE_TRACE_TIMEOUT_MS", traceTimeout);
        }

        builder.ConfigureLogging(l =>
        {
            l.ClearProviders();
            l.SetMinimumLevel(LogLevel.Trace);
            l.AddProvider(Logs);
        });
        builder.ConfigureTestServices(services =>
        {
            // The unrecorded introspector feeds the token stub and the auto-fixture middleware so "introspect" events stay clean.
            var inner = introspector ?? StubKeyIntrospector.CreateDefault(TimeProvider.System);
            if (replaceIntrospector)
            {
                Introspector = new RecordingKeyIntrospector(inner, Events, OutboundCalls);
                services.AddSingleton<IKeyIntrospector>(Introspector);
            }

            services.AddSingleton<IOperationalAlarm>(Alarms);
            services.AddSingleton<ITokenIssuer>(new RecordingTokenIssuer(innerIssuer ?? new StubTokenIssuer(inner, TimeProvider.System), Events, OutboundCalls));
            var recordingResolver = new RecordingTraceResolver(innerResolver ?? (traceMode == TraceMode.Explicit ? FixtureResolver : new AutoFixtureTraceResolver(FixtureResolver)), Events, OutboundCalls);
            if (traceMode == TraceMode.Explicit)
            {
                services.AddSingleton<ITraceResolver>(new CachingTraceResolver(recordingResolver));
            }
            else
            {
                services.AddSingleton<ITraceResolver>(recordingResolver);
                services.AddSingleton<IStartupFilter>(new AutoFixtureStartupFilter(inner));
            }

            Directory = new RecordingDirectoryResolver(innerDirectory ?? FixtureDirectory, Events, OutboundCalls);
            services.AddSingleton<IDirectoryGroupResolver>(Directory);
            services.AddScoped<IProposalStore>(sp =>
                new RecordingProposalStore(ActivatorUtilities.CreateInstance<PostgresProposalStore>(sp), Events));
            configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateKeyClient(string sessionKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AgentKeyHeader.Scheme, sessionKey);
        return client;
    }
}

// Test-only (never in src): before POST /v1/proposals reaches the endpoint, registers a matching fixture for the posted body so
// the legacy integration tests keep their meaning with trace verification mandatory (D-14).
internal sealed class AutoFixtureTraceResolver(ITraceResolver fallback) : ITraceResolver
{
    // Per-request facts flow through the async context, so concurrent requests sharing a trace_ref never overwrite each other.
    internal static readonly AsyncLocal<TraceFacts?> Current = new();

    public Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct) =>
        Current.Value is { } facts && facts.TraceId == traceId
            ? Task.FromResult<TraceLookup>(new TraceLookup.Found(facts, TraceVerificationSources.Fixture))
            : fallback.ResolveAsync(grant, traceId, ct);
}

internal sealed class AutoFixtureStartupFilter(IKeyIntrospector introspector) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nxt) =>
        {
            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path == "/v1/proposals")
            {
                AutoFixtureTraceResolver.Current.Value = await DeriveAsync(ctx);
            }

            await nxt();
        });
        next(app);
    };

    private async Task<TraceFacts?> DeriveAsync(HttpContext ctx)
    {
        try
        {
            var sessionKey = AgentKeyHeader.TryRead(ctx.Request.Headers[AgentKeyHeader.Name].ToString());
            ctx.Request.EnableBuffering();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            ctx.Request.Body.Position = 0;
            if (sessionKey is null)
            {
                return null;
            }

            var result = await introspector.IntrospectAsync(sessionKey, CancellationToken.None);
            if (!result.Active || result.Key is not { AgentId: not null } k)
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            var traceRef = root.GetProperty("trace_ref").GetString()!;
            var runId = root.GetProperty("run_id").GetString();
            return new TraceFacts(traceRef, "ate/0.4", runId, k.TenantId, k.AgentId, Jcs.ProposalHash(root), DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            // Tolerant by design: a body the endpoint will reject needs no fixture.
            return null;
        }
    }
}
