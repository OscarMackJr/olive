---
phase: 06-simulated-execution-and-audit-chain
reviewed: 2026-10-24T00:00:00Z
depth: standard
files_reviewed: 50
files_reviewed_list:
  - .env.example
  - .gitignore
  - build/demo-beat2.sh
  - contracts/execution-record/v1/execution-record.schema.json
  - contracts/execution-record/v1/execution-record.valid.json
  - contracts/execution-record/v1/execution-record.invalid.json
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
  critical: 1
  warning: 5
  info: 5
  total: 11
status: issues_found
---

# Phase 6: Code Review Report

**Reviewed:** 2026-10-24
**Depth:** standard
**Files Reviewed:** 50
**Status:** issues_found

## Narrative Findings (AI reviewer)

## Summary

The exactly-once core holds up under inspection. The advisory lock sits on its own connection, the `started` row is committed before the target write, `unique (tenant_id, proposal_id, phase)` backs up the lock, and a started-only shape is resolved as `execution_in_doubt` with no second write. The `finished` row, the state compare-and-set and the `proposal_event` share one transaction. Tenant isolation on `execution_ledger` (forced RLS plus app predicate) and on `execution_chain` (`security_invoker = true`, with base-table grants present for `olive_reader`) is correct. Failure reasons are static constants, and the sweep and endpoint log lines are static.

The serious defect is ordering. ASP.NET Core starts `IHostedService` instances **before** the request pipeline is built, so `ExecutionSweep` performs real executions (JSONL append and ledger and state writes) before `StartupGuardFilter` runs any fail-closed guard. I confirmed this with a minimal .NET 10 `WebApplication`: output order was `HOSTED`, `FILTER`, `STARTED`. The remaining warnings concern failure classification, a fail-open expiry conjunction, an equal lock/command timeout race, missing operational alarms for in-doubt outcomes, and a contract that is looser than the DB shape check.

## Critical Issues

### CR-01: Startup execution sweep runs before every startup guard, so fail-closed startup is bypassed

**File:** `src/Olive.Host/Program.cs:22,54`, `src/Olive.Host/Startup/ExecutionSweep.cs:9-54`, `src/Olive.Host/Startup/StartupGuardFilter.cs:260-274`
**Issue:** `StartupGuardFilter` is an `IStartupFilter`. It runs only when `GenericWebHostService.StartAsync` builds the pipeline. `WebApplicationBuilder` registers `GenericWebHostService` last, so user hosted services start first. That means `ExecutionSweep.StartAsync` lists approved proposals and calls `ExecutionService.ExecuteAsync` (executor write, ledger insert, state transition) **before** any of these guards run:
- D-19, migrator URL present
- FND-03, vendored vocabulary hash
- FND-05, non-production components in production
- Q17, dev reviewer scheme in production
- EXE-04, executor output path
- DB-07, database role and migration safety, e.g. a superuser or owner role that bypasses forced RLS, or unapplied or checksum-mismatched migrations

A host that is about to refuse start can therefore still execute approved proposals. For example, it can execute with an unsafe DB role, against a tampered migration set, or with a catalog that the guard would reject (`CatalogSet` is resolved lazily inside the sweep). The comment in `ExecutionSweep.cs:11-12` ("the startup guards keep reporting their own refusals first") assumes the opposite ordering and is wrong. No test covers "guard refuses, so no sweep execution". `ExecutionSweepTests` only exercises healthy hosts.
Repro (verified with a minimal .NET 10 app that registers an `IStartupFilter` and an `AddHostedService`): output order was `HOSTED`, `FILTER`, `STARTED`.
**Fix:** Make the sweep run only after the guards pass. Two options:
```csharp
// Option A: run the sweep after the server has started (guards have passed by then).
public sealed class ExecutionSweep(IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => SweepAsync(lifetime.ApplicationStopping)));
        return Task.CompletedTask;
    }
    ...
}
```
Option B: run the guards eagerly in `Program.cs` right after `builder.Build()` and before `app.Run()`, and keep the filter only as a backstop. Also add a test that boots a host with a failing guard (for example a missing `OLIVE_EXECUTOR_OUTPUT_PATH` alternative, or a DB-07 violation) while an approved proposal is pending. It should assert zero ledger rows and an unchanged `approved` state.

## Warnings

### WR-01: Executor exception is recorded as a definite failure even when the target write may have happened

