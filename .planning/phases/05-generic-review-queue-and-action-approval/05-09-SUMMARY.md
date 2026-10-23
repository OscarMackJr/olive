---
phase: 05-generic-review-queue-and-action-approval
plan: 09
subsystem: review-queue
tags: [decision, approval, self-approval, idempotency, lazy-expiry, postgres]
requires: [05-04, 05-08]
provides:
  - IQueueStore.DecideAsync with DecideResult union
  - QueueService.DecideAsync with DecisionResult union
  - POST /v1/queue/items/{id}/decision
affects: [05-10]
key-files:
  created: [tests/Olive.Tests/QueueDecisionStoreTests.cs, tests/Olive.Tests/QueueServiceDecideTests.cs, tests/Olive.Tests/QueueDecisionTests.cs, tests/Olive.Tests/SelfApprovalTests.cs, tests/Olive.Tests/QueueDecisionExpiryTests.cs]
  modified: [src/Olive.Application/IQueueStore.cs, src/Olive.Application/QueueService.cs, src/Olive.Persistence/QueueSql.cs, src/Olive.Persistence/PostgresQueueStore.cs, src/Olive.Contracts/ProblemReasons.cs, src/Olive.Contracts/QueueDtos.cs, src/Olive.Host/Problems/ProblemResults.cs, src/Olive.Host/Endpoints/QueueEndpoints.cs, src/Olive.Host/Endpoints/ProposalEndpoints.cs, tests/Olive.Tests/ReviewerTenantTests.cs]
requirements-completed: [QUE-07, QUE-08, QUE-09, QUE-10, APR-01, APR-02, APR-04, APR-05, POL-08]
metrics:
  tasks: 3
  completed: 2026-10-23
---

# Phase 5 Plan 09: Approval Decision Summary

A claimant reviewer holding the kind's required role approves or rejects an action proposal; the queue item and the proposal move together in one transaction, with self-approval refused on approve only.

## What was built
- Store `DecideAsync`: locks the item (tenant predicate), classifies (decided same/different/other actor, expired, closed, not claimed, not claimant), then on the same (conn, tx) runs the guarded item UPDATE, the `ProposalSql.UpdateState` awaiting_approval -> approved|rejected compare-and-set, a proposal_event and a queue_item_event. A CAS miss throws and rolls the item update back. Lazy expiry reuses 05-08's `ExpireInTransactionAsync` and returns `ProposalExpired` as a value so the commit survives. Event details carry only item_id, decision and auth_source; the rationale lives only on the item row.
- Service `DecideAsync`: blank oid throws; kind support (action_approval only), decision membership and rationale (non-blank incl. Unicode spaces, <= 4000), registry-driven role gate, then for approve only and only when the pre-read shows a live claim by the actor, `ApprovalIdentityCheck` runs outside any transaction (SelfApproval / Unresolved / Unavailable refuse). Reject never calls the resolver. The store re-validates under lock.
- Route: strict two-member body (`decision`, `rationale`; any other member is 400), exhaustive result switch with no discard arm, 503 `identity_resolution_unavailable` through `ProblemResults.Retryable`. `ProposalEndpoints.ReadBoundedAsync` made internal for reuse. New problem reasons all have static titles.
- Tests: 9 store, 7 service unit, 19 queue decision HTTP, 15 self-approval/expiry/sentinel. Full suite: 1087 passed.

## Deviations from Plan
- [Rule 1 - existing test] `ReviewerTenantTests.D_03` allowlist extended to `/decision` (create/withdraw still absent), as the orchestrator directed.
- Environment: worktree base was reset to aa1f3d9 per the branch check; vendored files restored to LF from HEAD (never staged).
- DecisionRequest DTO is declared in contracts for documentation; the route parses the body manually to reject unknown members and duplicates.

## Known Stubs
None.

## Commits
- a2128b2: store DecideAsync
- 65c13d6: service, route, DTOs, reasons, D-03 allowlist
- d07d7bb: HTTP integration tests

## Self-Check: PASSED
