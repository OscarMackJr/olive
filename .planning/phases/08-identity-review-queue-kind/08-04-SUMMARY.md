---
phase: 08-identity-review-queue-kind
plan: 04
subsystem: api
tags: [create, idempotency, producer]
requires: [08-01, 08-03]
provides:
  - POST /v1/queue/items (ProducerOnly) creating identity_review/candidate_pair items
  - contracts/queue-item/v1/identity-create schema and examples
  - IQueueStore.CreateIdentityItemAsync (ON CONFLICT DO NOTHING, canonical-hash replay/conflict)
  - ProblemReasons.ServerAssignedField, CreateItemResponse (slim)
  - QueueItemSummary/Detail/View trailing optional RulesetVersion (+ ProposalId on View)
affects: [08-05, 08-06, 08-08]
key-files:
  created:
    - contracts/queue-item/v1/identity-create.schema.json
    - contracts/queue-item/v1/identity-create.valid.json
    - contracts/queue-item/v1/identity-create.invalid.json
    - contracts/queue-item/v1/identity-create.link-challenge.invalid.json
    - contracts/queue-item/v1/identity-create.server-assigned.invalid.json
    - tests/Olive.Tests/IdentityReviewCreateTests.cs
  modified:
    - contracts/queue-item/v1/README.md
    - src/Olive.Application/QueueService.cs
    - src/Olive.Application/IQueueStore.cs
    - src/Olive.Persistence/PostgresQueueStore.cs
    - src/Olive.Persistence/QueueSql.cs
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Contracts/QueueDtos.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - tests/Olive.Tests/QueueSchemaTests.cs
    - tests/Olive.Tests/QueueServiceDecideTests.cs
    - tests/Olive.Tests/QueueSurfaceInvariantTests.cs
    - tests/Olive.Tests/ReviewerTenantTests.cs
    - tests/Olive.Tests/TestDatabase.cs
key-decisions:
  - "Server-assigned set (15 names) is checked before the unknown-field check, so server_assigned_field wins; neither field name nor value is echoed"
  - "Replay and conflict are decided inside one transaction by ON CONFLICT DO NOTHING plus a read of the stored body_sha256; 23505 is never caught"
  - "Create response is a slim CreateItemResponse (item_id, state) in Olive.Contracts; claimed_by can never reach bluto"
requirements-completed: [IRQ-01]
completed: 2026-10-25
---

# Phase 8 Plan 04: Producer create path Summary

Stub bluto can POST the six IR-1 fields with a popeye service token and get an idempotent identity_review/candidate_pair item (201, 200 replay, 409 conflict), with every IR-1 section 4 create rejection proven.

## Tasks

1. Create-body schema, valid/invalid variants, `QueueService.CreateAsync` value validation - commit e7abb5c (shared with task 2 because the service needs the store member to compile)
2. Store: `CreateIdentityItemAsync`, `InsertIdentityItem`/`FindByProducerRef` SQL, `ruleset_version` row mapping, fake store updated - commit e7abb5c
3. Endpoint, `server_assigned_field`, integration tests TC-IRQ-01/02/03/11 - commit 26b155e

Full suite: 1541 tests, all green (Docker Testcontainers).

## Deviations from Plan

**1. [Rule 1 - Test invariants] Two existing route-surface tests encoded "no POST on the collection".** `QueueSurfaceInvariantTests.D_03_...` and `ReviewerTenantTests.D_03_...` now expect exactly one collection POST (the producer create route) and still assert no withdraw route and no reviewer POST success. Both renamed to reflect that.

**2. [Rule 3] Added `TestDatabase.AdminCountAsync`** (superuser scalar count) for row-count assertions.

**3. Tasks 1 and 2 landed in one commit** (interdependent compile).

**4. [Environment]** After the base reset, `contracts/vendored/*` showed CRLF-modified; restored with `git checkout -- contracts/vendored/` and not committed.

Test choice note: the `producer` server-assigned case uses value `other-producer` because `olive` appears in the problem `type` URN and would trip the no-echo assertion falsely.

## Notes for plan 08 (bluto note)

- Wire additions beyond IR-1: `201`/`200` body `{"item_id","state"}`, new reason `server_assigned_field`.
- A withdrawn candidate cannot be recreated under the same producer_ref (unique key); replay of it returns 200 with state `withdrawn`. bluto must mint a new candidate id.

## Known Stubs

None.

## Self-Check: PASSED

Schema files, IdentityReviewCreateTests.cs present; commits e7abb5c and 26b155e exist; full suite green.
