---
phase: 01-foundation-and-walking-skeleton
plan: 02
subsystem: database
tags: [postgres, rls, migrations, testcontainers, roles]
requires: [01-01]
provides:
  - Idempotent role bootstrap (olive_owner, olive_migrator, olive_app, olive_reader)
  - Forward-only MigrationRunner with advisory lock, SET ROLE olive_owner, SHA-256 drift failure
  - Migration 0001 (proposal, proposal_event) with forced RLS and INSERT-only events
  - PostgresFixture assembly fixture exposing only least-privilege role connection strings
affects: [01-03, 01-04, 01-05, 01-06]
key-files:
  created: [sql/bootstrap/001_roles.sql, sql/bootstrap/002_database.sql, sql/migrations/0001_init.sql, src/Olive.Persistence/PostgresUrl.cs, src/Olive.Persistence/MigrationFile.cs, src/Olive.Persistence/MigrationCatalog.cs, src/Olive.Persistence/MigrationRunner.cs, src/Olive.Persistence/MigratorCommand.cs, tests/Olive.Tests/PostgresFixture.cs, tests/Olive.Tests/TestDatabase.cs, tests/Olive.Tests/PostgresUrlTests.cs, tests/Olive.Tests/MigrationRunnerTests.cs, tests/Olive.Tests/DatabaseRoleTests.cs, tests/Olive.Tests/RlsPatternTests.cs, tests/Olive.Tests/PrivilegeMatrixTests.cs]
  modified: [src/Olive.Migrator/Program.cs]
requirements-completed: [DB-01, DB-02]
requirements-partial: [DB-03, DB-04]
duration: 40min
completed: 2026-10-21
---

# Phase 1 Plan 02: Database Foundation Summary

Role bootstrap, forward-only migrator and migration 0001 with forced tenant RLS, proven by 40 passing tests (including DB_01..DB_04) running as real least-privilege roles in Testcontainers.

## D-17 result
- `deploy-postgres-1 server_version: 17.10` (image `postgres:17-alpine`). `\dn` showed only `public`; no `action_broker` or `integration_outbox` schema was hand-applied.
- Pinned Testcontainers image: `postgres:17-alpine` (`PostgresFixture.PostgresImage`).
- The container was started for the read-only check and stopped again.

## Commits
- cb58b25 feat: role bootstrap, migration 0001, PostgresUrl converter
- d61860c feat: migration runner, migrator command, Testcontainers fixture
- (task 3) test: table-driven role, RLS-pattern and privilege-matrix tests

## Notes and decisions
- DB-03 and DB-04 are fully satisfied for all five tables only after Phases 5 and 6; tests are table-driven (`ExpectedTables`, `Expected` arrays) so those phases append names (research Q1).
- The plan's "OpenConnectionAsync" is not an Npgsql API; the runner uses `NpgsqlConnection.OpenAsync`. The 01-04 DB-05 scan must allow `MigrationRunner.cs` (and the test fixture) to open connections.
- DB_01 re-run test flips `olive_reader` (not `olive_app`) to BYPASSRLS for a few milliseconds and restores it in `finally`; roles are cluster-wide so a parallel test that expected reader isolation could in theory be affected. None exist yet.
- Tampering checks done and reverted: removing a `force row level security` line fails DB_03; adding `delete` to the proposal_event grant fails DB_04.
- `contracts/vendored/VENDORED.md` shows as modified in the worktree (line-ending artifact from checkout); intentionally not committed.
- xUnit1051 analyzer warnings (CancellationToken) appear in new tests; not errors, left as is.

## Deviations from Plan
**[Rule 3 - Blocking] Worktree base mismatch:** HEAD was at 08232f4; reset to f835f42 per the worktree check; vocabulary.yaml re-checked out (sha256 60a44f00).

**[Rule 1 - Bug] Comment in 001_roles.sql** mentioned the word "password", which would fail the case-insensitive acceptance grep; reworded.

## Known Stubs
None.

## Self-Check: PASSED
