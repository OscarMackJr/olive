---
phase: 05-generic-review-queue-and-action-approval
plan: 01
subsystem: database
tags: [postgres, rls, review-queue, json-schema, ir-1]
requires:
  - phase: 01-04
    provides: proposal table, roles, RLS pattern, SchemaValidator
provides:
  - migration 0008 (queue_item, queue_item_event) with CHECKs, forced RLS, column grants
  - QueueStates constants
  - contracts/queue-item/v1 (IR-1 item schema) replacing v0
affects: [05-02, 05-03, 05-04, 05-05]
tech-stack:
  added: []
  patterns: [column-level grants, append-only event table, reference-only refs enforced by CHECK]
key-files:
  created:
    - sql/migrations/0008_review_queue.sql
    - src/Olive.Domain/QueueStates.cs
    - contracts/queue-item/v1/ (schema, valid, invalid, inline-payload.invalid, README)
    - tests/Olive.Tests/QueueTableTests.cs
    - tests/Olive.Tests/QueueSchemaTests.cs
  modified:
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/RlsPatternTests.cs
    - tests/Olive.Tests/TestDatabase.cs
    - tests/Olive.Tests/TenantIsolationAblationTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
    - CLAUDE.md
key-decisions:
  - "Event-type constants named EventCreated etc. in QueueStates to avoid clashing with state constants of the same literal."
requirements-completed: []
duration: 25min
completed: 2026-10-23
---

# Phase 5 Plan 01: Review queue data and contract foundation Summary

Migration 0008 adds queue_item and queue_item_event with database-enforced reference-only refs, claimant-only decisions, INSERT-only audit and forced RLS. The IR-1 v1 item contract replaces v0.

## Tasks

1. Migration 0008, QueueStates, pinning/privilege/RLS test edits: 1e0516b
2. Table-constraint, column-grant and ablation tests: 5f89f11
3. IR-1 v1 contract, v0 deleted, CLAUDE.md references updated: 1ac6e57

Full suite: 926 passed, 0 failed.

## Deviations from Plan

None in scope. Note: the `contracts/vendored/*` files appeared modified after `git reset --hard` (CRLF checkout via core.autocrlf=true), which broke DB_07 startup tests (vocabulary hash). I restored LF content from HEAD in the working tree; nothing was committed for those files.

## Requirements

QUE-01 and QUE-09 are only partially delivered here (schema and constraints; event appends and enqueue code come in later plans), so REQUIREMENTS.md was not modified.

## Known Stubs

None.

## Self-Check: PASSED
