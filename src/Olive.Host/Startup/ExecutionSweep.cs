using Olive.Application;

namespace Olive.Host.Startup;

// D-03 / EXE-01: one sweep after host start. Tenants come from the source-controlled map (AllTenants); each is set through
// the tenant-scoped store, so RLS is not weakened. Faults are logged statically and never stop host start.
// EXE-04: the sweep runs only after ApplicationStarted, which is raised after StartupGuardFilter has run every guard, so a
// host that refuses to start never executes. Dependencies resolve inside the sweep so a resolution fault is logged and
// cannot stop the host.
// EXE-05 / D-01: the stopping token is consulted only between attempts. An attempt that has begun always runs to completion
// with CancellationToken.None (the decide-path rule), and StopAsync drains the sweep, so a routine shutdown never leaves a
// started-only row that the next start would record as execution_in_doubt.
public sealed class ExecutionSweep(IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _sweep = Task.CompletedTask;

    // Completes (never faults) when the sweep has finished, or when the host stops before it started.
    public Task Completion => _completion.Task;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => Volatile.Write(ref _sweep, Task.Run(() => SweepAsync(lifetime.ApplicationStopping))));
        lifetime.ApplicationStopping.Register(() =>
        {
            if (Volatile.Read(ref _sweep).IsCompleted) { _completion.TrySetResult(); }
        });
        return Task.CompletedTask;
    }

    private async Task SweepAsync(CancellationToken stopping)
    {
        try
        {
            await RunAsync(stopping);
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

    private async Task RunAsync(CancellationToken stopping)
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
            if (stopping.IsCancellationRequested) { return; }
            IReadOnlyList<Guid> ids;
            try
            {
                ids = await store.ListApprovedAsync(tenantId, stopping);
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                log.LogError("Execution sweep could not list approved proposals. tenant_id={TenantId}", tenantId);
                continue;
            }

            foreach (var id in ids)
            {
                // Stop between attempts only; never cancel one that has begun.
                if (stopping.IsCancellationRequested) { return; }
                try
                {
                    var outcome = await execution.ExecuteAsync(tenantId, id, CancellationToken.None);
                    if (outcome is ExecutionResult.LockTimeout)
                    {
                        log.LogWarning("Execution sweep lock wait timed out. proposal_id={ProposalId} tenant_id={TenantId}", id, tenantId);
                    }
                }
                catch (Exception)
                {
                    log.LogError("Execution sweep attempt failed. proposal_id={ProposalId} tenant_id={TenantId}", id, tenantId);
                }
            }
        }
    }

    // Drains an in-flight sweep so an attempt between the target write and its finished row can record its outcome before
    // the service provider (and its data source) is disposed. Bounded by the host shutdown timeout.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Volatile.Read(ref _sweep).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _completion.TrySetResult();
        }
    }
}
