using Olive.Application;

namespace Olive.Host.Startup;

// D-03 / EXE-01: runs once at host start. Tenants come from the source-controlled map (AllTenants); each is set through
// the tenant-scoped store, so RLS is not weakened. Faults are logged statically and never stop host start.
public sealed class ExecutionSweep(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Dependencies resolve here, not in the constructor, so the startup guards keep reporting their own refusals first
        // and a sweep fault can never stop the host.
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Olive.Host.ExecutionSweep");
        IExecutionStore store;
        ExecutionService execution;
        IReadOnlyCollection<Guid> allTenants;
        try
        {
            store = services.GetRequiredService<IExecutionStore>();
            execution = services.GetRequiredService<ExecutionService>();
            allTenants = services.GetRequiredService<IReviewerTenantMap>().AllTenants;
        }
        catch (Exception)
        {
            log.LogError("Execution sweep could not start.");
            return;
        }

        foreach (var tenantId in allTenants)
        {
            IReadOnlyList<Guid> ids;
            try
            {
                ids = await store.ListApprovedAsync(tenantId, cancellationToken);
            }
            catch (Exception)
            {
                log.LogError("Execution sweep could not list approved proposals.");
                continue;
            }

            foreach (var id in ids)
            {
                try
                {
                    await execution.ExecuteAsync(tenantId, id, cancellationToken);
                }
                catch (Exception)
                {
                    log.LogError("Execution sweep attempt failed.");
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
