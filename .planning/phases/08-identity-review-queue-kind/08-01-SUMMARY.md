---
phase: 08-identity-review-queue-kind
plan: 01
subsystem: database
tags: [migration, fixtures, contract-pin]
requires: []
provides:
  - queue_item.body_sha256 (format + by-kind CHECKs, INSERT-only grant)
  - queue_item_event 'deferred' event type
  - queue_item_decided_poll_idx
  - steward reviewer fixtures
  - IR-1 SHA-256 pin test
  - TestDatabase.SeedIdentityItemAsync
affects: [08-02..08-08]
key-files:
  created:
    - sql/migrations/0010_identity_review_producer.sql
    - tests/Olive.Tests/Ir1ContractPinTests.cs
    - contracts/reviewer/alpha-steward-1.json
    - contracts/reviewer/alpha-steward-2.json
    - contracts/reviewer/beta-steward.json
  modified:
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/QueueTableTests.cs
    - tests/Olive.Tests/TestDatabase.cs
    - tests/Olive.Tests/ReviewerSessionTests.cs
    - tests/Olive.Tests/EntraClaimsMapperTests.cs
    - contracts/reviewer/index.json
    - contracts/reviewer/README.md
key-decisions:
  - "Steward fixture oids continue the aaaaaaaa-0000-4000-8000-0000000000NN pattern with 09, 10, 11 (beta-steward uses 11, same prefix as beta-sup)"
metrics:
  completed: 2026-10-25
---

# Phase 8 Plan 01: Migration 0010, IR-1 pin, steward fixtures Summary

Forward-only migration 0010 adds the identity_review body hash column, deferred event type and decided-poll index, with an IR-1 byte-identity pin test and three steward fixtures.

## Tasks

1. Migration 0010 plus tests and `SeedIdentityItemAsync` - commit f328301
2. IR-1 pin test, steward fixtures, mapper test, fixture count 8 to 11 - commit 47b2573

## Verification

Full suite: 1403 passed, 0 failed (Docker Testcontainers). New tests: `IRQ_01_*`, `IRQ_02_*`, `IRQ_03_*`.

## Deviations from Plan

None in code. Environment note: after `git reset --hard` to the base, `core.autocrlf=true` rewrote `contracts/vendored/vocabulary.yaml` and `VENDORED.md` with CRLF, which broke the FND-03 hash guard in host-startup tests. I restored the committed LF bytes from HEAD with `git show`. These files were not committed or changed in content.

The Ir1ContractPinTests assertion message is static, per plan.

## Known Stubs

None.

## Self-Check: PASSED

Files and commits f328301, 47b2573 exist.
