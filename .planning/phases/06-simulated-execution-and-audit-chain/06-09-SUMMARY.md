---
phase: 06-simulated-execution-and-audit-chain
plan: 09
subsystem: execution
tags: [gap-closure, WR-01, WR-02]
requires: []
provides: [fail-closed expiry, execution_in_doubt on executor exception]
key-files:
  modified:
    - src/Olive.Application/ExecutionService.cs
    - src/Olive.Application/ExecutionReasons.cs
    - tests/Olive.Tests/ExecutionServiceTests.cs
    - tests/Olive.Tests/ExecutionFailureTests.cs
requirements-completed: [EXE-03, EXE-05]
completed: 2026-10-24
---

# Phase 6 Plan 09: WR-01 and WR-02 gap closure Summary

Expiry now refuses independently of the lifecycle table (falls back to execution_failed), and an executor exception after the started row is recorded as execution_in_doubt.

## Changes
- ExecutionService takes an optional `canTransition` seam (defaults to `ProposalStates.CanTransition`; single public constructor, DI unchanged). The expiry guard is `snap.ExpiresAt <= now` only.
- The catch around `executor.ExecuteAsync` records `ExecutionReasons.ExecutionInDoubt`, with a WR-01 comment.
- `ExecutorFailed` was referenced nowhere else (no contract, SQL or spec enumerated `executor_failed`), so the constant was removed.
- Tests: new `EXE_03_expiry_falls_back_to_execution_failed_when_expired_transition_disallowed`; EXE_05 test renamed and asserts in-doubt; ExecutionFailureTests expects `execution_in_doubt`.

## Deviations
- Both tasks were committed in one commit because they share ExecutionService.cs and ExecutionServiceTests.cs.
- After the worktree reset, `contracts/vendored/*` showed line-ending-only diffs; restored with `git checkout` and not committed.

## Verification
Full `dotnet test tests/Olive.Tests`: 1208 passed, 0 failed.

## Self-Check: PASSED
