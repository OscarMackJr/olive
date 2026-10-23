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
var traceTimeoutText = builder.Configuration["OLIVE_TRACE_TIMEOUT_MS"];
var traceOptions = new TraceVerifierOptions();
if (!string.IsNullOrWhiteSpace(traceTimeoutText))
{
    if (!int.TryParse(traceTimeoutText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var traceTimeoutMs) || traceTimeoutMs <= 0)
    {
        throw new InvalidOperationException("OLIVE_TRACE_TIMEOUT_MS must be a positive integer.");
    }

    traceOptions = new TraceVerifierOptions(TimeSpan.FromMilliseconds(traceTimeoutMs));
}

builder.Services.AddSingleton(traceOptions);
builder.Services.AddSingleton<TraceVerifier>();
// D-02/D-07: read-only directory lookup, fixture-backed and barred in production by the startup guard; deliberately not cached.
builder.Services.AddSingleton<IDirectoryGroupResolver>(_ => FixtureDirectoryGroupResolver.CreateDefault());
builder.Services.AddSingleton<DirectoryVerifier>();
builder.Services.AddSingleton<IAgentAllowlist>(_ => AgentAllowlist.LoadEmbedded());
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

public partial class Program;
