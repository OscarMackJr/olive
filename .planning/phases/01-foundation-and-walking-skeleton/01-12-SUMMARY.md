---
phase: 01-foundation-and-walking-skeleton
plan: 12
subsystem: database
tags: [postgres, privileges, column-grants, migration]
requires: []
provides:
  - "Column-level INSERT grant on proposal for olive_app (migration 0003)"
affects: [phase-2, phase-5]
tech-stack:
  added: []
  patterns: ["new forward migration instead of editing checksum-pinned ones"]
key-files:
  created: [sql/migrations/0003_proposal_insert_columns.sql]
  modified: [src/Olive.Persistence/ProposalSql.cs, tests/Olive.Tests/PrivilegeMatrixTests.cs, tests/Olive.Tests/MigrationRunnerTests.cs]
key-decisions:
  - "state and created_at excluded from INSERT grant; they take table defaults"
requirements-completed: [DB-04]
duration: 15min
completed: 2026-10-21
---

# Phase 1 Plan 12: Proposal INSERT column grants Summary

Migration 0003 revokes table-level INSERT on proposal from olive_app and grants INSERT only on application-supplied columns, so state and created_at cannot be forged (WR-09).

## Commits
- e3c6b74 test(01-12): failing INSERT-privilege tests (RED)
- 2e3f553 feat(01-12): migration 0003 and state removed from ProposalSql.Insert (GREEN)

## Verification
- PrivilegeMatrixTests, MigrationRunnerTests and tenant ablation tests pass (new tests: DB_04_app_cannot_insert_non_default_state, DB_04_app_cannot_insert_created_at, DB_04_app_insert_is_limited_to_application_columns, DB_04_app_can_insert_proposal_with_default_state).
- 0001 and 0002 unchanged. The migration glob is embedded via `..\..\sql\migrations\*.sql`, so no csproj change was needed.

## Deviations from Plan
None to code. 

## Known Issues (environment, not caused by this plan)
Full `dotnet test` shows 72 failures, all host-startup tests (ApiProposal, IdentityEndpoint, StartupGuard, SecretLeakage, DatabaseSafety, VendoredContract). Cause: the worktree checkout has `contracts/vendored/vocabulary.yaml` with CRLF line endings (git reports i/lf w/crlf), so the FND-03 hash check fails. Fixing it means rewriting the vendored file's line endings, which the sandbox denied; I did not work around that. Normalizing the file to LF (or checking out with autocrlf=false) should clear these. As a result, the API tests that insert through `ProposalSql.Insert` were not exercised here; the insert column list is a strict subset of the granted columns and matches the SQL used in the passing privilege test.

## Self-Check: PASSED
