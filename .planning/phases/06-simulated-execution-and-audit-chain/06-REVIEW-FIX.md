---
phase: 06-simulated-execution-and-audit-chain
fixed_at: 2026-10-24T13:07:34Z
review_path: .planning/phases/06-simulated-execution-and-audit-chain/06-REVIEW.md
iteration: 1
findings_in_scope: 4
fixed: 3
skipped: 1
status: partial
---

# Phase 6: Code Review Fix Report

**Fixed at:** 2026-10-24T13:07:34Z
**Source review:** .planning/phases/06-simulated-execution-and-audit-chain/06-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 4 (fix_scope: critical_warning; 0 critical, 4 warnings)
- Fixed: 3
- Skipped: 1
- Full suite after fixes: `dotnet test tests/Olive.Tests` reported 1216 passed, 0 failed, 0 skipped, 1216 total (one run, 41 s). `StartupGuardTests.QUE_02_host_refuses_start_on_role_drift` passed in that run.

## Fixed Issues

### WR-01: Deferred sweep cancels started attempts on shutdown and StopAsync does not drain it

**Files modified:** `src/Olive.Host/Startup/ExecutionSweep.cs`, `tests/Olive.Tests/ExecutionSweepTests.cs`
**Commit:** 70efcc5
**Applied fix:** The sweep checks `ApplicationStopping` only between tenants and between attempts. Each `ExecuteAsync` now runs with `CancellationToken.None`, which is the D-01 rule the decide path already follows. The sweep task is stored, and `StopAsync` waits for it, bounded by the host shutdown token, before the provider and `NpgsqlDataSource` are disposed. As a small related change, `ApplicationStopping` now completes `Completion` only if the sweep has not started or has already finished. The sweep's own `finally` and `StopAsync` complete it in every other case.
**Test:** `EXE_05_shutdown_during_sweep_attempt_drains_and_never_leaves_in_doubt`. The test blocks the sweep inside the target write with `GateExecutor`, calls `StopApplication()`, starts disposing the factory, and checks that disposal is still waiting. It then releases the gate. The proposal ends `executed` with one `succeeded` finished row and no `execution_in_doubt` row. I ran this test against the old code and it failed (`Assert.False` on disposal completing early), so it detects the defect.

### WR-02: Lock timeout equals Npgsql's default command timeout

**Files modified:** `src/Olive.Persistence/TenantUnitOfWork.cs`, `src/Olive.Persistence/PostgresExecutionStore.cs`, `tests/Olive.Tests/TenantUnitOfWorkTests.cs`
**Commit:** 3e0d0cd
**Applied fix:** The lock statement now runs with `commandTimeout = ceil(lock_timeout seconds) + 5` (`LockCommandTimeoutMarginSeconds`). Postgres's `lock_timeout` (55P03) therefore always fires before the client-side timeout. The default execution lock timeout is now `PostgresExecutionStore.DefaultLockTimeout`, which drops from 30 s to 5 s. The store is one singleton shared by the decide path and the sweep, so the shorter default covers the decide path without a second store. A short wait is safe because a waiter only times out while another attempt holds the lock, and that holder finishes the proposal.
**Test:** `EXE_02_lock_timeout_outcome_is_deterministic_when_command_timeout_is_shorter`. The waiter's data source uses `Command Timeout=1` with a 2 s lock timeout, and the test asserts `null` (LockTimeout). Against the old code it failed with `NpgsqlException` / inner `TimeoutException`, which is the failure the review described.
**Note for human review:** 5 s as the default is a judgement call that the review suggested ("a few seconds"). It is not configurable yet.

### WR-03: In-doubt outcomes and decide-path lock timeouts raise no alarm and leave no correlated trace

**Files modified:** `src/Olive.Application/IOperationalAlarm.cs`, `src/Olive.Application/ExecutionService.cs`, `src/Olive.Host/Endpoints/QueueEndpoints.cs`, `src/Olive.Host/Startup/ExecutionSweep.cs`, `tests/Olive.Tests/ExecutionServiceTests.cs`, `tests/Olive.Tests/ExecutionConcurrencyTests.cs`, `tests/Olive.Tests/ExecutionFailureTests.cs`, `tests/Olive.Tests/ExecutionEndpointTests.cs`
**Commit:** 9892e76
**Applied fix:** This reuses the existing Phase 3 alarm pattern: `IOperationalAlarm` injected into an Application service, which raises a stable code and component only. I added `AlarmCodes.ExecutionInDoubt = "execution_in_doubt"`. `ExecutionService` takes `IOperationalAlarm` and raises it with component `executor` on both in-doubt paths: the D-08 started-without-finished resolution and the executor-exception path. Raising it inside the service, rather than in each caller, covers the decide path, the sweep and direct callers in one place. The decide path and the sweep now inspect the result and log a Warning on `LockTimeout`. Their static error messages now carry `proposal_id` and `tenant_id` as structured fields. Exception text is still never logged (D-09).
**Tests:** `EXE_05_started_without_finished_raises_execution_in_doubt_alarm`, `EXE_05_executor_exception_raises_execution_in_doubt_alarm`, and `EXE_03_definite_refusals_raise_no_alarm` (expired and catalog-invalid cases). The clean-success test now also asserts that no alarm fires. The integration tests `EXE_05_fault_between_ledger_write_and_state_update_resolves_in_doubt` and `EXE_05_executor_exception_is_terminal_execution_failed_with_static_reason` now assert the alarm. The new `EXE_02_decide_path_lock_timeout_logs_warning_with_proposal_and_tenant` holds the advisory lock while approving and asserts the warning and the proposal id appear in the logs.
**Note for human review:** the review did not mention two scope points I chose here. The alarm also fires on the D-08 restart resolution, not only on executor exceptions. A lock timeout logs a warning but raises no alarm, because a timeout means another attempt holds the lock and will finish the proposal.

## Skipped Issues

### WR-04: Execution-record v1 contract is looser than the `execution_ledger_shape` check

**File:** `contracts/execution-record/v1/execution-record.schema.json:7-17`
**Reason:** Skipped because the fix is a breaking v1 contract change. The proposed fix adds `dependentRequired` and `if`/`then` rules: `succeeded` would require `result_ref` and forbid `reason`, `failed` would require `reason`, and `outcome` would require `finished_at`. Each rule narrows the schema, so documents that validate against the published v1 schema today would start failing. The project constraint allows only additive, optional changes within `v1` (CLAUDE.md "Contracts"; Decision v0.5 line 104). A narrowing rule is neither. Any consumer validating with the current schema would see a breaking change. Two options exist. Ship the stricter shape as `contracts/execution-record/v2/`, or get explicit agreement that v1 has no external consumers yet and can be corrected in place. Either needs a decision from the owners, so it should not be applied automatically. The database `execution_ledger_shape` check still enforces the strict shape on everything Olive writes, so the gap is in documentation and consumer validation only and does not affect ledger integrity.
**Original issue:** The schema accepts `{"outcome":"succeeded"}` with no `result_ref`, `outcome: "failed"` with no `reason`, a `reason` alongside `succeeded`, and `outcome` without `finished_at`. The database cannot produce any of these shapes. `execution-record.invalid.json` tests only `execution_mode` and `additionalProperties`.

---

_Fixed: 2026-10-24T13:07:34Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
