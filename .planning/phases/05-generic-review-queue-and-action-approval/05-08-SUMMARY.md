---
phase: 05-generic-review-queue-and-action-approval
plan: 08
subsystem: review-queue
tags: [claim, release, lazy-expiry, concurrency, postgres]
requires: [05-07]
provides:
  - IQueueStore.ClaimAsync / ReleaseAsync with ClaimResult / ReleaseResult unions
  - PostgresQueueStore.ExpireInTransactionAsync (internal static, reusable by decide in plan 09)
  - POST /v1/queue/items/{id}/claim and /release
affects: [05-09]
key-files:
  created: [tests/Olive.Tests/QueueClaimTests.cs, tests/Olive.Tests/QueueReleaseTests.cs, tests/Olive.Tests/QueueExpiryTests.cs]
  modified: [src/Olive.Application/IQueueStore.cs, src/Olive.Application/QueueService.cs, src/Olive.Persistence/QueueSql.cs, src/Olive.Persistence/PostgresQueueStore.cs, src/Olive.Contracts/ProblemReasons.cs, src/Olive.Host/Problems/ProblemResults.cs, src/Olive.Host/Endpoints/QueueEndpoints.cs, tests/Olive.Tests/ReviewerTenantTests.cs]
decisions:
  - Business outcomes are returned as result values inside the unit of work so the lazy-expiry commit survives (POL-08).
  - claimed_by holds the reviewer Entra oid; no role is required to claim or release.
requirements-completed: [QUE-05, QUE-06, QUE-09, QUE-10, POL-08]
metrics:
  tasks: 2
  completed: 2026-10-23
---

# Phase 5 Plan 08: Claim, Release and Lazy Expiry Summary

Reviewers claim (one winner via SELECT FOR UPDATE plus guarded UPDATE) and release (claimant only) queue items over HTTP, and an expired proposal is withdrawn in one committed transaction before 409 proposal_expired is returned.

## What was built
- Store: `ClaimAsync`/`ReleaseAsync` lock the item row with the tenant predicate. If `expires_at <= now` they call `ExpireInTransactionAsync`, which does the proposal CAS awaiting_approval -> expired via `ProposalSql.UpdateState`, writes a `system` proposal_event transition, withdraws the item (`proposal_expired`) and writes a `withdrawn` queue_item_event, all on the same (conn, tx). It then returns `ProposalExpired` as a value. An unexpected proposal state throws and rolls everything back.
- Service and routes: exhaustive switch mappings (no discard arm): 200, 409 already_claimed / item_not_claimed / item_closed / proposal_expired, 403 not_claimant, 404 item_not_found. New reasons have static titles.
- Tests: 16-way store claim race, 5-way HTTP race, release rules, boundary at exactly expires_at, claimed-then-expired release with ordered history, rollback on inconsistency, D-04 404/403/401 cases.

## Deviations from Plan
- [Rule 1 - existing test] `ReviewerTenantTests.D_03_no_create_or_withdraw_route_exists` asserted no POST on any queue route; adjusted to permit POST only on `/claim` and `/release` (the plan's own acceptance criterion). Included in the task 2 commit.
- Environment: restored LF content for `contracts/vendored/*` in the working tree (not staged or committed).

## Verification
Full suite: 1037 tests, all pass after the D-03 test update (the one failure on the first run was that test). Acceptance greps: no `TransitionAsync`, `lock (` or `ConcurrentDictionary` in PostgresQueueStore.cs.

## Commits
- a2ad426: store claim/release/expiry
- 734fb0b: routes, problem mapping, D-03 test update

## Self-Check: PASSED
