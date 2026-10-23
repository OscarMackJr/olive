---
phase: 05-generic-review-queue-and-action-approval
fixed_at: 2026-10-23T00:00:00Z
review_path: .planning/phases/05-generic-review-queue-and-action-approval/05-REVIEW.md
iteration: 1
findings_in_scope: 6
fixed: 4
skipped: 2
status: partial
---

# Phase 5: Code Review Fix Report

**Source review:** 05-REVIEW.md  **Iteration:** 1
Scope was an explicit user-approved subset: CR-01, WR-01, WR-02, WR-03 (fix); WR-04, WR-05 (no behaviour change). Info items out of scope.

## Fixed Issues

### CR-01: Self-approval check skipped by racing claim and decide
**Commit:** a5bd16e (status: fixed, requires human verification - concurrency/logic)
**Files:** `src/Olive.Application/IQueueStore.cs`, `src/Olive.Application/QueueService.cs`, `src/Olive.Persistence/PostgresQueueStore.cs`
`IQueueStore.DecideAsync` gained `bool approveIdentityVerified`, true only when the service ran the approve identity check and it passed. Under the row lock, an approve with the flag false returns `NotClaimed` (409 `item_not_claimed`) after the claimant check, so the client retries and the check then runs. Reject, decided/idempotent paths and expiry are unchanged (D-18). The resolver is still not called for non-claimant or expired requests.
Tests: `APR_02_CR_01_*` in `QueueDecisionStoreTests` (store, flag=false on a row claimed by the same reviewer: NotClaimed, no state change, no event; retry verified succeeds; unverified reject still applies) and in `QueueServiceDecideTests` (fake store: open pre-read passes flag false with zero resolver calls; passed check passes flag true).

### WR-01: NUL in rationale caused a 500
**Commit:** a5bd16e
**File:** `src/Olive.Application/QueueService.cs`
Rationale containing U+0000 now returns `InvalidRequest` (400) before the store. Test: `WR_01_rationale_with_a_nul_character_...`. NUL is the only character Postgres `text` rejects; lone surrogates are already rejected by JSON parsing in the endpoint (caught as JsonException/InvalidOperationException).

### WR-02: Claim/release did not validate reviewer oid
**Commit:** a5bd16e
**File:** `src/Olive.Application/QueueService.cs`
`ClaimAsync`/`ReleaseAsync` now throw `ArgumentException` on a blank oid before the store, the same as `DecideAsync`. Test: `WR_02_blank_reviewer_oid_...`. Note: like decide, this surfaces as a 500 if an empty oid ever reaches the service; the endpoint filter/`ToReviewerContext` returning null for a blank oid (review's alternative) was not applied.

### WR-03: CS8509 pragmas
**Commit:** cca9981
**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs`
Pragmas removed; each of the five result switches has `_ => throw new InvalidOperationException("Unknown <x> result.")` (static message). This supersedes the earlier plan acceptance criterion of "no `_ =>` arm", per user approval.

## Skipped Issues

### WR-04: Non-supervisors can claim and lock items
**Status:** no_change_needed (skipped by direction)
**Reason:** Conflicts with locked decision D-09 (role checked at decide time; claim/list allowed without role, 05-VALIDATION D-09 row). Flag for Phase 7 (real IdP) consideration: claim lease/TTL or role-gated claim.

### WR-05: GET/events return stale expired items inconsistently with list
**Status:** no_change_needed (skipped by direction)
**Reason:** Conflicts with D-11 (lazy expiry committed at claim/release/decide only; list filters; no sweeper; GET must not write).

## Verification
- `dotnet build Olive.sln`: 0 errors (76 pre-existing warnings).
- `dotnet test tests/Olive.Tests`: Passed 1128, Failed 0, Skipped 0 (no StartupGuardTests flakes this run).
- Nothing under contracts/vendored/, STATE.md or ROADMAP.md was touched. Commits co-authored per instruction. CR-01/WR-01/WR-02 share one commit because they edit the same service and test files.

_Fixer: Claude (gsd-code-fixer)_
