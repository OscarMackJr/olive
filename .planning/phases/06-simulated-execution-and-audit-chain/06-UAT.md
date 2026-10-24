---
status: complete
phase: 06-simulated-execution-and-audit-chain
source: [06-01-SUMMARY.md, 06-02-SUMMARY.md, 06-03-SUMMARY.md, 06-04-SUMMARY.md, 06-05-SUMMARY.md, 06-06-SUMMARY.md, 06-07-SUMMARY.md, 06-08-SUMMARY.md, 06-09-SUMMARY.md]
started: 2026-10-24T16:33:30Z
updated: 2026-10-24T17:17:40Z
---

## Current Test
<!-- OVERWRITE each test - shows where we are -->

[testing complete]

## Tests

<!-- Section 1: user-flow walk-through (MVP mode). Halt on the first failing step. -->

### 1. Cold start beat 2 demo run
expected: With Docker Desktop running, delete TestResults/beat2-transcript.md, then run `bash build/demo-beat2.sh` from the repo root. A fresh Postgres container starts, all migrations (including 0009) apply, the Demo=beat2 tests pass, the script exits 0, and the last line reads `Transcript: <repo>/TestResults/beat2-transcript.md`.
result: pass

### 2. Agent submits a fixture-traced proposal
expected: Open TestResults/beat2-transcript.md. Line 1 shows agent `liam` submitting a `crm.case_note@1` proposal with HTTP 201 and state `awaiting_approval`. Line 2 says plainly that trace verification used fixtures (`trace_verification = fixture`), not a live hometown.
result: pass

### 3. Named reviewer approves with a rationale
expected: Line 3 shows reviewer `alpha-sup-1` listing the open item, claiming it, and approving with the rationale "Checked the case note reference against the source record." and HTTP 200.
result: pass

### 4. Approval executes once, in simulation only
expected: Line 4 shows the proposal state is now `executed` and the ledger `execution_mode` is `simulated`, with the statement that no network write occurred.
result: pass

### 5. Reviewer traces the whole chain from the execution record
expected: Line 5 shows a single `select * from execution_chain where tenant_id = @tenant_id and execution_id = @execution_id` run as `olive_reader`, returning exactly 1 row. That row contains proposal_ref `olive:proposal/<id>`, trace_verification `fixture`, a policy_decision with outcome `allowed` (checks catalog, purpose, entitlement, rate all pass), a non-empty decided_by plus the rationale, execution_mode `simulated`, outcome `succeeded`, and result_ref `simulated:<proposal_id>`. No parameter values, justification or request body appear in the row.
result: pass

### 6. Simulated target write is one reference-only JSONL line
expected: Line 6 shows exactly one JSONL line with `execution_mode: "simulated"`, `target_system: "nexus_crm"`, and `would_send` holding only `payload://` references (no inline bodies). No session key or token appears anywhere in the transcript.
result: pass

<!-- Section 2: technical checks (run only after section 1 passes). -->

### 7. Duplicate and concurrent execution produce one write
expected: `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionConcurrencyTests|FullyQualifiedName~ExecutionLedgerTests|FullyQualifiedName~PrivilegeMatrixTests"` passes with 0 failures (8-way race gives one Executed and seven Replayed; execution_ledger is INSERT-only for olive_app).
result: pass

### 8. Expired, invalid-catalog and faulted executions do not write
expected: `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionFailureTests|FullyQualifiedName~ExecutionServiceTests|FullyQualifiedName~CatalogExecutionCheckTests"` passes with 0 failures (proposal_expired, catalog_invalid, execution_failed and execution_in_doubt cases).
result: pass

### 9. Restart sweep runs only unexecuted approvals, after startup guards
expected: `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionSweepTests"` passes 8/8, including the case where a failing startup guard prevents any sweep execution.
result: pass

### 10. Chain view tenancy and JSONL sentinel containment
expected: `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionChainViewTests|FullyQualifiedName~ContainmentTests"` passes with 0 failures (cross-tenant query returns zero rows; session key, token and justification sentinels absent from JSONL and ledger rows).
result: pass

<!-- Section 3: coverage check (goal-backward). -->

### 11. Phase outcome holds across the whole suite
expected: Full `dotnet test tests/Olive.Tests` passes with 0 failures (about 1208 tests), confirming that a program reviewer can see an approved proposal execute exactly once in simulation and trace the full core-value chain from its execution record.
result: pass

## Summary

total: 11
passed: 11
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

[none yet]
