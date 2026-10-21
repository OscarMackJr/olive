using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Olive.Persistence;

namespace Olive.Host.Startup;

// Runs every guard while the host builds its pipeline, which is before Kestrel listens (and also under WebApplicationFactory).
public sealed class StartupGuardFilter(IConfiguration configuration, IServiceProvider provider, ServiceCollectionAccessor accessor) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var env = StartupGuards.ResolveEnvironment(configuration);
        StartupGuards.CheckMigratorUrlAbsent(configuration);
        StartupGuards.CheckVendoredVocabulary(Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml"));
        StartupGuards.CheckNoNonProductionComponents(accessor.Services, provider, env);
        CheckDatabase();
        return next;
    }

    private void CheckDatabase()
    {
        IReadOnlyList<string> violations;
        try
        {
            var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
            violations = DatabaseSafetyChecks.CheckAsync(dataSource, MigrationCatalog.LoadEmbedded(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            throw new StartupGuardException("DB-07", "the database is not configured or could not be checked");
        }

        if (violations.Count > 0)
        {
            throw new StartupGuardException("DB-07", "database role or migration state is unsafe [" + string.Join(", ", violations) + "]");
        }
    }
}
