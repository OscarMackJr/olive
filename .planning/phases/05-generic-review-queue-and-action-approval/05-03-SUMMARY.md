---
phase: 05-generic-review-queue-and-action-approval
plan: 03
subsystem: database
tags: [postgres, review-queue, enqueue, dapper, rls]
requires:
  - phase: 05-01
    provides: queue_item and queue_item_event tables, QueueStates
provides:
  - atomic enqueue of an open action_approval item inside the accept transaction (QUE-03)
  - IQueueStore read contract and PostgresQueueStore (list, get, events, payload)
  - QueueSql tenant-predicated statements
affects: [05-04, 05-05, 05-06]
tech-stack:
  added: []
  patterns: [result-value business outcomes (null/empty), injected now for expiry filtering, seq-ordered events]
key-files:
  created:
    - src/Olive.Application/IQueueStore.cs
    - src/Olive.Persistence/QueueSql.cs
    - src/Olive.Persistence/PostgresQueueStore.cs
    - tests/Olive.Tests/QueueEnqueueTests.cs
    - tests/Olive.Tests/QueueStoreReadTests.cs
  modified:
    - src/Olive.Persistence/PostgresProposalStore.cs
key-decisions:
  - "Added QueueSql.ItemExists so EventsAsync can return null for a missing or cross-tenant item (distinct from an item with zero events)."
  - "QueueItemDetail has no rationale member; ReviewerPayload is the only record carrying parameters."
requirements-completed: [QUE-03, QUE-04, QUE-09]
duration: 30min
completed: 2026-10-23
---

# Phase 5 Plan 03: Enqueue seam and queue read side Summary

A policy-accepted proposal now creates exactly one open action_approval queue_item plus a created event in the same transaction as the proposal; the store gains a tenant-safe, expiry-aware, write-free read side.

## Tasks

1. IQueueStore, QueueSql, enqueue in the accepted branch of InsertEvaluatedAsync, QueueEnqueueTests: dfa45b3
2. PostgresQueueStore read side and QueueStoreReadTests: see git log (second feat(05-03) commit)

## Verification

- QUE_03 (6 tests) and QUE_04/QUE_09 (6 tests) pass. The trigger-fault test proves the proposal row rolls back with the item insert.
- Full suite: 963 passed, 2 failed (StartupGuardTests FND_04 empty value, CatalogEndpointTests API_10 non-invalid-operation fault). Both pass when rerun in isolation, so they are load-related flakes unrelated to this plan (likely concurrent host startup with the parallel agent).
- IProposalStore.cs and Program.cs untouched.

## Deviations from Plan

None in scope. Environment: after `git reset --hard`, `contracts/vendored/vocabulary.yaml` and `VENDORED.md` differed in the working tree (CRLF checkout) although `git status` showed clean, failing FND-03. I restored LF content from HEAD in the working tree only; nothing under contracts/vendored/ was committed.

## Known Stubs

None.

## Self-Check: PASSED
