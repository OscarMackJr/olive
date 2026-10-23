---
phase: 04-policy-expiry-and-refusal
plan: 06
subsystem: policy-rate-lifecycle
tags: [rate-limit, advisory-lock, lifecycle, transition-table, awaiting_approval]
requires: [04-05]
provides:
  - ProposalStates.CanTransition (8-state transition table) enforced in TransitionAsync before DB work
  - IProposalStore.InsertEvaluatedAsync (advisory lock, sliding-window count, insert, accept CAS or rate_limited refusal, events)
  - PolicyEvaluator.AppendRate
  - Accepted proposals reach awaiting_approval with an allowed policy_evaluated event
affects: [04-07]
key-files:
  created:
    - tests/Olive.Tests/LifecycleTransitionTests.cs
    - tests/Olive.Tests/RateLimitTests.cs
  modified:
    - src/Olive.Domain/ProposalStates.cs
    - src/Olive.Domain/PolicyEvaluator.cs
    - src/Olive.Application/IProposalStore.cs
    - src/Olive.Persistence/ProposalSql.cs
    - src/Olive.Persistence/PostgresProposalStore.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/TestDatabase.cs
    - tests/Olive.Tests/OliveApiFactory.cs
decisions:
  - "Rate lock key is hashtextextended(tenant|agent|action, 0); it is taken only inside InsertEvaluatedAsync, which the endpoint reaches only after catalog, purpose and entitlement passed."
  - "Count happens before the insert and the rate outcome is decided before inserting, so a rate_limited row is inserted with expires_at null (never insert then update)."
  - "A catalog entry with max_rate limit 0 fails closed as rate_limited."
metrics:
  tasks: 3
  completed: 2026-10-22
---

# Phase 4 Plan 06: Rate Limit and Lifecycle Guard Summary

Proposals that pass catalog, purpose and entitlement are rate-checked under a per-scope Postgres advisory lock and land in `awaiting_approval` (allowed `policy_evaluated` event) or are stored as `refused_policy` / `rate_limited` (201, replay 200), and every lifecycle transition is table-guarded.

## Commits
- b45be90: transition table and guarded `TransitionAsync` (POL-06)
- 46af88e: `InsertEvaluatedAsync`, `AppendRate`, endpoint integration (rate-checked accept path)
- 2c9b436: `RateLimitTests` and helpers (`TestDatabase.AdminExecAsync`, `OliveApiFactory.CatalogWithRate`)

## Tests
- LifecycleTransitionTests: exhaustive 64-cell matrix, terminal states, illegal edge throws with no event, stale/cross-tenant CAS returns false with no event, one event per success, refused_policy rule retained.
- RateLimitTests (POL_03_*, 13 tests): accepted shape/events/expiry, limit+1 rate_limited 201 with expires_at null and 200 replay, refusals do not consume the limit, 12 parallel POSTs at limit 3 accept exactly 3 (9 rate_limited, no 5xx), sliding window, per-tenant, per-agent and per-action scope (store level), duplicate run, lock not taken for a refused proposal (another tx holding the key does not block it), accepted path does block on the lock, A1 and A2.
- POL_03 ran 3 consecutive times green. Full `dotnet test`: 874 passed, 0 failed. `dotnet build`: 0 errors.

## Assumptions verified
- A1: `hashtextextended` is available on postgres:17-alpine.
- A2: default isolation inside `TenantUnitOfWork` is `read committed`; no retry-on-40001 needed.

## Deviations from Plan

**1. [Rule 1 - Test update] Existing tests that used now-illegal transitions or expected "proposed"**
- `IdempotencyTests`, `ProposalStoreHashTests`, `TenantIsolationAblationTests`, `TenantUnitOfWorkTests` used `proposed -> approved` / `proposed -> expired`, which the table now rejects; they use legal edges (`proposed -> awaiting_approval`, `awaiting_approval -> approved`). `ApiProposalTests` and `PolicyEndpointTests` assertions of accepted state changed from `proposed` to `awaiting_approval`.

**2. [Scope] Second-agent scope tested at the store level**
- The introspection fixtures and stub token issuer support only agent `liam`, so per-agent and per-action scope is proven through `InsertEvaluatedAsync` directly; per-tenant scope is proven end to end (alpha vs beta keys). No fixture changes were made.

**3. Legacy catalog override not needed**
- Production limit 10 per 3600 was not hit by any legacy test; only `RateLimitTests` swap in a small-limit catalog via `OliveApiFactory.CatalogWithRate`.

The worktree base was off at start and was reset to the specified base per the orchestrator instruction.

## Threat model
T-04-21 (concurrency accepts exactly limit), T-04-22 (refused rows excluded from count), T-04-23 (CanTransition plus CAS), T-04-24 (tenant predicate under RLS tx), T-04-25 (one event per transition), T-04-26 (lock only after entitlement; refused path never blocks) are mitigated and tested. No packages, no HttpClient.

## Known Stubs
None.

## Self-Check: PASSED