**File:** `src/Olive.Application/ExecutionService.cs:49-53`, `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs:333-335`
**Issue:** Any non-cancellation exception from the executor is recorded as `outcome=failed, reason=executor_failed`. `SimulatedActionExecutor` can throw after `fs.WriteAsync` succeeded, for example from `fs.Flush(flushToDisk: true)` or from the `FileStream` dispose flush. In that case the JSONL line exists while the ledger asserts a clean failure. For the real executor this seam is designed for, a timeout or transport exception is the classic "may have written" case. The core value requires the execution record to prove what happened. Recording "failed" for an ambiguous outcome makes the record contradict the target. The same applies to cancellation in the opposite direction: `OperationCanceledException` from `_gate.WaitAsync(ct)` (nothing written) leaves a started-only row that later becomes `execution_in_doubt`. That is conservative but mislabelled.
**Fix:** Separate "definitely not attempted" from "outcome unknown". Validate and serialize before the started row (parse `ParametersJson` up front, which already happens before any I/O). Treat any exception raised after the executor has begun I/O as `execution_in_doubt`. Alternatively, let `IActionExecutor` return a typed result (`Succeeded`, `RejectedBeforeWrite`, `Unknown`) instead of relying on exceptions:
```csharp
catch (Exception) when (!ct.IsCancellationRequested)
{
    var failed = await attempt.RecordFinishedAsync(new FinishedOutcome(false, ExecutionReasons.ExecutionInDoubt, null), ...);
```

### WR-02: Expiry check is fail-open on the lifecycle table

**File:** `src/Olive.Application/ExecutionService.cs:31`
**Issue:** `if (snap.ExpiresAt <= now && ProposalStates.CanTransition(snap.State, ProposalStates.Expired))` means that when the lifecycle table does not permit `approved -> expired`, an expired proposal falls through and **is executed**. Today the table permits it (`ProposalStates.cs:23`), so the guard is dead. However, a future edit to the lifecycle table would silently turn expired approvals into executions. Expiry must not depend on whether the state can be recorded.
**Fix:**
```csharp
if (snap.ExpiresAt <= now)
{
    var to = ProposalStates.CanTransition(snap.State, ProposalStates.Expired) ? ProposalStates.Expired : ProposalStates.ExecutionFailed;
    return new ExecutionResult.Failed(await attempt.RecordRefusalAsync(ExecutionReasons.ProposalExpired, to, now, ct));
}
```

### WR-03: Lock timeout equals Npgsql's default command timeout, so `LockTimeout` is nondeterministic and the decide request can block for 30 s

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:14`, `src/Olive.Persistence/TenantUnitOfWork.cs:173-182`, `src/Olive.Host/Endpoints/QueueEndpoints.cs:150,185`
**Issue:** The default `lockTimeout` is 30 s and `set local lock_timeout` uses that value. Npgsql's default `CommandTimeout` is also 30 s, and `Program.cs:33` does not override it. Whichever fires first wins. If the client-side command timeout fires, Npgsql throws `NpgsqlException` (inner `TimeoutException`), not `PostgresException 55P03`. The attempt then surfaces as an unhandled exception instead of `ExecutionResult.LockTimeout`, and the connection is broken. Separately, `DecisionAsync` awaits execution inline with `CancellationToken.None`. A reviewer's decide call can therefore hang for up to about 30 s plus executor time on lock contention (for example a reviewer retry that races the first request), and the client cannot cancel it.
**Fix:** Keep `lock_timeout` well below the command timeout. For example, set an explicit `CommandTimeout` on the lock command (`new CommandDefinition(lockSql, parameters, tx, commandTimeout: (int)lockTimeout.TotalSeconds + 5, ...)`), or default `lockTimeout` to a few seconds for the request path. Consider a short lock timeout on the decide path and leave stragglers to the sweep.

### WR-04: In-doubt and failed executions raise no operational alarm, and fault logs carry no correlation

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:183-190`, `src/Olive.Host/Startup/ExecutionSweep.cs:36-51`
**Issue:** `ExecutionResult.Failed` with `execution_in_doubt` is the one outcome an operator must reconcile manually against the target system. Both callers discard the result. There is no `IOperationalAlarm` call (one is already registered in `Program.cs:57`) and no log line. Thrown faults log a fixed string with no proposal or tenant identifier, so an operator cannot tell which proposal is stranded in `approved` or ended in-doubt. Proposal and tenant GUIDs are not secrets and are not bodies, so static-text logging (D-09) does not preclude them.
**Fix:** Inspect the result. On `Failed` with reason `execution_in_doubt`, raise the operational alarm. Include `proposal_id` and `tenant_id` as structured log fields in the static messages:
```csharp
log.LogError("Execution sweep attempt failed. proposal_id={ProposalId} tenant_id={TenantId}", id, tenantId);
```

