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

    // API-10: the source-controlled catalog, and the envelope contract it is applied through (WR-05), are loaded eagerly so a
    // fault stops the host before it serves traffic.
    public static void CheckCatalog(IServiceProvider provider)
    {
        try
        {
            _ = provider.GetRequiredService<Olive.Catalog.CatalogSet>();
            Olive.Catalog.SubmissionValidator.EnsureContractLoaded();
            _ = provider.GetRequiredService<Olive.Catalog.SubmissionValidator>();
        }
        catch (Exception)
        {
            // Any fault, whatever its type, maps to the static refusal; the original message may quote catalog content (WR-01).
            throw new StartupGuardException("API-10", "the source-controlled catalog failed validation");
        }
    }

    // QUE-02 / D-21: the kind registry and reviewer tenant map load, and every catalog required_approval equals the
    // action_approval kind's required_role so the two sources of truth cannot drift.
    public static void CheckQueueKinds(IServiceProvider provider)
    {
        try
        {
            var registry = provider.GetRequiredService<IQueueKindRegistry>();
            _ = provider.GetRequiredService<IReviewerTenantMap>();
            if (!registry.TryGet("action_approval", out var kind))
            {
                throw new InvalidOperationException();
            }

            foreach (var release in provider.GetRequiredService<Olive.Catalog.CatalogSet>().Releases)
            {
                foreach (var entry in release.Entries)
                {
                    if (entry.EntryJson.TryGetProperty("required_approval", out var ra) && ra.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.Equals(ra.GetString(), kind.RequiredRole, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException();
                    }
                }
            }
        }
        catch (Exception)
        {
            throw new StartupGuardException("QUE-02", "the review queue kind registry failed validation");
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

        // Resolution pass (WR-03): every closed service type from any assembly, keyed and unkeyed, so factory
        // registrations are seen; each resolved instance is unwrapped through its IComponentDecorator chain.
        try
        {
            using var scope = provider.CreateScope();
            var closed = services.Where(d => !d.ServiceType.IsGenericTypeDefinition && !d.ServiceType.ContainsGenericParameters).ToList();
            foreach (var serviceType in closed.Where(d => !d.IsKeyedService).Select(d => d.ServiceType).Distinct())
            {
                if (scope.ServiceProvider.GetServices(serviceType).Any(IsNonProduction))
                {
                    throw new StartupGuardException("FND-05", reason);
                }
            }

            foreach (var (serviceType, key) in closed.Where(d => d.IsKeyedService && !Equals(d.ServiceKey, KeyedService.AnyKey))
                .Select(d => (d.ServiceType, d.ServiceKey)).Distinct())
            {
                if (scope.ServiceProvider.GetKeyedServices(serviceType, key).Any(IsNonProduction))
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

    private const int MaxDecoratorDepth = 32;

    // A decorator chain deeper than the limit (or a cycle) is refused rather than trusted.
    private static bool IsNonProduction(object? instance)
    {
        for (var depth = 0; instance is not null; depth++)
        {
            if (instance is INonProductionOnly || depth >= MaxDecoratorDepth)
            {
                return true;
            }

            instance = instance is IComponentDecorator d ? d.Inner : null;
        }

        return false;
    }
}
