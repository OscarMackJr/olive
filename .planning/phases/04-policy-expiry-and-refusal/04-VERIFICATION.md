---
phase: 04-policy-expiry-and-refusal
verified: 2026-10-23T00:00:00Z
status: passed
score: 8/8 requirements verified
overrides_applied: 0
---

# Phase 4: Policy, Expiry and Refusal Verification Report

**Phase Goal:** Policy decides before any human sees a proposal. Out-of-purpose, unentitled or rate-limited proposals are stored as refusals and never queued, every lifecycle transition is guarded, and the full submission order is enforced and asserted.
**Status:** passed. Re-verification: No.

## Requirements Coverage

| Req | Evidence in code | Status |
| --- | --- | --- |
| POL-01 | `PolicyEvaluator.EvaluateCatalogAndPurpose` (ordinal match, empty list fails closed). `ProposalEndpoints.cs` stores the refusal through `StoreRefusalAsync`, which returns 201 `refused_policy`. | SATISFIED |
| POL-02 | `PolicyEvaluator.EvaluateEntitlement` requires both the user's directory groups and the agent allowlist to hold `required_entitlement`. Null or empty data fails closed. Resolver failure returns 503 and stores nothing. `IDirectoryGroupResolver` is read-only and its fixture is `INonProductionOnly`. | SATISFIED |
| POL-03 | `ProposalSql.cs` takes `pg_advisory_xact_lock` keyed on tenant, agent and action. `PostgresProposalStore.InsertEvaluatedAsync` counts, inserts and applies the rate verdict in one transaction. | SATISFIED |
| POL-04 | Every policy path, including the unknown-action and refusal branches, appends a `policy_evaluated` event. `ToDetailJson` is built from static names only. | SATISFIED |
| POL-05 | The replay path (`DecideExistingAsync`) runs before validation and policy, so it appends no events and does not re-evaluate. | SATISFIED |
| POL-06 | `ProposalStates.CanTransition` holds the edge table. `PostgresProposalStore.TransitionAsync` rejects illegal edges before any database work, then runs a CAS `UpdateState` paired with an `InsertEvent` row in one unit of work. It returns false when 0 rows match. | SATISFIED |
| POL-07 | Expiry is `min(now + min(expires_in_seconds ?? 28800, max_expiry_seconds), key.expires_at)`, stored as an absolute time. `expires_at` is null on refusals. `max_expiry_seconds` is a required catalog field. | SATISFIED |
| API-08 | Order is documented in `ProposalEndpoints.cs` and asserted by `SubmissionOrderTests`. The order is: introspect, principal checks, idempotency, schema and catalog, trace, policy (purpose, then entitlement, then rate), persist. A trace refusal wins over a policy refusal (D-18). | SATISFIED |

All eight phase IDs appear in PLAN frontmatter (04-01 through 04-07). REQUIREMENTS.md maps POL-01 to POL-07 and API-08 to Phase 4. POL-08 is mapped to Phase 5 and is out of scope here. No orphaned requirements.

## Spot-Checks

- I re-ran the targeted tests: SubmissionOrder, RateLimit, Expiry, LifecycleTransition, PolicyEndpoint and PolicyEvaluator. Result: 105 passed, 0 failed.
- The orchestrator reported a clean build and 889/889 passing on the full suite.
- A grep for `TBD`, `FIXME` and `XXX` across `src` found nothing.

## Anti-Patterns

None blocking. The RLS and tenant predicates are retained in the persistence code.

## Notes

- Nothing in Phase 4 calls `TransitionAsync` for approve, reject, execute or expire. Those consumers belong to Phase 5. The guard and its tests exist now, which is what POL-06 requires.
- The unchecked boxes in REQUIREMENTS.md and ROADMAP.md are orchestrator bookkeeping, not a gap.
- `StartupGuardTests` and `ApiProposalTests.API_06...` are known to flake under parallel cold start. They are unrelated to this phase.

## Human Verification Required

None.

## Gaps Summary

No gaps. The phase goal is achieved in code and tests.

_Verifier: Claude (gsd-verifier)_
