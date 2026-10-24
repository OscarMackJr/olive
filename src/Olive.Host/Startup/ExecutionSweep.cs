using Olive.Application;

namespace Olive.Host.Startup;

// D-03 / EXE-01: one sweep after host start. Tenants come from the source-controlled map (AllTenants); each is set through
// the tenant-scoped store, so RLS is not weakened. Faults are logged statically and never stop host start.
// EXE-04: the sweep runs only after ApplicationStarted, which is raised after StartupGuardFilter has run every guard, so a
// host that refuses to start never executes. Dependencies resolve inside the sweep so a resolution fault is logged and
// cannot stop the host.
public sealed class ExecutionSweep(IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes (never faults) when the sweep has finished, or when the host stops before it started.
    public Task Completion => _completion.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => SweepAsync(lifetime.ApplicationStopping)));
        lifetime.ApplicationStopping.Register(() => _completion.TrySetResult());
        return Task.CompletedTask;
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Never surfaces: the sweep must not affect the host.
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
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
