using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Olive.Application;
using Olive.Integrations.Popeye;
using Olive.Persistence;

namespace Olive.Tests;

public sealed class OliveApiFactory(TestDatabase db, IKeyIntrospector? introspector = null, IDictionary<string, string?>? extraConfig = null, string? environment = "ci", Action<IServiceCollection>? configureServices = null, bool replaceIntrospector = true)
    : WebApplicationFactory<Program>
{
    public InMemoryLogCapture Logs { get; } = new();

    public List<string> Events { get; } = [];

    public RecordingKeyIntrospector Introspector { get; private set; } = null!;

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
        builder.ConfigureLogging(l =>
        {
            l.ClearProviders();
            l.SetMinimumLevel(LogLevel.Trace);
            l.AddProvider(Logs);
        });
        builder.ConfigureTestServices(services =>
        {
            if (replaceIntrospector)
            {
                Introspector = new RecordingKeyIntrospector(introspector ?? StubKeyIntrospector.CreateDefault(TimeProvider.System), Events);
                services.AddSingleton<IKeyIntrospector>(Introspector);
            }

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
