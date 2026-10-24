---
phase: 06-simulated-execution-and-audit-chain
plan: 06
subsystem: testing
tags: [execution-chain, sentinel-scan, rls, postgres]
requires:
  - phase: 06-03
    provides: execution_chain view and PostgresExecutionStore
  - phase: 06-04
    provides: FaultingExecutionStore
provides:
  - ExecutionChainViewTests with a reusable ChainSelect constant and QueryChainAsync helper
  - SentinelScan.AddJsonl and execution_ledger row scanning
  - IDN-08 JSONL containment tests
affects: [06-07]
tech-stack:
  added: []
  patterns: [single-SELECT chain proof, multi-source sentinel scan]
key-files:
  created:
    - tests/Olive.Tests/ExecutionChainViewTests.cs
  modified:
    - tests/Olive.Tests/ContainmentTests.cs
    - tests/Olive.Tests/SentinelScan.cs
key-decisions:
  - "queue_item is not scanned for the rationale sentinel because it stores the rationale by design"
requirements-completed: [EXE-06, IDN-08]
duration: 20min
completed: 2026-10-24
---

# Phase 6 Plan 06: Chain view proof and JSONL sentinel scan Summary

One SELECT against execution_chain, run as olive_reader keyed by the execution record, returns the proposal, trace verification, policy decision, named approver with rationale and execution result; the IDN-08 sentinel scan now also covers the JSONL output and execution_ledger rows.

## Tasks

1. Chain view tests (6: whole chain, no parameters/justification/canonical_body columns, cross-tenant zero rows, olive_app can select, keyed by the started row, in-doubt shows started without finish): 9ad963f
2. Sentinel scan extension (AddJsonl, execution_ledger table, two IDN-08 tests for session key/token/rationale and justification): 7351ab9

## Deviations from Plan

**1. [Rule 1 - Bug in plan assumption] Policy check names**
- **Issue:** Plan listed purpose_entitlement/principal_delegation/rate_limit; the stored policy_decision uses catalog, purpose, entitlement, rate.
- **Fix:** Test asserts the real names.

**2. [Rule 3 - Blocking] Justification sentinel test uses the default stub flow**
- **Issue:** The verified-trace fixture pins the proposal hash, so changing justification yields trace_proposal_mismatch and no queue item.
- **Fix:** IDN_08_justification_sentinel_absent_from_jsonl_and_view_row runs on the default factory with a modified justification.

## Known Stubs

None.

## Threat Flags

None.

## Verification

ExecutionChainViewTests (6) and ContainmentTests plus SentinelScanSelfTests (10) pass.

## Self-Check: PASSED
