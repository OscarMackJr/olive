---
phase: 06-simulated-execution-and-audit-chain
verified: 2026-10-24T12:00:00Z
status: passed
score: 7/7 must-haves verified (5 roadmap SCs + IDN-08 + fail-closed startup constraint)
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 5/6
  gaps_closed:
    - "CR-01: ExecutionSweep ran before StartupGuardFilter guards (fail-closed breach)"
  gaps_remaining: []
  regressions: []
gaps: []
human_verification: []
---

# Phase 6: Simulated Execution and Audit Chain Verification Report

**Phase Goal:** An approved proposal executes exactly once in simulation, and the whole core-value chain can be queried from the execution record.
**Verified:** 2026-10-24
**Status:** passed
**Re-verification:** Yes, after gap closure plans 06-08 (CR-01) and 06-09 (WR-01/WR-02)

## CR-01 re-check (the previous gap)

- `src/Olive.Host/Startup/ExecutionSweep.cs` read directly. `StartAsync` now only registers a callback on `lifetime.ApplicationStarted` and returns `Task.CompletedTask`. It performs no execution inline. `ApplicationStarted` is raised only after `GenericWebHostService.StartAsync` has built the pipeline, which is where `StartupGuardFilter.Configure` runs every guard. A host whose guard throws never reaches `ApplicationStarted`, so the sweep never runs. The misleading comment was replaced with an accurate one.
- `Program.cs` lines 22 and 55-56: the guard filter and the sweep are still registered, and the ordering defect is removed by deferral, not by registration order.
- Regression test `EXE_04_failing_startup_guard_prevents_sweep_execution` (`tests/Olive.Tests/ExecutionSweepTests.cs:192`) approves a proposal, starts a second host with a refusing guard (`OLIVE_MIGRATOR_URL` present), and asserts state is still `approved`, zero `execution_ledger` rows, and an empty JSONL file. This is exactly the test the previous report said was missing.
- Targeted run `dotnet test --filter ExecutionSweep|StartupGuard|ExecutionService`: 77 tests. The first two runs: run 1 had 1 failure (`StartupGuardTests.QUE_02_host_refuses_start_on_role_drift`, "Host failed for a non-guard reason"), run 2 had 0 failures. Orchestrator's full post-merge run was 1209/1209. This looks like a flaky or environment-sensitive test (container or role state), unrelated to the sweep change. It is recorded as a warning below, not a blocker.

Result: CR-01 CLOSED.

## Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Post-approval simulated execution outside approval txn, `execution_mode=simulated` ledger + JSONL, no network write; restart sweep | VERIFIED | Executor, ExecutionService, ExecutionSweep, QueueEndpoints hook wired in Program.cs; sweep now post-guard |
| 2 | Duplicate/concurrent attempts yield one ledger row and one write; ledger INSERT-only for olive_app | VERIFIED (regression check) | Migration 0009 unique key and grants; concurrency and privilege tests green |
| 3 | Invalid catalog or expired proposal not executed; executor failure ends in `execution_failed` | VERIFIED | Expiry now unconditional (06-09 WR-02); executor exceptions recorded as `execution_in_doubt` and proposal moves to `execution_failed` (06-09 WR-01); tests green |
| 4 | One query on a `security_invoker` view returns the full chain | VERIFIED (regression check) | `execution_chain` view and ExecutionChainViewTests unchanged and green |
| 5 | Scripted beat 2 demo prints the chain | VERIFIED (regression check) | build/demo-beat2.sh, Beat2DemoTests |
| 6 | IDN-08 sentinel scan covers executor JSONL | VERIFIED (regression check) | Beat2DemoTests via SentinelScan |
| 7 | Fail-closed startup: a refusing host executes nothing | VERIFIED | CR-01 closed, regression test above |

**Score:** 7/7

## Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| EXE-01 | SATISFIED | Endpoint hook plus post-guard restart sweep |
| EXE-02 | SATISFIED | Unique (tenant, proposal, phase), advisory lock, replay returns first row |
| EXE-03 | SATISFIED | Unconditional expiry check, catalog re-check before the started row |
| EXE-04 | SATISFIED | Simulated mode only, no HttpClient, guards now precede any execution |
| EXE-05 | SATISFIED | Failures map to `execution_failed`, fault-injection tests |
| EXE-06 | SATISFIED | security_invoker execution_chain view |
| EVD-02 | SATISFIED | demo-beat2.sh and Beat2DemoTests |

All seven phase IDs are claimed by plans and mapped in REQUIREMENTS.md. No orphaned requirements.

## Review warnings weighed against must-haves (06-REVIEW.md: 0 critical, 4 warnings, 6 info)

None breaks a must-have. All are advisory follow-ups.

- **WR-01 (shutdown during sweep):** The sweep passes `ApplicationStopping` into attempts and `StopAsync` does not drain. If the host stops mid-sweep, an attempt can be cancelled after the started row, and the next start records `execution_in_doubt`. Exactly-once is preserved (no double write; a cancelled-before-write attempt is never repeated), and the proposal ends in a failed state with a ledger record, not silently lost. It is a conservative false positive that costs an approved action, so it is a real robustness defect worth a follow-up, but it does not violate SC1-SC5 or fail-closed startup. The review's suggested fix (CancellationToken.None per attempt, check stopping between attempts, drain in StopAsync) is cheap.
- **WR-02 (lock timeout equals command timeout):** latent nondeterminism under contention; fail-closed in effect.
- **WR-03 (no alarm for in-doubt / lock timeout):** observability gap, not a correctness gap for the chain; should be addressed before any real executor (Phase 1 is simulation-only).
- **WR-04 (contract looser than DB check):** DB constraint is the enforcing layer; contract tightening is follow-up.
- IN-01..IN-06: informational. IN-06 (test factory host leak on timeout) can affect test reliability and may relate to the one flaky run above.

## Anti-Patterns

No unreferenced TBD/FIXME/XXX markers were found by the review in the phase files; no blocker anti-pattern identified.

## Gaps Summary

No gaps. The CR-01 blocker is closed and verified in source and by a regression test. Recommend scheduling WR-01 (shutdown drain), WR-03 (in-doubt alarm), and investigating the intermittent `QUE_02_host_refuses_start_on_role_drift` failure as follow-up work before any real executor phase.

_Verified: 2026-10-24_
_Verifier: Claude (gsd-verifier)_
