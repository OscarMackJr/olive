---
phase: 01-foundation-and-walking-skeleton
plan: 05
subsystem: api
tags: [aspnetcore, minimal-api, auth-handler, problem-details, rls, integration-tests]
requires: [01-03, 01-04]
provides:
  - POST /v1/proposals and GET /v1/proposals/{id} on a real ASP.NET Core host
  - AgentKey authentication handler (introspect first, uniform 401 key_inactive)
  - ProblemReasons and ProposalDtos in Olive.Contracts
  - OliveApiFactory harness with log capture and recording doubles
affects: [01-06, 01-07]
key-files:
  created:
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Contracts/ProposalDtos.cs
    - src/Olive.Host/Auth/OliveClaims.cs
    - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Host/Problems/ProblemExceptionHandler.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/InMemoryLogCapture.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/ApiProposalTests.cs
    - tests/Olive.Tests/IdentityEndpointTests.cs
  modified:
    - src/Olive.Host/Program.cs
requirements-completed: [API-01, API-02, IDN-01, IDN-02, IDN-04]
completed: 2026-10-21
---

# Phase 1 Plan 05: Walking Skeleton API Summary

Introspect-first POST/GET /v1/proposals over the stub popeye port and a real Postgres write and read under forced RLS, with ordered principal checks, strict body validation and leak-free problem+json errors; 168 tests green (Release).

## Commits
- d1e3b5d: feat: walking skeleton POST/GET with introspect-first auth, harness, API_01 roundtrip
- c3fabeb: feat: principal checks, validation, error states, identity tests

## RED evidence
Before any Host code, `dotnet test --filter API_01_post_then_get_roundtrip` failed: `Assert.Equal() Failure: Expected: Created, Actual: NotFound` (the bootstrap host had no routes). After the slice it passed.

## Verification
- Filtered API_/IDN_ run: 94 passed (includes 01-03 unit tests matching the filter). Full solution `dotnet test Olive.sln -c Release`: 168 passed, 0 failed, 0 build errors.
- Greps for `UseDeveloperExceptionPage|UseHttpLogging` in src and `"Authorization"` in src/Olive.Host print nothing; `EnvironmentName = Environments.Production` is in Program.cs.

## Decisions
- Problem responses use `Results.Json(..., "application/problem+json")` with a `reason` extension and static titles; the exception handler logs only the exception type name.
- Body limit enforced twice: Kestrel `MaxRequestBodySize` (BadHttpRequestException 413 caught) and a bounded manual read (TestServer has no Kestrel limit).
- Parameters containing `\u0000` are refused as 400 because jsonb cannot store U+0000 (otherwise a 500).
- A malformed (non-GUID) id fails the `:guid` route constraint and returns a bare 404 with no problem body.

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** HEAD base was 08232f4, reset to f21f07e per the worktree check; vocabulary.yaml re-checked out (sha256 60a44f00).

**[Rule 2 - Missing critical] Extras beyond plan text:** NUL-character rejection for string fields and parameters (prevents a Postgres 500); `IDN_01_unexpected_failure_gives_generic_500...` test so every ProblemReasons constant is exercised.

No temporary sanity-break edits were requested in this plan.

## Known Stubs
`StubKeyIntrospector` is wired as the only introspector (intentional, INonProductionOnly); the production guard lands in 01-06.

## Self-Check: PASSED
All created files exist; commits d1e3b5d and the task 2 commit are in `git log`.