### WR-05: Execution-record v1 contract is looser than the ledger shape constraint

**File:** `contracts/execution-record/v1/execution-record.schema.json:7-17`
**Issue:** `sql/migrations/0009_execution_ledger.sql:18-21` requires the following: `succeeded` implies `result_ref` and no `reason`; `failed` implies `reason`; `outcome` implies a finished record. The schema has none of these conditionals. It accepts `{"outcome":"succeeded"}` with no `result_ref`, `{"outcome":"failed"}` with no `reason`, and `reason` with `outcome: "succeeded"`. It also accepts `outcome` without `finished_at` and vice versa. External consumers validating against the contract can accept records the database could never produce, so the published contract does not encode the audit chain's invariants. Note also that `format: "uuid"` and `format: "date-time"` are annotation-only in draft 2020-12 unless format assertion is enabled.
**Fix:** Add conditionals mirroring the DB check, plus an invalid example per rule:
```json
"dependentRequired": { "outcome": ["finished_at"], "finished_at": ["outcome"] },
"allOf": [
  { "if": { "properties": { "outcome": { "const": "succeeded" } }, "required": ["outcome"] },
    "then": { "required": ["result_ref"], "not": { "required": ["reason"] } } },
  { "if": { "properties": { "outcome": { "const": "failed" } }, "required": ["outcome"] },
    "then": { "required": ["reason"] } }
]
```

## Info

### IN-01: Transition event detail JSON built by string concatenation

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:128`
**Issue:** `"{\"reason\":\"" + reason + "\",...}"` is safe today only because every `reason` is a constant from `ExecutionReasons` or `"executed"`. A future non-constant reason would produce malformed JSON or allow injection into the event detail.
**Fix:** Serialize with `JsonSerializer.Serialize(new { reason, execution_mode = "simulated" })`.

### IN-02: Ledger row timestamps differ between first execution and replay

**File:** `src/Olive.Persistence/PostgresExecutionStore.cs:101`
**Issue:** `InsertLedger` returns `now` at 100 ns precision, while `LoadAsync` returns the DB value truncated to microseconds. As a result, `Executed.Row.RecordedAt != Replayed.Row.RecordedAt` for the same row. It is not surfaced over HTTP today, but any future API returning the row would be non-idempotent.
**Fix:** Truncate `now` to microseconds before insert and return the truncated value, or use `insert ... returning recorded_at`.

### IN-03: Valid example `result_ref` does not match the real format

**File:** `contracts/execution-record/v1/execution-record.valid.json:1`
**Issue:** The example uses `"simulated://1"`, but the executor emits `"simulated:<uuid>"` (`SimulatedActionExecutor.cs:342`).
**Fix:** Use `"simulated:22222222-2222-2222-2222-222222222222"`.

### IN-04: Inconsistent file sharing between the startup probe and the executor

**File:** `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs:333`, `src/Olive.Host/Startup/StartupGuards.cs:172`
**Issue:** The guard probes with `FileShare.ReadWrite`, while the executor opens with `FileShare.Read`. A second process appending to the same path, such as a second host instance or a log shipper opening for write, makes every execution throw `IOException`, and each one is recorded as `executor_failed` (see WR-01). The probe does not detect this.
**Fix:** Use the same share mode in both places, and document a single-writer requirement for the output path.

### IN-05: Startup sweep is serial and blocks host start

**File:** `src/Olive.Host/Startup/ExecutionSweep.cs:29-53`
**Issue:** Each attempt can wait up to the 30 s lock timeout, and `StartAsync` blocks host start until every tenant and proposal is processed. With many approved proposals, or a peer instance holding locks, startup time grows without bound. This compounds CR-01, because the sweep runs before the server listens.
**Fix:** Once CR-01 is fixed, run the sweep in the background after `ApplicationStarted`, with a short per-attempt lock timeout.

---

_Reviewed: 2026-10-24_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
