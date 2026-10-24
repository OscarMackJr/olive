---
phase: 06-simulated-execution-and-audit-chain
reviewed: 2026-10-24T00:00:00Z
depth: standard
files_reviewed: 47
files_reviewed_list:
  - build/demo-beat2.sh
  - contracts/execution-record/v1/execution-record.invalid.json
  - contracts/execution-record/v1/execution-record.schema.json
  - contracts/execution-record/v1/execution-record.valid.json
  - sql/migrations/0009_execution_ledger.sql
  - src/Olive.Application/ExecutionReasons.cs
  - src/Olive.Application/ExecutionService.cs
  - src/Olive.Application/IActionCatalogCheck.cs
  - src/Olive.Application/IActionExecutor.cs
  - src/Olive.Application/IExecutionStore.cs
  - src/Olive.Application/IReviewerTenantMap.cs
  - src/Olive.Catalog/CatalogExecutionCheck.cs
  - src/Olive.Catalog/ReviewerTenantMap.cs
  - src/Olive.Executor.Simulated/SimulatedActionExecutor.cs
  - src/Olive.Executor.Simulated/SimulatedExecutorOptions.cs
  - src/Olive.Host/Endpoints/QueueEndpoints.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/ExecutionSweep.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Persistence/ExecutionSql.cs
  - src/Olive.Persistence/PostgresExecutionStore.cs
  - src/Olive.Persistence/TenantUnitOfWork.cs
  - tests/Olive.Tests/Beat2DemoTests.cs
  - tests/Olive.Tests/CatalogExecutionCheckTests.cs
  - tests/Olive.Tests/ContainmentTests.cs
  - tests/Olive.Tests/ContractSchemaTests.cs
  - tests/Olive.Tests/ExecutionChainViewTests.cs
  - tests/Olive.Tests/ExecutionConcurrencyTests.cs
  - tests/Olive.Tests/ExecutionEndpointTests.cs
  - tests/Olive.Tests/ExecutionFailureTests.cs
  - tests/Olive.Tests/ExecutionLedgerTests.cs
  - tests/Olive.Tests/ExecutionServiceTests.cs
  - tests/Olive.Tests/ExecutionSweepTests.cs
  - tests/Olive.Tests/FaultingDoubles.cs
  - tests/Olive.Tests/MigrationRunnerTests.cs
  - tests/Olive.Tests/OliveApiFactory.cs
  - tests/Olive.Tests/PrivilegeMatrixTests.cs
  - tests/Olive.Tests/QueueDecisionTests.cs
  - tests/Olive.Tests/ReviewerTenantMapTests.cs
  - tests/Olive.Tests/RlsPatternTests.cs
  - tests/Olive.Tests/SelfApprovalTests.cs
  - tests/Olive.Tests/SentinelScan.cs
  - tests/Olive.Tests/SimulatedActionExecutorTests.cs
  - tests/Olive.Tests/StartupGuardTests.cs
  - tests/Olive.Tests/TenantIsolationAblationTests.cs
  - tests/Olive.Tests/TenantUnitOfWorkTests.cs
findings:
  critical: 0
  warning: 4
  info: 6
  total: 10
status: issues_found
---

# Phase 6: Code Review Report (re-review after gap closure 06-08 / 06-09)

**Reviewed:** 2026-10-24
**Depth:** standard
**Files Reviewed:** 47
**Status:** issues_found

## Narrative Findings (AI reviewer)

## Summary

This is a re-review after gap-closure plans 06-08 (CR-01) and 06-09 (WR-01, WR-02). Commits reviewed: 3e9e1b2, 73298d0, eff7af6.

**Status of the previous findings:**

| Prior ID | Status | Notes |
|----------|--------|-------|
| CR-01 sweep runs before startup guards | **Resolved** | `ExecutionSweep.StartAsync` now only registers on `ApplicationStarted`. The host raises that event after `GenericWebHostService.StartAsync` has built the pipeline, so every guard in `StartupGuardFilter.Configure` runs first. A refusing host never raises `ApplicationStarted`. Regression test `EXE_04_failing_startup_guard_prevents_sweep_execution` would fail against the old code. The fix adds a new shutdown hazard; see WR-01 below. |
| WR-01 executor exception recorded as definite failure | **Resolved (exception path)** | Exceptions now record `execution_in_doubt` (`ExecutionService.cs:54-59`). `ExecutorFailed` is removed and no references remain. The cancellation half of the old finding remains open and is now more likely to happen; see WR-01 below. |
| WR-02 fail-open expiry | **Resolved** | Expiry is unconditional (`ExecutionService.cs:34-38`) and falls back to `execution_failed`. |
| WR-03 lock timeout equals command timeout | **Open** | Carried forward as WR-02. |
| WR-04 no alarm or correlation for in-doubt | **Open, and more important** | Carried forward as WR-03. Every executor fault is now in-doubt, and none of them raises an alarm. |
| WR-05 contract looser than DB shape | **Open** | Carried forward as WR-04. |
| IN-01 event detail built by concatenation | Open | IN-01 |
| IN-02 timestamp precision on replay | Open | IN-02 |
| IN-03 example `result_ref` format | Open | IN-03 |
| IN-04 file-share mismatch | Open | IN-04 |
| IN-05 serial sweep blocks host start | **Resolved** | The sweep runs in the background after `ApplicationStarted`. |

