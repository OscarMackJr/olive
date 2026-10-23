---
phase: 05-generic-review-queue-and-action-approval
plan: 05
subsystem: reviewer-auth
tags: [apr-01, apr-05, d-01, d-02, d-07, d-08, q17]
requires: [05-02, 05-04]
provides: [DevReviewer scheme, IReviewerIdentitySource, ReviewerClaims, ReviewerContext, contracts/reviewer fixtures, CreateReviewerClient, new reviewer ProblemReasons]
affects: [plan 06 reviewer routes, Phase 7 Entra OIDC]
key-files:
  created:
    - contracts/reviewer/ (schema, valid/invalid, index, README, 8 fixtures)
    - src/Olive.Application/IReviewerIdentitySource.cs
    - src/Olive.Application/ReviewerContext.cs
    - src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs
    - src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs
    - src/Olive.Host/Auth/ReviewerClaims.cs
    - tests/Olive.Tests/ReviewerAuthTests.cs
  modified:
    - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
    - src/Olive.Host/{Program.cs,Problems/ProblemResults.cs,Startup/StartupGuards.cs,Startup/StartupGuardFilter.cs}
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Integrations.Entra/Olive.Integrations.Entra.csproj
    - tests/Olive.Tests/{OliveApiFactory,StartupGuardTests,ContractSchemaTests,ArchitectureTests}.cs
requirements-completed: [APR-01, APR-05]
completed: 2026-10-23
---

# Phase 5 Plan 05: Dev reviewer authentication and caller separation Summary

DevReviewer authentication scheme (header carries only a fixture id; oid, tid, groups and the single-tenant claim come from server-side fixtures and the group map) hard-separated from agent-key auth by scheme-specific challenge overrides, with a four-case caller matrix proven against a test-only probe route.

## Commits
- 8123d01: reviewer fixtures, identity source port, fixture source, claims
- bb86483: DevReviewer handler, AgentKey adjustments, problem reasons, Program registration, Q17 guard
- 9d72f48: reviewer test client, probe route, matrix tests
- (final) test: architecture rule exemption

## Behaviour
- DevReviewer: absent header NoResult; unknown, empty or case-different id Fail (401 reviewer_unauthenticated); known id succeeds. Tenant claim only if the group map yields exactly one tenant (D-08). auth_source is always `stub`, set by the handler.
- Challenge overrides (D-02): DevReviewer challenge returns 403 principal_not_permitted when an Authorization header is present without the reviewer header, else 401 reviewer_unauthenticated. AgentKey challenge returns 403 principal_not_permitted when the reviewer header is present without Authorization, else the unchanged 401 key_inactive. AgentKey authenticate returns NoResult under /v1/queue, so no introspection occurs there.
- Production bar: FixtureReviewerIdentitySource is INonProductionOnly (FND-05); StartupGuards.CheckDevReviewerNotInProduction (code Q17) refuses production while the DevReviewer scheme is registered.

## Framework behaviour observed
The ASP.NET Core multi-scheme challenge behaviour matched the research assumptions; all four matrix cases passed first run, so no design change was needed. Both credentials: the probe (policy scheme DevReviewer) is decided by the reviewer scheme (200, zero introspections); /v1/proposals (default scheme AgentKey) is decided by the agent scheme (key introspected).

## Deviations from Plan

**1. [Rule 3 - Blocking] Probe built as a middleware, not endpoint routing.** The probe is a test-only IStartupFilter middleware that runs IPolicyEvaluator (DevReviewer scheme + authenticated user), then ChallengeAsync/ForbidAsync, mirroring the authorization middleware, because endpoints cannot be safely added from a startup filter alongside WebApplication's own routing. It exercises the same handler challenge paths the real routes will.

**2. [Rule 3 - Blocking] StartupGuardTests.ProductionSafePorts** now swaps in a production reviewer source stub and removes the DevReviewer scheme from AuthenticationOptions (parameter `keepDevReviewerScheme` retains it for the Q17 refusal test); otherwise every existing production positive control would be refused by the new guard.

**3. [Rule 3 - Blocking] ArchitectureTests IDN_08** exempts IReviewerIdentitySource (an in-process fixture lookup, not an outbound port) from the OutboundPort rule.

## Verification
Full suite: 999 passed, 0 failed. `contracts/vendored/` restored to LF in the working tree and never staged.

## Known Stubs
FixtureReviewerIdentitySource and the DevReviewer scheme are intentional non-production stubs; Phase 7 replaces them with Entra OIDC emitting the same claims. The reviewer_tenant_unresolved 403 route behaviour is plan 06.

## Self-Check: PASSED
