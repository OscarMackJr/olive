---
phase: 08-identity-review-queue-kind
plan: 03
subsystem: auth
tags: [auth, dispatch, credential-matrix, service-token]
requires: [08-02]
provides:
  - ServiceToken authentication scheme and QueueCaller policy scheme on /v1/queue/items
  - QueueCallerFilters (ReviewerCheck, ProducerCheck, ReviewerOnly, ProducerOnly) and ProducerContext
  - ProblemReasons.TenantNotPermitted, ProducerNotRegistered
  - Credential matrix ServiceToken row (40 cells)
affects: [08-04, 08-06, 08-08]
key-files:
  created:
    - src/Olive.Host/Auth/ServiceClaims.cs
    - src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs
    - src/Olive.Host/Auth/QueueCallerFilters.cs
    - src/Olive.Application/ProducerContext.cs
    - tests/Olive.Tests/ServiceTokenAuthTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Contracts/ProblemReasons.cs
    - contracts/introspection/index.json
    - tests/Olive.Tests/CredentialRouteMatrixTests.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/RecordingPorts.cs
    - tests/Olive.Tests/FaultingDoubles.cs
    - tests/Olive.Tests/IntrospectionFixtureTests.cs
key-decisions:
  - "Queue routes dispatch by header shape: Authorization without the dev header goes to ServiceToken, otherwise DevReviewer"
  - "Reviewer and producer gates key on AuthenticationType and principal_type, never on tenant-claim presence"
requirements-completed: [IRQ-04, IDN-03]
duration: 40 min
completed: 2026-10-25
---

# Phase 8 Plan 03: Credential dispatch and service-token auth Summary

A `QueueCaller` policy scheme routes `/v1/queue/items` to `DevReviewer` or a new `ServiceToken` handler (popeye verifier only, fail closed 401/503), and caller-type filters refuse agent, user, service and delegated callers on reviewer routes while a unit-proven producer gate is ready for plans 04 and 06.

## Tasks

The three tasks are interdependent (the matrix and fixtures needed the handler and filters), so they landed in one commit: `1ddb36c`.

1. ServiceClaims, ServiceTokenAuthenticationHandler, QueueCaller selector, two new reason codes with static titles.
2. QueueCallerFilters, ProducerContext, QueueEndpoints group rework (7 reviewer routes each carry `ReviewerOnly`), test factory `CreateServiceClient`, `RecordingServiceTokenVerifier`, `FaultingServiceTokenVerifier`, `ServiceTokenAuthTests`.
3. Matrix `Cred.ServiceToken` (35 to 40 cells), introspection index entries, fixture count test.

Full suite: 1488 tests, all green after one hygiene fix (below).

## Deviations from Plan

**1. [Rule 1 - Bug in plan assumption] IDN-03 refusal is 401, not 403.** The plan assumed an existing "non-agent principal -> 403" check in the agent-key path. That check exists only inside `POST /v1/proposals`; `GET /v1/proposals/{id}` and `GET /v1/catalog/{v}` admit an application principal (verified: 404 and 200). Mapping the service-token index entries to `application.json` therefore did not refuse them. Instead the entries map to `inactive.json` (a service token is not a session key; popeye's session-key introspection answers inactive), giving 401 `key_inactive`. IDN-03 accepts 401 or 403. No service-token special case was added to the agent handler (D-05). Matrix cells and `IDN_03_...` test pin 401 with a comment. Existing user/application-key behavior on GET routes is untouched (out of scope).

**2. [Rule 3 - Blocking] Header-name hygiene.** `SourceHygieneTests.IDN_02` allows the literal `"Authorization"` only in `AgentKeyHeader`; the selector uses `AgentKeyHeader.Name`.

**3. [Rule 3] `IntrospectionFixtureTests` index count updated from 6 to 8.**

**4. Single commit** for tasks 1-3 rather than three, because each task's tests depend on the others' code.

**5. [Environment]** `contracts/vendored/*` showed CRLF-modified after the base reset; restored with `git checkout -- contracts/vendored/` and not committed.

## Interim state

A service token on bare `GET /v1/queue/items` returns 403 `principal_not_permitted` until plan 06 adds the poll view.

## Known Stubs

None added. `StubServiceTokenVerifier` (plan 02) remains the fixture stand-in for popeye.

## Self-Check: PASSED

Files present, commit 1ddb36c verified, full suite green (1488).
