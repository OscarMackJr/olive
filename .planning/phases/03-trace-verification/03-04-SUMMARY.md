---
phase: 03-trace-verification
plan: 04
subsystem: api
tags: [trace-verification, refusal, idempotency]
requires:
  - phase: 03-03
    provides: trace step, fixture contract, explicit test mode
provides:
  - Stored trace refusals (201 with refusal_reason) through InsertRefusedAsync
  - Replay of a refused run returns 200 with the same body
key-files:
  modified:
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
  created:
    - tests/Olive.Tests/TraceFixtureLoader.cs
    - tests/Olive.Tests/TraceRefusalEndpointTests.cs
requirements-completed: [TRC-03, TRC-04, TRC-05, TRC-06]
completed: 2026-10-22
---

# Phase 3 Plan 04: Trace refusals Summary

A proposal whose trace disagrees on run, tenant, agent or hash is stored as `refused_policy` with its static reason code, returned as 201 with Location and `{proposal_id, state, refusal_reason}`, and replays as 200 with an identical body without calling the token issuer or resolver.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Refusal branch and replay body | 33a4f93 | Verified and Refused share one insert try/catch and lost-race handling; replay carries `existing.RefusalReason` |
| 2. Refusal integration tests | 1a3b8d3 | 13 tests over the committed fixtures, all four reasons, check order, replay finality, GET, cross-tenant 404 |

Full suite: 695 passed, 0 failed.

## Deviations from Plan

None in behavior. Notes:
- `TraceFixtureLoader` did not exist, so it was created as planned.
- The "agent differing from token-delegated agent" test uses the `agent-differs` fixture (agent `not-liam`) and an inline-facts case in the order test, since the Alpha key delegates `liam`.
- Event order is asserted as the set `submitted,trace_refused` (sorted by type), because both events share one transaction timestamp.
- Base reset needed; CRLF vendored files were restored with autocrlf off and not committed.

## Known Stubs

None added.

## Threat Flags

None. T-03-20 to T-03-24 mitigated and tested (body carries only the static reason code; replay precedes the trace step; cross-tenant GET is 404).

## Self-Check: PASSED
