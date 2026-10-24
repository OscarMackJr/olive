---
phase: 06-simulated-execution-and-audit-chain
verified: 2026-10-24T00:00:00Z
status: gaps_found
score: 5/6 must-haves verified (5 roadmap SCs + IDN-08; SC3 partial and the fail-closed constraint breached by CR-01)
gaps:
  - truth: "Service fails closed: a host that should refuse to start executes nothing (project constraint; EXE-04 no-write-on-unsafe-host; Phase 1 SC4 guards)"
    status: failed
    reason: "ExecutionSweep (IHostedService) starts before StartupGuardFilter (IStartupFilter) runs, so approved proposals can be executed (JSONL append, ledger rows, state change) on a host whose guards would refuse start (bad DB role, migration mismatch, stubs in production, missing output path)."
    artifacts:
      - path: "src/Olive.Host/Program.cs"
        issue: "AddHostedService<ExecutionSweep>() with guards only in IStartupFilter; no eager guard run before app.Run()"
      - path: "src/Olive.Host/Startup/ExecutionSweep.cs"
        issue: "StartAsync runs executions inline; comment claiming guards report first is wrong"
      - path: "src/Olive.Host/Startup/StartupGuardFilter.cs"
        issue: "Guards run in Configure(), which GenericWebHostService invokes after user hosted services start"
    missing:
      - "Run the sweep only after guards pass (e.g. defer to IHostApplicationLifetime.ApplicationStarted) or run the guards eagerly in Program.cs before app.Run()"
      - "Test: host with a failing guard and a pending approved proposal yields zero ledger rows, no JSONL line, state still approved"
human_verification: []
---

# Phase 6: Simulated Execution and Audit Chain Verification Report

**Phase Goal:** An approved proposal executes exactly once in simulation, and the whole core-value chain can be queried from the execution record.
**Status:** gaps_found
**Re-verification:** No, initial verification

## Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Post-approval simulated execution outside approval txn, `execution_mode=simulated` ledger + JSONL, no network write; restart sweep | VERIFIED (with CR-01 caveat) | SimulatedActionExecutor, ExecutionService, ExecutionSweep, QueueEndpoints hook exist and are wired in Program.cs; ExecutionEndpoint/Sweep tests pass. Sweep ordering defect below. |
| 2 | Duplicate/concurrent attempts yield one ledger row and one write; ledger INSERT-only for olive_app | VERIFIED | Migration 0009 grants only select, insert to olive_app; unique (tenant_id, proposal_id, phase); advisory lock on its own connection; concurrency and privilege-matrix tests pass. |
| 3 | Invalid catalog or expired proposal not executed; executor failure (incl. injected fault) ends in `execution_failed` | VERIFIED (warnings) | ExecutionService checks expiry then catalog before the started row; failures map to ExecutionFailed; ExecutionFailureTests pass. WR-01 and WR-02 reduce robustness but do not break the criterion (see below). |
| 4 | One query on a `security_invoker` view returns proposal, trace, policy, approver+rationale, execution | VERIFIED | `execution_chain` view with `security_invoker = true`, grants to olive_reader/olive_app; ExecutionChainViewTests pass. |
| 5 | Scripted beat 2 demo prints the chain | VERIFIED | build/demo-beat2.sh and Beat2DemoTests exist and pass. |
| 6 | IDN-08: sentinel search extended to executor JSONL | VERIFIED | Beat2DemoTests feeds the JSONL output through SentinelScan.AddSource. |
| 7 | Fail-closed startup (project constraint) holds with the new sweep | FAILED | CR-01, see below. |

## CR-01 independent assessment: REAL, classified as a gap

Program.cs registers `ExecutionSweep` as a hosted service and `StartupGuardFilter` as an `IStartupFilter`. Guards (migrator URL, vocabulary hash, catalog, non-production components, dev reviewer scheme, executor output path, DB-07 role/migration checks) all run inside `Configure()`. `Configure()` is invoked when `GenericWebHostService.StartAsync` builds the pipeline, and that service is registered after user hosted services, so `ExecutionSweep.StartAsync` runs first and awaits executions inline. The review's minimal .NET 10 repro (HOSTED, FILTER, STARTED) agrees with this reading of the code. I did not re-run it; I confirmed the registration order and the inline await in the source. The comment in ExecutionSweep.cs asserting guards come first is incorrect, and no test covers a failing guard with a pending approved proposal.

Impact: exactly-once and the simulated-only write are unaffected (no real credential, no network). But a host that must refuse to start (unsafe DB role bypassing RLS, tampered migrations, stub components in production, catalog the guard rejects) can first append JSONL and ledger rows. That contradicts "fails closed on any resolver or auth failure" and Phase 1 SC4. It does not by itself falsify the Phase 6 roadmap SC1-5 on a healthy host, but it is a fail-closed regression introduced by this phase, so status is gaps_found.

## Warnings weighed against SC3

- WR-01 (executor exception after write recorded as definite `executor_failed`): state still ends `execution_failed`, so SC3 and EXE-05 literally hold. The ledger reason can be inaccurate when the write may have occurred (flush/dispose throw). Warning, accuracy of the record, not a blocker. Recommended follow-up.
- WR-02 (expiry check conjoined with CanTransition): fail-open only if the lifecycle table changes; today `approved -> expired` is allowed so the guard is effective. Latent risk, warning. Cheap to fix.
- WR-03 to WR-05 (lock/command timeout parity, no alarm for in-doubt, contract looser than DB check): not blocking, tracked in 06-REVIEW.md.

## Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| EXE-01 | SATISFIED (CR-01 caveat on sweep ordering) | endpoint hook + ExecutionSweep |
| EXE-02 | SATISFIED | unique constraint, lock, replay returns first row, concurrency tests |
| EXE-03 | SATISFIED (WR-02 latent) | expiry and catalog re-check before started row |
| EXE-04 | SATISFIED for ledger/JSONL/no-network; guard ordering is CR-01 | execution_mode simulated; no HttpClient; output-path guard exists but runs after sweep |
| EXE-05 | SATISFIED (WR-01 caveat) | failure -> execution_failed, fault-injection tests |
| EXE-06 | SATISFIED | security_invoker execution_chain view |
| EVD-02 | SATISFIED | demo-beat2.sh + Beat2DemoTests |
| IDN-08 | SATISFIED | JSONL included in sentinel scan |

No orphaned requirements found: all IDs mapped to Phase 6 in REQUIREMENTS.md are claimed.

## Behavioral Spot-Checks

Targeted run `dotnet test --filter Execution|Beat2|StartupGuard`: 113 passed, 0 failed. (Last full run by executor: 1207 passed.) Tests are green but none exercise guard-failure plus pending approved proposal, which is why CR-01 is undetected.

## Anti-Patterns

No TBD/FIXME/XXX markers were specifically reviewed beyond the review report; the only blocker-level finding is CR-01.

## Gaps Summary

One blocker: reorder the sweep after the startup guards (or run guards eagerly) and add a guard-refuses-so-no-execution test. Recommended to fold WR-01 and WR-02 into the same gap-closure plan. No human verification items.

_Verifier: Claude (gsd-verifier)_
