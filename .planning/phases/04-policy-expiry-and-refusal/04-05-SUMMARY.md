---
phase: 04-policy-expiry-and-refusal
plan: 05
subsystem: policy-entitlement
tags: [directory, entitlement, fail-closed, endpoint, 503]
requires: [04-04]
provides:
  - POST /v1/proposals entitlement step (user groups intersect agent allowlist) after purpose
  - directory_unavailable 503 retryable problem mapping
  - FixtureDirectoryGroupResolver registered (singleton, uncached) and barred in production
affects: [04-06]
key-files:
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Contracts/ProblemReasons.cs
    - tests/Olive.Tests/StartupGuardTests.cs
    - tests/Olive.Tests/FaultingDoubles.cs
    - tests/Olive.Tests/RecordingPorts.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/PolicyEndpointTests.cs
    - tests/Olive.Tests/IdentityEndpointTests.cs
    - tests/Olive.Tests/TraceVerifiedEndpointTests.cs
decisions:
  - "Directory is consulted only when the trace verified and purpose passed; trace-refused, purpose-refused, not-in-catalog and replay paths never call it (D-18)."
  - "Unknown user or empty/missing groups store entitlement_missing (201); Unavailable/timeout/throw/malformed/caller-rejected return 503 retryable, nothing stored."
metrics:
  tasks: 2
  completed: 2026-10-22
---

# Phase 4 Plan 05: Entitlement in the Live Path Summary

The submission path now applies the entitlement intersection (D-03/D-05) after the purpose check, with directory outages failing closed as a retryable 503 and entitlement gaps stored as `entitlement_missing` refusals.

## Commits
- 5daf709: DI registration (`IDirectoryGroupResolver` fixture singleton, `DirectoryVerifier`), directory test doubles, factory wiring, production startup-guard tests (incl. decorator-wrapped)
- cc8fd58: `ProblemReasons.DirectoryUnavailable`, endpoint entitlement step and 503 mapping, POL_02/POL_04 tests

## Tests
- StartupGuard: production refuses fixture directory resolver, also behind an `IComponentDecorator`; ci host resolves both services. Existing production positive controls swap in a non-fixture directory double.
- PolicyEndpoint: empty groups, other groups, unknown user, agent without entitlement (user has it), both entitled (directory called with key tenant and verbatim on_behalf_of), cross-tenant, 503 for six fault modes with Retry-After and nothing stored, alarms 401/403/malformed, resolver not called for purpose/catalog/trace refusals, no re-call on replay, no group names or UPN in event detail, responses or logs.

## Deviations from Plan
**1. [Rule 1 - Test update] Event-order assertions** in `IdentityEndpointTests.IDN_01` and `TraceVerifiedEndpointTests.TRC_ORDER` now expect `directory` between `resolve` and `insert`, since the accepted path legitimately performs the lookup. No production behavior change.

Worktree base was off at start; reset to the specified base per the orchestrator instruction. ContainmentTests needed no change (it does not enumerate destinations).

## Threat model
T-04-14..18, 19, 20 mitigated: evaluator fail-closed matrix, intersection tests, 503 split, tenant from introspected key only, no groups/UPN in details or logs (sentinel test), production guard. No packages, no HttpClient.

## Known Stubs
None beyond the intentional non-production fixture directory.

## Verification
`dotnet build` 0 errors; full `dotnet test`: 855 passed, 0 failed.

## Self-Check: PASSED
