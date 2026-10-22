---
phase: 02-contract-integrity-and-idempotent-submission
plan: 07
subsystem: api
tags: [idempotency, jcs, concurrency, problem-details]
requires:
  - phase: 02-01
    provides: Jcs.Canonicalize, Sha256Hex, ProposalHash
  - phase: 02-03
    provides: hash columns and FindByRunAsync
  - phase: 02-06
    provides: SubmissionValidator and the idempotency slot
provides:
  - idempotent POST /v1/proposals on (agent_id, run_id)
  - idempotency_conflict problem reason (replaces duplicate_run)
requirements-completed: [API-06, API-07]
key-files:
  created: [tests/Olive.Tests/IdempotencyTests.cs]
  modified: [src/Olive.Host/Endpoints/ProposalEndpoints.cs, src/Olive.Contracts/ProblemReasons.cs, src/Olive.Host/Problems/ProblemResults.cs, tests/Olive.Tests/ApiProposalTests.cs, tests/Olive.Tests/IdentityEndpointTests.cs, docs/STATUS.md]
completed: 2026-10-22
---

# Phase 2 Plan 07: Idempotent submission Summary

POST /v1/proposals now hashes the whole JCS-canonical body (including nono_session_id), looks up the run key before validation, replays 200 `{proposal_id, state}` with the current state for identical bodies, returns a value-free-logged 409 `idempotency_conflict` for different bodies, and re-looks-up after a lost insert race. canonical_body, body_sha256 and proposal_hash are stored.

## Tasks

| Task | Commit | Notes |
|------|--------|-------|
| 1 Failing tests | c72c7ce | RED recorded: compile failure (`ProblemReasons.IdempotencyConflict` missing) |
| 2 Endpoint orchestration | 7f0ac71 | 548/548 tests pass with Docker |

## Decisions

- Replay 200 repeats the Location header; 409 log carries tenant_id, agent_id, proposal_id and 12-char hash prefixes only.
- A stored NULL body hash never matches (always 409); a lost race whose re-lookup is empty fails closed with 409.

## Deviations from Plan

- [Rule 1] The plan said proposal_hash covers action_type, party_ref, parameters and trace_ref in its test sketch; the actual Jcs.ProposalHash (plan 01) covers action_type, party_ref, parameters, justification. The recompute test follows the implemented four fields.
- [Rule 1] `IDN_01_exactly_one_introspect_precedes_insert` event sequence updated to include `FindByRun`.
- `bash build/vulnerable-gate.sh` requires a report argument produced by CI (`<report> <expected-count>`), so it was not run standalone; `dotnet restore --locked-mode` passed.

## Manual verification item

Q20: hometown should confirm the shared JCS vectors in contracts/jcs/v1.

## Environment note

CRLF checkout of contracts/vendored/* was restored from HEAD and not committed.

## Known Stubs

None.

## Self-Check: PASSED
