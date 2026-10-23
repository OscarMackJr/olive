using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Host.Alarms;
using Olive.Host.Auth;
using Olive.Host.Endpoints;
using Olive.Host.Problems;
using Olive.Host.Startup;
using Olive.Integrations.Entra;
using Olive.Integrations.Hometown;
using Olive.Integrations.Popeye;
using Olive.Persistence;

// Forced to Production so the developer exception page is never enabled.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Production });
builder.Configuration.AddDotEnvBelowEnvironment(Path.Combine(builder.Environment.ContentRootPath, ".env"));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ProposalEndpoints.MaxBodyBytes);

builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
builder.Services.AddSingleton(new ServiceCollectionAccessor(builder.Services));
builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, StartupGuardFilter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SubmissionValidator>();
builder.Services.AddSingleton(sp =>
{
    var url = sp.GetRequiredService<IConfiguration>()["OLIVE_POSTGRES_URL"];
    if (string.IsNullOrWhiteSpace(url))
    {
        throw new InvalidOperationException("OLIVE_POSTGRES_URL is not configured.");
    }

    return NpgsqlDataSource.Create(PostgresUrl.ToNpgsql(url));
});
builder.Services.AddSingleton<TenantUnitOfWork>();
builder.Services.AddSingleton<IProposalStore, PostgresProposalStore>();
// Phase 1 has only the stub introspector; the production guard in 01-06 refuses it.
builder.Services.AddSingleton<IKeyIntrospector>(sp => StubKeyIntrospector.CreateDefault(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IOperationalAlarm, LoggingOperationalAlarm>();
builder.Services.AddSingleton<ITokenIssuer>(sp => StubTokenIssuer.CreateDefault(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ITraceResolver>(_ => new CachingTraceResolver(FixtureTraceResolver.CreateDefault()));
builder.Services.AddSingleton<ServiceTokenProvider>(sp => new ServiceTokenProvider(
    sp.GetRequiredService<ITokenIssuer>(), sp.GetRequiredService<IOperationalAlarm>(), sp.GetRequiredService<TimeProvider>()));
// D-12: validated eagerly so a bad value stops startup rather than the first request.
var traceTimeout = ReadTimeoutMs(builder.Configuration, "OLIVE_TRACE_TIMEOUT_MS");
builder.Services.AddSingleton(traceTimeout is { } t ? new TraceVerifierOptions(t) : new TraceVerifierOptions());
builder.Services.AddSingleton<TraceVerifier>();
// D-02/D-07: read-only directory lookup, fixture-backed and barred in production by the startup guard; deliberately not cached.
// WR-03: its timeout is its own setting, so tuning the trace timeout never changes directory fail-closed behaviour.
var directoryTimeout = ReadTimeoutMs(builder.Configuration, "OLIVE_DIRECTORY_TIMEOUT_MS");
builder.Services.AddSingleton(directoryTimeout is { } d ? new DirectoryVerifierOptions(d) : new DirectoryVerifierOptions());
builder.Services.AddSingleton<IDirectoryGroupResolver>(_ => FixtureDirectoryGroupResolver.CreateDefault());
builder.Services.AddSingleton<DirectoryVerifier>();
// D-06 (Q14 ruling): read-only (oid, tid) resolution for the approve-path self-approval check; fixture-backed, barred in production, own timeout.
var identityTimeout = ReadTimeoutMs(builder.Configuration, "OLIVE_IDENTITY_RESOLVER_TIMEOUT_MS");
builder.Services.AddSingleton(identityTimeout is { } it ? new UserIdentityVerifierOptions(it) : new UserIdentityVerifierOptions());
builder.Services.AddSingleton<IUserIdentityResolver>(_ => FixtureUserIdentityResolver.CreateDefault());
builder.Services.AddSingleton<UserIdentityVerifier>();
builder.Services.AddSingleton<ApprovalIdentityCheck>();
builder.Services.AddSingleton<IAgentAllowlist>(_ => AgentAllowlist.LoadEmbedded());
builder.Services.AddSingleton<IQueueKindRegistry>(_ => QueueKindRegistry.LoadEmbedded());
builder.Services.AddSingleton<IReviewerTenantMap>(_ => ReviewerTenantMap.LoadEmbedded());
builder.Services.AddSingleton(_ => CatalogLoader.LoadEmbedded(Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml")));
builder.Services.AddAuthentication(AgentKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapProposalEndpoints();
app.MapCatalogEndpoints();
app.Run();

// Absent means the default; present must be a positive integer of milliseconds. The message names the setting, never the value.
static TimeSpan? ReadTimeoutMs(IConfiguration configuration, string name)
{
    var text = configuration[name];
    if (string.IsNullOrWhiteSpace(text))
    {
        return null;
    }

    if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms <= 0)
    {
        throw new InvalidOperationException(name + " must be a positive integer.");
    }

    return TimeSpan.FromMilliseconds(ms);
}

public partial class Program;
