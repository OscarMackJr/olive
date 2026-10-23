---
phase: 04-policy-expiry-and-refusal
plan: 02
subsystem: policy-refusal
tags: [policy, refusal, audit, catalog, purpose]
requires: [04-01]
provides:
  - PolicyEvaluator.EvaluateCatalogAndPurpose and extensible PolicyVerdict (Then) with static detail JSON
  - PolicyRefusalReasons (not_in_catalog, purpose_not_permitted, entitlement_missing, rate_limited)
  - IProposalStore.InsertRefusedAsync optional policyDetailJson (policy_evaluated + policy_refused events)
  - Stored 201 refusals for unknown action and wrong purpose
affects: [04-03, 04-04, 04-05, 04-06, 04-07]
key-files:
  created:
    - src/Olive.Domain/PolicyRefusalReasons.cs
    - src/Olive.Domain/PolicyEvaluator.cs
    - tests/Olive.Tests/PolicyEvaluatorTests.cs
    - tests/Olive.Tests/PolicyEndpointTests.cs
  modified:
    - src/Olive.Application/IProposalStore.cs
    - src/Olive.Persistence/ProposalSql.cs
    - src/Olive.Persistence/PostgresProposalStore.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - src/Olive.Catalog/SubmissionValidator.cs
    - tests/Olive.Tests/ApiIntegrityTests.cs
    - tests/Olive.Tests/ApiProposalTests.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/ProposalStoreRefusalTests.cs
decisions:
  - "D-17 deviation (intentional): new refusal reasons need no migration change. proposal_refusal_reason_format (0006) is a regex ^[a-z][a-z_]{0,63}$, not an enum, so CONTEXT D-17 wording implying a constraint change does not apply."
  - "Endpoint seam EvaluatePolicy(entry, purpose) in ProposalEndpoints is where plans 05 and 06 append entitlement and rate checks via PolicyVerdict.Then."
metrics:
  tasks: 2
  tests: "782/782 passing"
---

# Phase 4 Plan 02: Policy refusal slice Summary

An unknown action or wrong-purpose proposal is now a stored, replayable `refused_policy` refusal with a `policy_evaluated` audit event, never queue-eligible, replacing the old non-stored 400 for unknown actions.

## What was built

- `PolicyEvaluator` (Domain, primitives only): first-failure-wins chain catalog then purpose, ordinal case-sensitive, null/empty required purposes fail closed. `PolicyVerdict.ToDetailJson()` emits exactly `{outcome, reason, checks:[{name,result}]}` from static names; later checks omitted after a failure. `Then(name, passed, failReason)` lets later plans append `entitlement` and `rate` checks without signature changes.
- `IProposalStore.InsertRefusedAsync(..., ct, policyDetailJson = null)`: with detail, writes `submitted`, `policy_evaluated` (detail jsonb), then `policy_refused` after the CAS, in one tenant transaction; without detail the Phase 3 `trace_refused` path is unchanged. `ProposalSql.InsertEvent` now carries `detail`.
- `ProposalEndpoints.PostAsync`: unknown action returns 201 `not_in_catalog` before any trace resolution (D-19); after a verified trace the purpose check runs (D-17); trace refusal keeps priority (D-18); replay is decided before any evaluation (200, no new events; different body is 409).

## Deviations from Plan

1. **[Rule 1 - Bug] proposal_hash is required for the unknown-action refusal.** The plan said ProposalHash may be left null, but constraint `proposal_hash_columns_all_or_none` (0004) requires it whenever canonical_body is stored, which caused a 500. The endpoint now computes `Jcs.ProposalHash(root)` once, before the unknown-action branch, and reuses it for trace verification. Replay still works because body hash and canonical body are stored.
2. **[Rule 3 - Blocking] PermissiveCatalog test helper** (`ApiProposalTests.PermissiveCatalog`) now declares `RequiredPurpose: ["servicing"]`, since the purpose check fails closed on a null list and those tests expect 201 proposed for the `servicing` fixture key.
3. D-17 constraint wording deviation recorded above (no migration needed).
4. Worktree housekeeping: the worktree checkout had CRLF working copies of `contracts/vendored/*` (they are `-text` in .gitattributes). Restored with `git checkout --` of those two files so they match the index (LF). No content change; vocabulary.yaml was not edited.

## Tests

POL_01 (unknown action stored, wrong purpose stored after one trace resolve, trace refusal wins, invalid params still 400), POL_04 (exact detail JSON, no sentinel/parameter values in events or logs, store-level event sequence), POL_05 (replay 200 with same reason and no new events for both reasons; different body 409), API_05 rewritten to the stored-refusal assertion. Full `dotnet build` and `dotnet test`: 782 passed, 0 failed (one StartupGuardTests timing flake appeared once on an earlier run and passed on rerun).

## Known Stubs

None.

## Threat Flags

None. No new endpoints or trust boundaries; audit detail is built only from static names.

## Self-Check: PASSED

Commits 11e5ab1 and 2215c47 present; created files exist.