The exactly-once core is unchanged and still holds. This review found no new Critical issues. The new defect is in how the deferred sweep handles cancellation and shutdown. It conflicts with the decide path's own rule (D-01: `CancellationToken.None` so that an interrupted attempt is never left in-doubt).

## Warnings

### WR-01: Deferred sweep runs attempts on `ApplicationStopping` and `StopAsync` does not drain it, so a routine shutdown leaves approved proposals that were never written marked as in-doubt failures

**File:** `src/Olive.Host/Startup/ExecutionSweep.cs:19,75,85`, `src/Olive.Application/ExecutionService.cs:47-54`, `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs:33,37`
**Issue:** The sweep is started with `lifetime.ApplicationStopping` as its token (line 19). That token reaches every `ExecuteAsync` call (line 75), and `ExecutionService` passes it to the executor. Since 73298d0 the sweep runs after the server is listening, so any shutdown during the sweep cancels it. Rolling deploys, an orchestrator restart shortly after start, and SIGTERM all qualify. Trace an attempt cancelled after `RecordStartedAsync` has committed (line 47):
1. `_gate.WaitAsync(ct)` or `fs.WriteAsync(line, ct)` throws `OperationCanceledException` before any byte is written.
2. The catch at line 54 is filtered by `when (!ct.IsCancellationRequested)`, so the OCE propagates.
3. A started-only row remains. On the next start, the D-08 branch (line 22-26) records `execution_in_doubt` and moves the proposal to `execution_failed`.

The result is a human-approved action that ends permanently failed without ever being attempted, and a ledger that reports "may have written" when nothing was written. The decide path avoids exactly this with `CancellationToken.None` (`QueueEndpoints.cs:171-185`, comment D-01). The sweep breaks that design rule.

`StopAsync` also returns `Task.CompletedTask` (line 85) without waiting for the background sweep. The host then disposes the service provider (including `NpgsqlDataSource`) while an attempt may be between the target write and `RecordFinishedAsync`. That attempt ends in-doubt even though a completed write could have been recorded as `succeeded`. Every remaining proposal in the loop also logs "Execution sweep attempt failed." at Error level on a normal shutdown, which is misleading.

No test covers shutdown during a sweep.
**Fix:** Use the stopping token only between attempts. Never cancel an attempt once it has started, and drain the sweep in `StopAsync`:
```csharp
private Task _sweep = Task.CompletedTask;

public Task StartAsync(CancellationToken _)
{
    lifetime.ApplicationStarted.Register(() => _sweep = Task.Run(() => SweepAsync(lifetime.ApplicationStopping)));
    return Task.CompletedTask;
}

// in RunAsync
foreach (var id in ids)
{
    if (stopping.IsCancellationRequested) { return; }
    try { await execution.ExecuteAsync(tenantId, id, CancellationToken.None); }   // same rule as the decide path (D-01)
    catch (Exception) { log.LogError(...); }
}

public async Task StopAsync(CancellationToken ct)
{
    try { await _sweep.WaitAsync(ct); } catch (OperationCanceledException) { }
}
```
Add a test that triggers `StopApplication()` while a blocking executor double is in the sweep. Assert that the proposal ends `executed`, or stays `approved` with zero ledger rows, and never ends `execution_in_doubt`.

### WR-02 (carried from prior WR-03): Lock timeout equals Npgsql's default command timeout, so `LockTimeout` is nondeterministic and decide can block for about 30 s

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:14`, `src/Olive.Persistence/TenantUnitOfWork.cs:41-43`, `src/Olive.Host/Program.cs:33`
**Issue:** Nothing has changed here. The `lockTimeout` default is 30 s, and Npgsql's default `CommandTimeout` is also 30 s and is not overridden. If the client-side timeout fires first, the lock statement throws `NpgsqlException` with an inner `TimeoutException`, not `55P03`. The catch at `TenantUnitOfWork.cs:46` then misses it, and the attempt surfaces as an exception instead of `ExecutionResult.LockTimeout`. Since the sweep now runs while traffic is live, contention between the sweep and the decide path on the same proposal is a normal event. The decide request awaits the lock inline with no cancellation.
**Fix:** Make `lock_timeout` clearly shorter than the command timeout, for example by passing `commandTimeout: (int)lockTimeout.TotalSeconds + 5` on the lock `CommandDefinition`. Use a short lock timeout (a few seconds) on the decide path.

### WR-03 (carried from prior WR-04, escalated): In-doubt outcomes and decide-path lock timeouts raise no alarm and leave no correlated trace

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:173-191`, `src/Olive.Host/Startup/ExecutionSweep.cs:71-81`, `src/Olive.Application/ExecutionService.cs:54-59`
**Issue:** After eff7af6, every executor exception is recorded as `execution_in_doubt`. An operator must reconcile each of these against the target by hand. Both callers discard the `ExecutionResult`, so `IOperationalAlarm` (registered at `Program.cs:59`) is never raised, and nothing is logged. A `LockTimeout` on the decide path is also dropped with no log. That proposal stays `approved` until the next process restart, because the sweep runs only once and has no timer. Thrown faults log a fixed string with no `proposal_id` or `tenant_id`. These GUIDs are neither secrets nor bodies, and D-09 does not forbid them as structured fields.
**Fix:** Inspect the result in both callers. On `Failed` with `Reason == ExecutionReasons.ExecutionInDoubt`, call `alarm.Raise(AlarmCodes.ExecutionInDoubt, "executor")`. On `LockTimeout`, log a warning. Add `proposal_id={ProposalId} tenant_id={TenantId}` as structured fields to the static messages.

