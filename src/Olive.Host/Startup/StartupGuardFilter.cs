using Microsoft.AspNetCore.Hosting;

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
        return next;
    }
}
