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

    // D-06 / QUE-02: every producer's kinds, subtypes and ref scheme must be registered in the queue-kind registry, and the
    // identity_review kind may only be filed by bluto (migration 0008 constrains it). Static message: no registry content is quoted.
    public static void CheckProducers(IServiceProvider provider)
    {
        try
        {
            var producers = provider.GetRequiredService<IProducerRegistry>();
            var kinds = provider.GetRequiredService<IQueueKindRegistry>();
            foreach (var producer in producers.All)
            {
                foreach (var (kindName, subtypes) in producer.KindSubtypes)
                {
                    if (!kinds.TryGet(kindName, out var kind)
                        || subtypes.Any(s => !kind.Subtypes.Contains(s))
                        || !kind.RefSchemes.Contains(producer.Producer)
                        || (kindName == "identity_review" && producer.Producer != "bluto"))
                    {
                        throw new InvalidOperationException();
                    }
                }
            }
        }
        catch (Exception)
        {
            throw new StartupGuardException("QUE-02", "the producer registry failed validation");
        }
    }

    // Q17 / T-05-20: the dev reviewer scheme must not exist in production, whatever identity source is registered.
    public static void CheckDevReviewerNotInProduction(IServiceProvider provider, OliveEnvironment env)
    {
        if (!env.IsProduction)
        {
            return;
        }

        var scheme = provider.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>()
            .GetSchemeAsync(Auth.DevReviewerAuthenticationHandler.SchemeName).GetAwaiter().GetResult();
        if (scheme is not null)
        {
            throw new StartupGuardException("Q17", "the dev reviewer authentication scheme is registered in production");
        }
    }

    // UI-01 / D-18: production requires a complete, https Entra configuration and no dev sign-in path. Messages are static: they
    // name settings only, never a configured value.
    public static void CheckOidcConfiguration(IConfiguration configuration, OliveEnvironment env, IServiceCollection? services = null)
    {
        if (!env.IsProduction)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration["OLIVE_OIDC_AUTHORITY"]))
        {
            throw new StartupGuardException("UI-01", "OLIVE_OIDC_AUTHORITY is required in production");
        }

        if (string.IsNullOrWhiteSpace(configuration["OLIVE_OIDC_CLIENT_ID"]))
        {
            throw new StartupGuardException("UI-01", "OLIVE_OIDC_CLIENT_ID is required in production");
        }

        if (string.IsNullOrWhiteSpace(configuration["OLIVE_OIDC_CLIENT_SECRET"]))
        {
            throw new StartupGuardException("UI-01", "OLIVE_OIDC_CLIENT_SECRET is required in production");
        }

        if (!Uri.TryCreate(configuration["OLIVE_OIDC_AUTHORITY"]!.Trim(), UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
        {
            throw new StartupGuardException("UI-01", "OLIVE_OIDC_AUTHORITY must be an absolute https URL in production");
        }

        if (services is not null && services.Any(d => d.ServiceType == typeof(Auth.DevSignInGate) || d.ImplementationType == typeof(Auth.DevSignInGate)))
        {
            throw new StartupGuardException("UI-01", "the dev sign-in picker is registered in production");
        }
    }

    // EXE-04 / D-11: the simulated executor's JSONL path must be set and appendable. The message is static: it never carries the path
    // or the underlying exception text.
    public static void CheckExecutorOutput(IConfiguration configuration)
    {
        const string reason = "the executor output path is missing or not writable";
        var path = configuration["OLIVE_EXECUTOR_OUTPUT_PATH"];
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new StartupGuardException("EXE-04", reason);
        }

        try
        {
            if (Directory.Exists(path))
            {
                throw new InvalidOperationException();
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            throw new StartupGuardException("EXE-04", reason);
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
