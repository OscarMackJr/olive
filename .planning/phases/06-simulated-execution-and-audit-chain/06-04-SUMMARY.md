---
phase: 06-simulated-execution-and-audit-chain
plan: 04
subsystem: execution
tags: [fault-injection, concurrency, in-doubt, postgres]
requires:
  - phase: 06-03
    provides: PostgresExecutionStore, post-commit execution hook
provides:
  - Test-only faulting doubles for the execution path (FaultingExecutionStore, GateExecutor, ThrowingExecutor, CountingExecutor, SwitchableCatalogCheck, Decorate helper)
  - ExecutionConcurrencyTests (3) and ExecutionFailureTests (6)
affects: [06-05, 06-06, 06-07]
tech-stack:
  added: []
  patterns: [singleton decoration through configureServices, one-shot fault mode]
key-files:
  created:
    - tests/Olive.Tests/ExecutionConcurrencyTests.cs
    - tests/Olive.Tests/ExecutionFailureTests.cs
  modified:
    - tests/Olive.Tests/FaultingDoubles.cs
key-decisions:
  - "Doubles live only in tests; ServiceDecoration.Decorate wraps the registered singleton so production registration is untouched"
requirements-completed: [EXE-02, EXE-03, EXE-05]
duration: 25min
completed: 2026-10-24
---

# Phase 6 Plan 04: Execution fault and concurrency proofs Summary

Exactly-once, refusal and in-doubt behaviour are now demonstrated against Postgres: an 8-way race yields one Executed, seven Replayed, one started and one finished row, one JSONL line and no in-doubt row, and a fault injected before the finished row resolves to execution_failed on the next attempt without a second target write.

## Tasks

1. Faulting doubles (store decorator with ThrowOnBegin/ThrowOnStarted/ThrowOnFinish and ThrowOnce, gate and throwing executors, switchable catalog check): 4f9592e
2. Concurrency tests (8-way race, 1s lock-timeout waiter returns LockTimeout, unique phase key 23505): cfd0635
3. Failure tests (200 on fault, proposal_expired, catalog_invalid, expiry before catalog, execution_in_doubt, executor_failed; sentinel absent from rows, events, logs, responses): 7ec8293

## Deviations from Plan

None. The new tests passed against the existing ExecutionService and PostgresExecutionStore, so no source changes were needed. A CountingExecutor double was added beyond the plan list to assert executor call counts.

## Known Stubs

None.

## Threat Flags

None.

## Verification

Full `dotnet test tests/Olive.Tests`: 1189 passed, 0 failed. One earlier full run saw two transient failures (QUE_08 repeat and FND_04 host start) that passed in isolation and on rerun; this matches the host-start flake noted in Plans 02 and 03 and is unrelated to this plan.

## Self-Check: PASSED
