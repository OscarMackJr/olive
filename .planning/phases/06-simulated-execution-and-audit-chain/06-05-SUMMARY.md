---
phase: 06-simulated-execution-and-audit-chain
plan: 05
subsystem: execution
tags: [hosted-service, startup-sweep, restart, in-doubt]
requires:
  - phase: 06-03
    provides: PostgresExecutionStore, ExecutionService wiring
  - phase: 06-04
    provides: FaultingExecutionStore doubles
provides:
  - IReviewerTenantMap.AllTenants
  - ExecutionSweep startup hosted service
  - ExecutionSweepTests (7)
affects: [06-06, 06-07]
tech-stack:
  added: []
  patterns: [hosted service resolves dependencies lazily inside StartAsync]
key-files:
  created:
    - src/Olive.Host/Startup/ExecutionSweep.cs
    - tests/Olive.Tests/ExecutionSweepTests.cs
  modified:
    - src/Olive.Application/IReviewerTenantMap.cs
    - src/Olive.Catalog/ReviewerTenantMap.cs
    - src/Olive.Host/Program.cs
    - tests/Olive.Tests/ReviewerTenantMapTests.cs
key-decisions:
  - "ExecutionSweep takes IServiceProvider and resolves its dependencies inside StartAsync; constructor injection made the host fail before the startup guards (API-10, DB-07) could report their own refusals"
  - "Beta requester identity registered in test code only (Identity() helper); no contract fixture added"
requirements-completed: [EXE-01, EXE-03, EXE-05]
duration: 35min
completed: 2026-10-24
---

# Phase 6 Plan 05: Startup execution sweep Summary

At host start, every approved proposal without a finished ledger row is run through the same ExecutionService and advisory lock, per tenant from the source-controlled map, once and with no timer. A restart resolves crashed attempts as execution_in_doubt without a second target write.

## Tasks

1. AllTenants on IReviewerTenantMap and ReviewerTenantMap, ExecutionSweep, AddHostedService registration, map test: see commit "feat(06-05)"
2. ExecutionSweepTests (plain both tenants, non-approved skipped, startup only, in-doubt, removed catalog entry, expiry, fault does not stop start): see commit "test(06-05)"

## Deviations from Plan

**1. [Rule 1 - Bug] Sweep constructor injection broke startup guard tests**
- **Found during:** Task 2 full-suite run (API_10 x2, DB_07 failed)
- **Issue:** Hosted services are constructed before guards run, so unresolvable services (bad catalog, missing DB url) threw before the guard refusals.
- **Fix:** ExecutionSweep resolves dependencies lazily in StartAsync inside try/catch with a static log.
- **Files modified:** src/Olive.Host/Startup/ExecutionSweep.cs

**2. Test-only beta identity.** The identity fixtures hold no beta requester, so ExecutionSweepTests registers one on a FixtureUserIdentityResolver instance passed through the factory.

## Known Stubs

None.

## Threat Flags

None.

## Verification

Full `dotnet test`: 1197 passed, 0 failed. Host-start flakiness (FND_04, FND_05, QUE_08): did not recur in the final run. The sweep never throws out of StartAsync, so it should not add to it; the character of the flakiness is unchanged as far as observed. No SECURITY DEFINER, timer or delay in the sweep.

## Self-Check: PASSED
