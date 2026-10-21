---
phase: 01-foundation-and-walking-skeleton
plan: 09
subsystem: database
tags: [postgres, privileges, migrations, gap-closure]
requires: []
provides:
  - "Migration 0002: olive_app has column-level UPDATE on proposal.state only"
affects: [persistence, privilege-tests]
key-files:
  created: [sql/migrations/0002_proposal_column_grants.sql]
  modified: [tests/Olive.Tests/PrivilegeMatrixTests.cs, tests/Olive.Tests/MigrationRunnerTests.cs]
requirements-completed: [DB-04]
metrics:
  completed: 2026-10-21
---

# Phase 1 Plan 09: Proposal column-level UPDATE grant (WR-02) Summary

Migration 0002 revokes table-level UPDATE on proposal from olive_app and grants UPDATE (state) only; 0001 is untouched.

## Commits
- 13fde20: migration 0002 + privilege tests (matrix updated; column-level, immutable-column 42501 denial, state update still works)
- Task 2 commit: MigrationRunnerTests updated for two migrations (applied ["0001","0002"], 0002 checksum/filename, Real.Count assertions, full-list tamper test, concurrent test)

## Verification
- PrivilegeMatrixTests: 8/8 pass. MigrationRunnerTests: all pass.
- Full suite: 163 pass, 66 fail. All failures are host-startup/vendored-contract tests failing with FND-03 (vocabulary.yaml SHA mismatch). Cause: in this worktree, core.autocrlf=true checked out contracts/vendored/vocabulary.yaml with CRLF (git status shows it and VENDORED.md modified). This is environmental and unrelated to this plan; I did not commit those files.

## Deviations from Plan
- Worktree base was reset to 013978f per the branch check (initial merge-base differed). No other deviations; no files outside plan scope were changed.

## Self-Check: PASSED
