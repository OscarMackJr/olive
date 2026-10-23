---
phase: 04-policy-expiry-and-refusal
plan: 03
subsystem: proposal-expiry
tags: [expiry, migration, contract, idempotency]
requires: [04-02]
provides:
  - Migration 0007 (proposal.expires_at timestamptz null, insert-only grant, proposal_rate_idx for plan 06)
  - Server-computed absolute expires_at on accepted proposals, exposed on GET /v1/proposals/{id}
  - Optional expires_in_seconds (integer >= 1) in the proposal-request v1 contract
affects: [04-06, 05]
key-files:
  created:
    - sql/migrations/0007_policy_expiry_rate.sql
    - tests/Olive.Tests/ExpiryTests.cs
  modified:
    - src/Olive.Application/IProposalStore.cs
    - src/Olive.Persistence/ProposalSql.cs
    - src/Olive.Persistence/PostgresProposalStore.cs
    - src/Olive.Contracts/ProposalDtos.cs
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - contracts/proposal/v1/proposal.schema.json
    - contracts/proposal/v1/proposal.valid.json
    - contracts/proposal/v1/proposal-request.schema.json
    - contracts/proposal/v1/proposal-request.valid.json
    - contracts/proposal/v1/proposal-request.invalid.json
    - tests/Olive.Tests/PrivilegeMatrixTests.cs
    - tests/Olive.Tests/MigrationRunnerTests.cs
    - tests/Olive.Tests/ContractSchemaTests.cs
    - tests/Olive.Tests/ProposalStoreRefusalTests.cs
decisions:
  - "expires_at = min(now + min(expires_in_seconds ?? 28800, max_expiry_seconds), key.expires_at), computed once from the injected TimeProvider, stored absolute (D-14); key cap is silent (D-15)."
  - "expires_in_seconds is parsed with TryGetInt64; non-number, non-int64, 1.0, 1e0 -> 400 schema_violation at /expires_in_seconds. It stays in the idempotency body hash and out of the four-key proposal_hash (D-16)."
metrics:
  tasks: 2
  completed: 2026-10-22
---

# Phase 4 Plan 03: Proposal Expiry Summary

Accepted proposals now carry a server-computed absolute `expires_at` (default 8h, shorten-only, per-action max, silently capped at the key's own expiry), stored via migration 0007 and returned on GET; out-of-range `expires_in_seconds` is a 400 with nothing stored.

## Commits
- 97affb4: migration 0007, store/GET/schema persistence of expires_at, request schema gains expires_in_seconds
- 11e0f8e: endpoint computation and validation, ExpiryTests (POL_07_*)

## Deviations from Plan

- The request-schema files (Task 2 scope) were committed in the Task 1 commit because the ContractSchema tests added in Task 1 exercise them. No behavioral impact.
- The schema (envelope) validator already rejects 0, negatives, 1.5 and strings with 400; the endpoint adds the over-max, non-int64 (1.0, 1e0, huge) checks after the catalog entry is known.
- Worktree base was off by one commit at start; reset to the specified base per the orchestrator instruction.

## Threat model
T-04-10..12 mitigated (400 over max, TryGetInt64 -> 400, insert-only column grant verified by `POL_07_app_cannot_update_expires_at`). T-04-13 accepted as planned.

## Known Stubs
None.

## Verification
`dotnet build` 0 errors; full `dotnet test` 807 passed, 0 failed.

## Self-Check: PASSED
