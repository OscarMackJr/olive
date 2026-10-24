---
phase: 06-simulated-execution-and-audit-chain
plan: 03
subsystem: execution
tags: [postgres, advisory-lock, startup-guard, endpoint-hook]
requires:
  - phase: 06-01
    provides: execution_ledger table
  - phase: 06-02
    provides: ExecutionService, ports, SimulatedActionExecutor
provides:
  - PostgresExecutionStore with lock-holder transaction (TenantUnitOfWork.AcquireAdvisoryLockAsync)
  - Host registration of the executor stack, CheckExecutorOutput guard, execution_mode=simulated startup log
  - Post-commit execution hook in the decision endpoint
affects: [06-04, 06-05, 06-06, 06-07]
tech-stack:
  added: []
  patterns: [lock held on dedicated connection A, writes on short connection B transactions]
key-files:
  created:
    - src/Olive.Persistence/ExecutionSql.cs
    - src/Olive.Persistence/PostgresExecutionStore.cs
    - tests/Olive.Tests/ExecutionEndpointTests.cs
  modified:
    - src/Olive.Persistence/TenantUnitOfWork.cs
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Startup/StartupGuards.cs
    - src/Olive.Host/Startup/StartupGuardFilter.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/TenantUnitOfWorkTests.cs
    - tests/Olive.Tests/StartupGuardTests.cs
    - tests/Olive.Tests/QueueDecisionTests.cs
    - tests/Olive.Tests/SelfApprovalTests.cs
    - .env.example
    - .gitignore
key-decisions:
  - "Assumption A1 holds: set local lock_timeout covers pg_advisory_xact_lock waits (SQLSTATE 55P03 mapped to null); no poll-loop fallback needed"
  - "Startup log line emitted from StartupGuardFilter.Configure, not after Build(), so it also runs under WebApplicationFactory"
  - "Executor path is read lazily in the IActionExecutor factory (message names the setting only); CheckExecutorOutput validates eagerly at startup"
requirements-completed: [EXE-01, EXE-02, EXE-04]
duration: 40min
completed: 2026-10-24
---

# Phase 6 Plan 03: Execution wiring Summary

Approving through the API now executes the proposal in simulation after the approve commit: state becomes executed, the ledger holds one started and one finished row, and the JSONL file gets one line. A per-proposal advisory lock held on its own connection makes repeats replay instead of re-writing.

## Tasks

1. ExecutionSql, PostgresExecutionStore, lock-holder transaction and EXE_02 lock tests: 22db1de
2. Host registration, CheckExecutorOutput guard, factory temp path, .env.example, guard tests: eea10be
3. Post-commit hook in DecisionAsync and ExecutionEndpointTests: 9256bf1

## Deviations from Plan

**1. [Rule 1 - Bug] Phase 5 tests asserted the old post-approve state**
- **Found during:** Task 3
- **Issue:** QUE_07, APR_04 and APR_02 expected proposal state `approved` after an approve; with execution wired it is now `executed`.
- **Fix:** Updated the three assertions to `executed`.
- **Files modified:** tests/Olive.Tests/QueueDecisionTests.cs, tests/Olive.Tests/SelfApprovalTests.cs
- **Commit:** 9256bf1

**2. Startup log placement.** The plan said to log after the host is built; it is emitted in StartupGuardFilter.Configure because code after `Build()` does not run under WebApplicationFactory.

QueueService.cs is unchanged. No DTO or proposal response gained a field.

## Known Stubs

None.

## Threat Flags

None.

## Verification

Full `dotnet test`: 1180 passed, 0 failed. The FND_05 flaky host-start failure noted in Plan 02 did not recur.

## Self-Check: PASSED
