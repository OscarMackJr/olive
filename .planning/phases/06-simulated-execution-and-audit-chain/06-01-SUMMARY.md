---
phase: 06-simulated-execution-and-audit-chain
plan: 01
subsystem: database
tags: [postgres, rls, ledger, view, json-schema]
requires:
  - phase: 05-review-queue
    provides: queue_item, queue_item_event, RLS and grant pattern
provides:
  - execution_ledger table (INSERT-only, forced RLS, one row per proposal and phase)
  - execution_chain security_invoker view
  - execution-record v1 contract
affects: [06-02, 06-03, 06-04, 06-05, 06-06, 06-07]
tech-stack:
  added: []
  patterns: [security_invoker view over forced-RLS tables, INSERT-only ledger]
key-files:
  created:
    - sql/migrations/0009_execution_ledger.sql
    - tests/Olive.Tests/ExecutionLedgerTests.cs
    - contracts/execution-record/v1/execution-record.schema.json
    - contracts/execution-record/v1/execution-record.valid.json
    - contracts/execution-record/v1/execution-record.invalid.json
  modified:
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/RlsPatternTests.cs
    - tests/Olive.Tests/TenantIsolationAblationTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
    - CLAUDE.md
key-decisions:
  - "EXE-02 unique row per proposal implemented as unique (tenant_id, proposal_id, phase) (D-07)"
  - "Chain view anchored on the started row; omits parameters, justification, canonical_body"
requirements-completed: [EXE-02, EXE-04, EXE-06]
duration: 25min
completed: 2026-10-24
---

# Phase 6 Plan 01: Execution ledger and chain view Summary

Migration 0009 adds an INSERT-only, forced-RLS `execution_ledger` (unique per tenant, proposal and phase, simulated mode pinned, static snake_case reasons) and a `security_invoker` `execution_chain` view; execution-record v1 replaces the v0 placeholder.

## Tasks

1. Migration 0009 plus ledger constraint tests and migration pins: f996abd
2. Privilege-matrix, RLS-pattern and ablation coverage for ledger and view: 271a3e4
3. execution-record v1 contract replacing v0: 683bf9c

## Deviations from Plan

- [Rule 3] CLAUDE.md referenced `execution-record/v0/`; updated to v1 so the no-dangling-reference check passes.
- The view comment in the migration says it omits "request bodies and free text supplied by the agent" rather than naming the columns, so the acceptance grep for parameters/justification/canonical_body over non-comment lines returns 0.
- Added a contract test (`EXE_04_execution_record_v1_pins_simulated_mode_and_static_reason`) beyond the paired-example theory.
- Ablation ledger seeding uses the superuser `AdminExecAsync` helper because olive_app cannot write another tenant's rows.

## Known Stubs

None.

## Threat Flags

None.

## Verification

Full `dotnet test tests/Olive.Tests`: 1142 passed, 0 failed. 0001-0008 unchanged.

## Self-Check: PASSED
