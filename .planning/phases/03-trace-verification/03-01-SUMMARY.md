---
phase: 03-trace-verification
plan: 01
subsystem: database
tags: [postgres, migration, dapper, json-schema, refusal, trace]
requires:
  - phase: 02
    provides: proposal table, PostgresProposalStore, proposal response contract
provides:
  - Migration 0006 (trace_verification, trace_schema_version, refusal_reason columns, checks, grants)
  - IProposalStore.InsertRefusedAsync (atomic insert then CAS to refused_policy)
  - Additive optional response fields and trace_not_found / trace_verification_unavailable problem reasons
affects: [03-02, 03-03, 03-04, phase-04]
tech-stack:
  added: []
  patterns: ["Refusal stored as insert (default state) then CAS in one tenant transaction, since olive_app has no INSERT grant on state"]
key-files:
  created:
    - sql/migrations/0006_trace_verification.sql
    - tests/Olive.Tests/ProposalStoreRefusalTests.cs
    - contracts/proposal/v1/proposal-submit-response.refused.valid.json
    - contracts/proposal/v1/proposal-submit-response.badreason.invalid.json
    - contracts/proposal/v1/proposal.refused.valid.json
    - contracts/proposal/v1/proposal.badtrace.invalid.json
  modified:
    - src/Olive.Application/IProposalStore.cs
    - src/Olive.Persistence/PostgresProposalStore.cs
    - src/Olive.Persistence/ProposalSql.cs
    - src/Olive.Contracts/ProposalDtos.cs
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - contracts/proposal/v1/proposal-submit-response.schema.json
    - contracts/proposal/v1/proposal.schema.json
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
    - tests/Olive.Tests/TestDoubles.cs
    - tests/Olive.Tests/ApiProposalTests.cs
key-decisions:
  - "The three migration 0006 columns are nullable on purpose: pre-Phase-3 rows and refusals that never reach trace verification (e.g. Phase 4 not_in_catalog) carry no trace data"
  - "Reason list is not pinned by a CHECK (format only) so Phase 4 can add reasons without another constraint change"
requirements-completed: [TRC-03, TRC-09]
duration: 20min
completed: 2026-10-22
---

# Phase 3 Plan 01: Trace Verification Storage Foundation Summary

Migration 0006 plus `InsertRefusedAsync` and additive response fields, so a refused_policy proposal with a reason and trace outcome can be stored atomically as olive_app.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Migration 0006 and privilege/migration tests | 764592a | Columns, 4 CHECKs, INSERT grant on trace columns, UPDATE grant on refusal_reason |
| 2. Store contract and InsertRefusedAsync | 9c0e981 | Insert + CAS + `submitted`/`trace_refused` events in one transaction |
| 3. Additive response contract | 47b988b | Omit-when-null DTO fields, schemas, new problem reasons, example pairs |

The migration 0006 columns (trace_verification, trace_schema_version, refusal_reason) are nullable on purpose, since pre-Phase-3 rows have no trace data and some refusals never reach trace verification.

## Verification

Full `dotnet test tests/Olive.Tests`: 608 passed, 0 failed. Docker was available, so all Testcontainers integration tests ran. Migrations 0001-0005 are unchanged.

## Deviations from Plan

**1. [Rule 3 - Blocking] Extra interface implementer updated**
- `ThrowingStore` in `tests/Olive.Tests/ApiProposalTests.cs` implements IProposalStore and would not compile without `InsertRefusedAsync`. Added a throwing stub. File was not in the plan's files list.

**2. [Environment] Vendored files showed CRLF noise after the base reset**
- After `git reset --hard 43ab9a1`, `contracts/vendored/vocabulary.yaml` and `VENDORED.md` had CRLF line endings in the worktree, failing the FND-03 hash guard and 18 host-based tests. Restored both from HEAD with a path-scoped `git checkout --`; nothing about them was committed. Worth the orchestrator knowing in case other worktrees show the same symptom.

**3. Event ordering in tests**
- `proposal_event.created_at` is `now()` (transaction time), so the two refusal events share a timestamp. The refusal test asserts the event set (type, from, to) instead of order.

## Known Stubs

None.

## Threat Flags

None. All threat-register mitigations (T-03-01 to T-03-05) are implemented and covered by tests: 42501 on non-granted writes, 23514 on trace_verification / state-reason CHECKs, cross-tenant Get returns null, static problem titles, paired events in one transaction.

## Self-Check: PASSED

Files and commits 764592a, 9c0e981, 47b988b exist.
