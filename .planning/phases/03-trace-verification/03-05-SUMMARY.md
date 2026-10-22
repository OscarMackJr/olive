---
phase: 03-trace-verification
plan: 05
subsystem: api
tags: [trace-verification, fail-closed, alarms, retry]
requires:
  - phase: 03-04
    provides: refusal branch, explicit test mode
provides:
  - 422 trace_not_found and 503 trace_verification_unavailable, both retryable, 503 with Retry-After 5
  - IntrospectionCallerRejectedException with introspection_caller_rejected alarm
  - Faulting test doubles and TraceFailClosedTests
affects: [03-06]
key-files:
  created:
    - src/Olive.Application/IntrospectionCallerRejectedException.cs
    - tests/Olive.Tests/FaultingDoubles.cs
    - tests/Olive.Tests/TraceFailClosedTests.cs
  modified:
    - src/Olive.Application/IntrospectionUnavailableException.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Host/Problems/ProblemExceptionHandler.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/TraceVerifiedEndpointTests.cs
requirements-completed: [TRC-02, TRC-07, TRC-08, IDN-06, IDN-07]
completed: 2026-10-22
---

# Phase 3 Plan 05: Trace fail-closed paths Summary

Every trace-step failure now returns a retryable 422 or 503, stores nothing, hides the cause, and raises a distinct alarm code when Olive's own credential or registration is at fault.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Retryable mappings, caller-rejected outcome | 08a1dfb | `ProblemResults.Retryable` (retryable extension, Retry-After 5 on 503); NotFound arm returns 422; exception handler raises `introspection_caller_rejected` and still returns 503 |
| 2. Faulting doubles and suite | see git log (test(03-05)) | `OliveApiFactory` takes `innerResolver` and `innerIssuer` beneath the recorders; 26 new test cases |

Full suite: 721 passed, 0 failed (Docker available).

## Deviations from Plan

**1. [Rule 1] Updated interim test.** `TRC_09_unknown_trace_fails_closed_with_503_and_stores_nothing` (plan 03-03) asserted the interim 503 for an unknown trace. It is now `TRC_09_unknown_trace_gives_422_and_stores_nothing`, as the plan's final mapping requires.

**2. Test refinement.** `ExpiresAfterKey` derives its expiry from the introspected key expiry plus one day, because the fixture keys expire far enough out that a fixed offset from the grant did not exceed them.

**3. Theory parameters.** Plan names a single test for popeye token 401/403 and for introspection caller rejection; both are theories over 401 and 403, and the cause-exposure test is a theory over five resolver faults.

No STATE.md or ROADMAP.md changes (orchestrator owns them). CRLF vendored files were restored with autocrlf off and not committed.

## Known Stubs

None added. `grep -rn "Faulting" src` returns nothing.

## Threat Flags

None. T-03-25 to T-03-31 mitigated and covered by named tests.

## Self-Check: PASSED
