using Npgsql;
using Olive.Application;
using Olive.Catalog;
using Olive.Host.Auth;
using Olive.Host.Endpoints;
using Olive.Host.Problems;
using Olive.Host.Startup;
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
builder.Services.AddSingleton<IAgentAllowlist>(_ => AgentAllowlist.LoadEmbedded());
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
app.Run();

public partial class Program;
