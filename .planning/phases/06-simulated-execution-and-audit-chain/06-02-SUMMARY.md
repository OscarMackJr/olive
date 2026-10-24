---
phase: 06-simulated-execution-and-audit-chain
plan: 02
subsystem: execution
tags: [executor, jsonl, catalog, ports]
requires:
  - phase: 06-01
    provides: execution_ledger table and chain view
provides:
  - IActionExecutor, IActionCatalogCheck, IExecutionStore/IExecutionAttempt ports
  - ExecutionService decision table and ExecutionResult union
  - SimulatedActionExecutor (fsynced allow-listed JSONL appender)
  - CatalogExecutionCheck over CatalogSet
affects: [06-03, 06-04, 06-05, 06-06, 06-07]
tech-stack:
  added: []
  patterns: [result-union orchestrator over locked attempt port, allow-list JSON writer]
key-files:
  created:
    - src/Olive.Application/ExecutionReasons.cs
    - src/Olive.Application/IActionExecutor.cs
    - src/Olive.Application/IActionCatalogCheck.cs
    - src/Olive.Application/IExecutionStore.cs
    - src/Olive.Application/ExecutionService.cs
    - src/Olive.Executor.Simulated/SimulatedActionExecutor.cs
    - src/Olive.Executor.Simulated/SimulatedExecutorOptions.cs
    - src/Olive.Catalog/CatalogExecutionCheck.cs
    - tests/Olive.Tests/ExecutionServiceTests.cs
    - tests/Olive.Tests/SimulatedActionExecutorTests.cs
    - tests/Olive.Tests/CatalogExecutionCheckTests.cs
  modified: []
key-decisions:
  - "Expiry is checked before the catalog (D-05/D-06 do not order them)"
  - "Executor exceptions are caught only when cancellation was not requested; the message is never read"
  - "Replayed/in-doubt detection runs before the approved-state check so ledger rows win over proposal state"
requirements-completed: [EXE-01, EXE-02, EXE-03, EXE-04, EXE-05]
duration: 20min
completed: 2026-10-24
---

# Phase 6 Plan 02: Execution core Summary

ExecutionService implements the full execution decision table (replay, in-doubt, expiry, catalog, started/executor/finished ordering) behind Application ports, with a fsynced allow-listed JSONL simulated executor and a catalog re-check, all proven without a database.

## Tasks

1. Ports, reasons, ExecutionService and 14 classification tests: 20ea514
2. SimulatedActionExecutor JSONL appender with tests: fbcf9a0
3. CatalogExecutionCheck with tests: 9f7bbaa

## Deviations from Plan

None. The plan executed as written. In the test fake, the catalog-check class exposes its logic via an explicit interface implementation because a member cannot share its enclosing type name.

## Known Stubs

None.

## Threat Flags

None.

## Verification

Full `dotnet test tests/Olive.Tests`: 1165 passed, 0 failed. One earlier filtered run saw a transient `StartupGuardTests.FND_05_production_stub_behind_decorator_refuses` host-start failure under parallelism; it passed on rerun and in the full suite (unrelated to this plan).

## Self-Check: PASSED
