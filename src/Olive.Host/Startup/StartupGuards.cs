using Olive.Application;

namespace Olive.Host.Startup;

public static class StartupGuards
{
    public static OliveEnvironment ResolveEnvironment(IConfiguration configuration) => OliveEnvironment.Parse(configuration);

    // D-19: only the Migrator may hold the owner-capable URL; presence (even empty) in the service refuses start.
    public static void CheckMigratorUrlAbsent(IConfiguration configuration)
    {
        if (configuration["OLIVE_MIGRATOR_URL"] is not null)
        {
            throw new StartupGuardException("D-19", "the migrator credential must not be present in the service configuration");
        }
    }

    // FND-03: raw-byte SHA-256 of the copied vendored vocabulary must match the constant.
    public static void CheckVendoredVocabulary(string path)
    {
        if (!VendoredContracts.VerifyVocabulary(path))
        {
            throw new StartupGuardException("FND-03", "the vendored vocabulary is missing or does not match the pinned hash");
        }
    }

    // FND-05: in production no INonProductionOnly implementation may be registered or resolvable.
    public static void CheckNoNonProductionComponents(IServiceCollection services, IServiceProvider provider, OliveEnvironment env)
    {
        if (!env.IsProduction)
        {
            return;
        }

        const string reason = "a non-production component is registered";
        foreach (var d in services)
        {
            var types = d.IsKeyedService
                ? new[] { d.KeyedImplementationInstance?.GetType(), d.KeyedImplementationType }
                : new[] { d.ImplementationInstance?.GetType(), d.ImplementationType };
            if (types.Any(t => t is not null && typeof(INonProductionOnly).IsAssignableFrom(t)))
            {
                throw new StartupGuardException("FND-05", reason);
            }
        }

        try
        {
            using var scope = provider.CreateScope();
            var serviceTypes = services
                .Where(d => !d.IsKeyedService && !d.ServiceType.IsGenericTypeDefinition && !d.ServiceType.ContainsGenericParameters
                    && (d.ServiceType.Assembly.GetName().Name ?? "").StartsWith("Olive.", StringComparison.Ordinal))
                .Select(d => d.ServiceType)
                .Distinct()
                .ToList();
            foreach (var serviceType in serviceTypes)
            {
                if (scope.ServiceProvider.GetServices(serviceType).Any(i => i is INonProductionOnly))
                {
                    throw new StartupGuardException("FND-05", reason);
                }
            }
        }
        catch (StartupGuardException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new StartupGuardException("FND-05", "component resolution failed while checking for non-production components");
        }
    }
}
