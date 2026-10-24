using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Executor.Simulated;
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
// UI-01: the Hosting request-start line logs the full query string, which on /signin-oidc carries the one-time authorization code.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
// UI-01: the OpenIdConnect handler logs the raw callback message (including the code) at Debug.
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Information);
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
builder.Services.AddSingleton<IQueueStore, PostgresQueueStore>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<IAuditStore, PostgresAuditStore>();
// EXE-01 / D-13: the executor is simulated and not INonProductionOnly. A singleton so its write semaphore serialises all writers.
builder.Services.AddSingleton<IExecutionStore>(sp => new PostgresExecutionStore(sp.GetRequiredService<TenantUnitOfWork>()));
builder.Services.AddSingleton<IActionExecutor>(sp =>
{
    var path = sp.GetRequiredService<IConfiguration>()["OLIVE_EXECUTOR_OUTPUT_PATH"];
    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException("OLIVE_EXECUTOR_OUTPUT_PATH is not configured.");
    }

    return new SimulatedActionExecutor(new SimulatedExecutorOptions(path));
});
builder.Services.AddSingleton<IActionCatalogCheck, CatalogExecutionCheck>();
builder.Services.AddSingleton<ExecutionService>();
// D-03 / EXE-01: one sweep of approved-but-unfinished proposals, no timer. It runs after ApplicationStarted, i.e. after every
// startup guard; the singleton registration lets tests await its Completion.
builder.Services.AddSingleton<ExecutionSweep>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExecutionSweep>());
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
// UI-01: Entra sign-in is configured only from environment variables; absent settings leave the dev picker as the only sign-in path.
var oidc = OidcConfig.Read(builder.Configuration);
var authentication = builder.Services.AddAuthentication(AgentKeyAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, _ => { })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevReviewerAuthenticationHandler>(DevReviewerAuthenticationHandler.SchemeName, _ => { })
    .AddReviewerSession(oidc is null ? ReviewerCookie.LoginPath : "/auth/sign-in");
if (oidc is not null)
{
    authentication.AddReviewerOidc(oidc);
}
// D-07: dev reviewer identities from a source-controlled fixture (INonProductionOnly, so FND-05 bars it in production).
builder.Services.AddSingleton<IReviewerIdentitySource>(_ => FixtureReviewerIdentitySource.CreateDefault());
// D-15: the reviewer cookie scheme is named only by this UI policy, never by a /v1 route.
builder.Services.AddAuthorization(o => o.AddPolicy("ReviewerUi", p => p.AddAuthenticationSchemes(ReviewerCookie.Scheme).RequireAuthenticatedUser()));
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Review", "ReviewerUi");
    o.Conventions.AllowAnonymousToFolder("/Dev");
    // Sign-out authenticates the cookie so its antiforgery token (bound to the signed-in user) validates; SignedOut stays anonymous.
    o.Conventions.AuthorizePage("/Account/SignOut", "ReviewerUi");
    o.Conventions.AllowAnonymousToPage("/Account/SignedOut");
    o.Conventions.AllowAnonymousToPage("/Account/SignIn");
    o.Conventions.AllowAnonymousToPage("/Account/Failed");
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "__Host-olive-af";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Lax;
});
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("olive");
if (builder.Configuration["OLIVE_DATA_PROTECTION_PATH"] is { Length: > 0 } keyPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}

// D-02 / D-18: unconditional; as an INonProductionOnly marker it makes FND-05 refuse production while the picker exists.
builder.Services.AddSingleton<DevSignInGate>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseOliveSecurityHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapGet("/", () => Results.Redirect("/review"));
app.MapRazorPages();
app.MapProposalEndpoints();
app.MapQueueEndpoints();
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
