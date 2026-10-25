---
phase: 08-identity-review-queue-kind
plan: 06
subsystem: api
tags: [poll, withdraw, cursor]
requires: [08-03, 08-04, 08-05]
provides:
  - ProducerCursor (opaque base64url codec for decided_at micros + item_id)
  - QueueService.PollDecisionsAsync and WithdrawAsync; IQueueStore.ListDecidedForProducerAsync and WithdrawByProducerAsync
  - GET /v1/queue/items credential dispatch (reviewer list vs producer poll) and POST /v1/queue/items/{id}/withdraw
  - identity-withdraw and identity-poll-response contract schemas with paired examples
affects: [08-07, 08-08]
key-files:
  created:
    - src/Olive.Application/ProducerCursor.cs
    - tests/Olive.Tests/ProducerCursorTests.cs
    - tests/Olive.Tests/IdentityReviewPollWithdrawTests.cs
    - contracts/queue-item/v1/identity-withdraw.{schema,valid,invalid}.json
    - contracts/queue-item/v1/identity-poll-response.{schema,valid,invalid}.json
  modified:
    - src/Olive.Application/{IQueueStore,QueueService}.cs
    - src/Olive.Persistence/{PostgresQueueStore,QueueSql}.cs
    - src/Olive.Contracts/QueueDtos.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - contracts/queue-item/v1/README.md
    - tests/Olive.Tests/{QueueSchemaTests,QueueServiceDecideTests,IdentityReviewCreateTests,IdentityReviewDecisionTests,CredentialRouteMatrixTests,ServiceTokenAuthTests,QueueSurfaceInvariantTests,ReviewerTenantTests}.cs
key-decisions:
  - "Cursor is plain base64url of v1.<unix micros>.<item id N-format>, no HMAC; strict canonical decode; next_cursor is the last returned row, else the supplied cursor, else null"
  - "Repeat withdraw: 200 for the same reason, 409 item_closed for a different reason (resolves RESEARCH Open Question 4)"
  - "GET handler dispatches on authentication type with no route filter; service credentials accept only {kind,state[,decided_after]} (no limit, page fixed at 100)"
  - "Withdraw is one tenant transaction: row lock, guarded UPDATE that also nulls claimed_by/claimed_at, withdrawn event with actor = producer"
requirements-completed: [IRQ-03]
completed: 2026-10-25
---

# Phase 8 Plan 06: Poll and withdraw Summary

bluto can poll accepted and rejected identity decisions through an opaque keyset cursor (100 per page, oldest first by decided_at then item_id) and withdraw open or claimed items, both scoped by token tenant and producer with forced RLS behind the application predicates.

## Tasks

1. ProducerCursor codec and poll/withdraw schemas - commit d03f2dd
2. Poll and withdraw in QueueService, IQueueStore, PostgresQueueStore, SQL (store/service tests, RLS ablation both directions) - commit 07214af
3. Producer GET dispatch, withdraw route, HTTP TC-IRQ-09/10 tests, updated plan 03 assertions - commit b711689

Full suite: 1647 tests green (Docker Testcontainers).

## Deviations from Plan

**1. [Rule 3 - Blocking] Two route-invariant tests asserted that no withdraw route exists.** `QueueSurfaceInvariantTests.D_03_...` and `ReviewerTenantTests.D_03_no_reviewer_create_or_withdraw_route_exists` failed once the producer withdraw route was added. They now assert exactly one withdraw route (POST), and the reviewer test also checks that a reviewer POST to it is 403. The first test was renamed `D_03_only_the_producer_only_create_and_withdraw_routes_exist`. Neither file was in the plan file list.

**2. Test adjustment.** The DB check `queue_item_producer_check` pins identity_review to producer `bluto`, so a "foreign producer" item cannot be seeded. Foreign-producer isolation is tested by calling poll/withdraw with a different producer name against bluto's rows, plus the cross-tenant and action_approval cases.

**3. [Environment]** After the base reset `contracts/vendored/*` appeared CRLF-modified; restored with `git checkout -- contracts/vendored/`, never committed.

## Notes for plan 08 (bluto-owner note, additive wire fields)

- `next_cursor` on the poll envelope and the `{"reason": ...}` withdraw body are additive; the create/withdraw responses are slim (`{item_id, state}`).
- Cursor overlap advice (Pitfall 7a): `decided_at` is stamped inside the decide transaction, so a slow commit can land behind an already-returned cursor; bluto should re-poll with a small overlap and de-duplicate on `item_id`.
- Poll items never carry the rationale, `claimed_by` or any deferral history.

## Known Stubs

None.

## Self-Check: PASSED

Created files present; commits d03f2dd, 07214af, b711689 exist; full suite 1647 passed.
