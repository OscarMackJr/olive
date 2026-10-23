---
phase: 05-generic-review-queue-and-action-approval
plan: 04
subsystem: identity
tags: [apr-02, d-06, q14, self-approval, fail-closed]
requires: [05-01]
provides: [IUserIdentityResolver, UserIdentityVerifier, ApprovalIdentityCheck, FixtureUserIdentityResolver]
affects: [plan 09 approve path]
key-files:
  created:
    - src/Olive.Application/IUserIdentityResolver.cs
    - src/Olive.Application/UserIdentityVerifier.cs
    - src/Olive.Application/ApprovalIdentityCheck.cs
    - src/Olive.Integrations.Entra/FixtureUserIdentityResolver.cs
    - contracts/identity/ (README, schema, valid/invalid, index, 3 fixtures)
    - tests/Olive.Tests/UserIdentityResolverTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Integrations.Entra/Olive.Integrations.Entra.csproj
    - tests/Olive.Tests/{RecordingPorts,FaultingDoubles,OliveApiFactory,ContractSchemaTests,StartupGuardTests}.cs
requirements: [APR-02]
completed: 2026-10-23
---

# Phase 5 Plan 04: User identity resolution and self-approval check Summary

Read-only IUserIdentityResolver port (on_behalf_of opaque, resolved to one Entra oid+tid) with a fail-closed timeout wrapper and an ApprovalIdentityCheck that decides self-approval by oid+tid equality (OrdinalIgnoreCase), backed by an INonProductionOnly fixture.

## Tasks
1. Port, UserIdentityVerifier (5s linked-CTS timeout, catch-all to Unavailable with static cause and exception type name only) and ApprovalIdentityCheck (Allowed | SelfApproval | Unresolved | Unavailable) - commit 8c568df.
2. FixtureUserIdentityResolver, contracts/identity (schema-validated), Program.cs registration with OLIVE_IDENTITY_RESOLVER_TIMEOUT_MS, recording/faulting doubles, OliveApiFactory innerUserIdentity, production-guard test - commit 207a4bd.

Full suite: 971 passed, 0 failed.

## Deviations from Plan

**1. [Rule 3 - Blocking] StartupGuardTests.ProductionSafePorts** now also swaps in a non-fixture IUserIdentityResolver; otherwise the existing production positive-control tests refused (the new fixture is correctly barred). Added APR_02 test proving production refuses the fixture identity resolver.

**2. STATE.md not edited** per orchestrator instruction; the change is below.

Environment: worktree base was reset to 76bff00 as instructed; vendored files restored to LF (not committed).

## Orchestrator follow-up: STATE.md

Apply after merge, editing only these lines:
- Decisions section: add "Phase 5 D-06 (Q14 ruling): on_behalf_of stays an opaque UPN-style string on the wire; IUserIdentityResolver resolves (tenant_id, on_behalf_of) to exactly one Entra (oid, tid); zero, several, malformed or unavailable results refuse approve fail-closed; self-approval is oid+tid equality; reject is exempt (D-18); live adapter deferred."
- Replace the line beginning "- Q14 conflict: NONO_v0.4.3 section 5 ..." with "- Q14 conflict: CLOSED 2026-10-23 by Phase 5 D-06 (see Decisions). on_behalf_of stays UPN-style opaque; oid+tid resolved via IUserIdentityResolver."

## Known Stubs
FixtureUserIdentityResolver is an intentional non-production fixture; the live adapter is deferred.

## Self-Check: PASSED