### WR-04 (carried from prior WR-05): Execution-record v1 contract is looser than the `execution_ledger_shape` check

**File:** `contracts/execution-record/v1/execution-record.schema.json:7-17`, `sql/migrations/0009_execution_ledger.sql:18-21`
**Issue:** Nothing has changed here. The schema accepts `{"outcome":"succeeded"}` with no `result_ref`, `outcome: "failed"` with no `reason`, a `reason` alongside `succeeded`, and `outcome` without `finished_at`. The database cannot produce any of these shapes. `execution-record.invalid.json` tests only `execution_mode` and `additionalProperties`.
**Fix:** Add `dependentRequired` for `outcome` and `finished_at`. Add `allOf`/`if-then` rules for `succeeded` (requires `result_ref`, forbids `reason`) and for `failed` (requires `reason`). Add one invalid example per rule.

## Info

### IN-01: Transition event detail JSON built by string concatenation

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:128`
**Issue:** This is unchanged. It is safe only while every `reason` is a constant.
**Fix:** `JsonSerializer.Serialize(new { reason, execution_mode = "simulated" })`.

### IN-02: Returned `LedgerRow.RecordedAt` has 100 ns precision; replayed rows have microsecond precision

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:101`
**Fix:** Truncate `now` to microseconds before the insert, or use `returning recorded_at`.

### IN-03: Valid example `result_ref` does not match the emitted format

**File:** `contracts/execution-record/v1/execution-record.valid.json:1`
**Issue:** The example uses `"simulated://1"`, but the executor emits `"simulated:<uuid>"` (`SimulatedActionExecutor.cs:45`).

### IN-04: Startup probe and executor open the output file with different share modes

**File:** `src/Olive.Host/Startup/StartupGuards.cs:115`, `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs:36`
**Issue:** The probe uses `FileShare.ReadWrite` and the executor uses `FileShare.Read`. A second writer therefore makes every execution throw. Since eff7af6, each of those failures is recorded as `execution_in_doubt` even though nothing was written.
**Fix:** Use one share mode in both places, and document that the output path must have a single writer.

### IN-05: `ExecutionService` exposes a lifecycle-override seam on its public, DI-activated constructor

**File:** `src/Olive.Application/ExecutionService.cs:6-8`
**Issue:** `Func<string, string, bool>? canTransition` exists only for the WR-02 unit test. Because `ExecutionService` is DI-activated (`Program.cs:52`), any future `Func<string,string,bool>` registration in the container would be injected silently. It would then override the lifecycle table for the expiry decision. Meanwhile `PostgresExecutionStore.Transition` (line 107) still consults `ProposalStates.CanTransition` directly, so there are two sources of truth that can disagree. The failure mode is fail-closed, because the store throws and the transaction rolls back, but the seam is a hazard.
**Fix:** Make the seam an `internal` constructor with `InternalsVisibleTo` for the test project. Alternatively, inject a named `ILifecycleTable` abstraction that the store also uses.

### IN-06: `ExecutionSweep.Completion` signals "done" on shutdown while the sweep may still be running, and `OliveApiFactory.CreateHost` leaks the host when the wait times out

**File:** `src/Olive.Host/Startup/ExecutionSweep.cs:15,20`, `tests/Olive.Tests/OliveApiFactory.cs:125-134`
**Issue:** The `ApplicationStopping.Register(() => _completion.TrySetResult())` call completes `Completion` while an attempt may still be in flight, which contradicts the comment on line 14. In the factory, `Completion.WaitAsync(TimeSpan.FromSeconds(60))` throws `TimeoutException` after `base.CreateHost` has already started the host. `WebApplicationFactory` never receives that host, so it is never stopped or disposed. The started host and its sweep keep running against the test database and can affect later assertions.
**Fix:** Complete `Completion` only from `SweepAsync`'s `finally`, or from a "never started" path when host start fails. In `CreateHost`, wrap the wait in a try/catch that calls `host.StopAsync()` and `host.Dispose()` before rethrowing.

---

_Reviewed: 2026-10-24_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
