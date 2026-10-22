---
phase: 02-contract-integrity-and-idempotent-submission
plan: 03
subsystem: persistence
tags: [postgres, migration, idempotency, rls]
requires: []
provides:
  - migration 0004 (canonical_body, body_sha256, proposal_hash)
  - IProposalStore.FindByRunAsync (tenant-scoped)
affects: [02-07]
key-files:
  created:
    - sql/migrations/0004_proposal_body_hash.sql
    - tests/Olive.Tests/ProposalStoreHashTests.cs
  modified:
    - src/Olive.Application/IProposalStore.cs
    - src/Olive.Persistence/ProposalSql.cs
    - src/Olive.Persistence/PostgresProposalStore.cs
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/ApiProposalTests.cs
decisions:
  - Hash columns nullable with format and all-or-none checks; NULL-hash legacy rows surface as null BodySha256 (never a match)
  - No SHA recompute trigger; drift covered by recompute test
metrics:
  tasks: 2
  completed: 2026-10-22
---

# Phase 2 Plan 03: Proposal body hash persistence Summary

Forward-only migration 0004 adds nullable canonical_body, body_sha256 and proposal_hash to proposal (format and all-or-none checks, INSERT-only column grant to olive_app), and the store gains optional hash fields on NewProposal plus a tenant-scoped FindByRunAsync returning id, current state and stored body hash.

## Tasks

| Task | Commit | Notes |
|------|--------|-------|
| 1 Migration 0004 and pinned expectations | 58c2946 | migration list, checksum, INSERT column list, check-constraint and no-UPDATE tests |
| 2 Insert columns and FindByRunAsync | 1a305e8 | ProposalStoreHashTests (5 tests), doubles updated |

## Verification

Filters API_06, API_01, Category=Architecture, DB_02, DB_04, StartupGuard: 169 passed, 0 failed. Migrations 0001-0003 untouched.

## Deviations from Plan

None to code. Environmental note: in this worktree `core.autocrlf=true` checked out `contracts/vendored/vocabulary.yaml` with CRLF, breaking the vendored SHA check and host startup tests (pre-existing, unrelated to this plan). I rewrote the working-copy file from HEAD (LF) to run tests; nothing under contracts/ was committed. VENDORED.md also shows a CRLF-only working-copy diff, left uncommitted.

## Known Stubs

None.

## Self-Check: PASSED
