---
phase: 05-generic-review-queue-and-action-approval
plan: 06
subsystem: reviewer-api
tags: [que-04, apr-01, d-01, d-03, d-08, d-14]
requires: [05-03, 05-05]
provides: [QueueService read side, QueueItemDto, GET /v1/queue/items, GET /v1/queue/items/{id}]
affects: [plans 07+ reviewer routes (payload, events, claim, decide), Phase 7 Razor pages]
key-files:
  created:
    - src/Olive.Application/QueueService.cs
    - src/Olive.Contracts/QueueDtos.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - tests/Olive.Tests/ReviewerTenantTests.cs
    - tests/Olive.Tests/QueueListTests.cs
  modified:
    - src/Olive.Host/Program.cs
requirements-completed: [QUE-04, APR-01]
completed: 2026-10-23
---

# Phase 5 Plan 06: Reviewer list and get endpoints Summary

Authenticated dev reviewers can list and read their own tenant's reference-only review items through a DevReviewer-scheme route group backed by a reusable QueueService.

## Commits
- d76445c: QueueService, DTOs, QueueEndpoints, Program.cs registration
- (second commit) test: ReviewerTenantTests and QueueListTests (17 tests)

## Behaviour
- /v1/queue/items group requires the DevReviewer scheme; an endpoint filter returns 403 reviewer_tenant_unresolved when the tenant claim is absent (two-tenant and no-tenant fixtures). Tenant is read only through ToReviewerContext; query, header and body tenant values are ignored.
- List: filters kind and state (default open), limit default 100 max 500, anything invalid is 400 invalid_request; oldest first and expiry filtering come from the store (no write). Get: 404 item_not_found, identical for foreign and missing ids.
- QueueService lives in Olive.Application and returns application records (QueueItemView, QueueListResult); the Host maps them to snake_case DTOs because Application does not reference Contracts. DTO has no parameters, justification, on_behalf_of or rationale member; null optionals are omitted so items validate against the v1 schema.
- Registered type-based: AddSingleton<QueueService>() plus AddSingleton<IQueueStore, PostgresQueueStore>().
- No POST or withdraw route exists (D_03 test checks the endpoint data source and a POST is non-2xx).

## Deviations from Plan
None. Environment: worktree base was reset to e678d20 at start; contracts/vendored restored to LF in the working tree and never staged.

## Verification
Full suite 1016 passed. One run had a flaky StartupGuardTests FND_04 failure (known); rerun was fully green.

## Known Stubs
None.

## Self-Check: PASSED
