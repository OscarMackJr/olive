---
phase: 04-policy-expiry-and-refusal
plan: 07
subsystem: api
tags: [submission-order, policy, replay, contracts]
requires: [04-06]
provides:
  - API-08 peeled-fault order tests
  - POL-05 replay matrix for all four policy refusals
  - additive v1 contract docs and policy-refused examples
affects: [phase-05]
key-files:
  created:
    - tests/Olive.Tests/SubmissionOrderTests.cs
    - contracts/proposal/v1/proposal-submit-response.policy-refused.valid.json
    - contracts/proposal/v1/proposal.policy-refused.valid.json
  modified:
    - contracts/proposal/v1/README.md
    - tests/Olive.Tests/ContractSchemaTests.cs
key-decisions:
  - "No production edits and no marker renames: existing markers (introspect, FindByRun, token, resolve, directory, insert, insert_refused) are enough; validator is not decorated so step 4 uses the fallback rule (status plus absence of later markers and rows)."
requirements-completed: [API-08, POL-05, POL-06]
duration: 25min
completed: 2026-10-23
---

# Phase 4 Plan 07: Submission order and replay matrix Summary

Peeled-fault tests prove the fixed order 401, 403, 409, 400, 422, purpose_not_permitted, entitlement_missing, rate_limited with recorded markers showing later steps never ran, plus a replay matrix for all four stored policy refusals.

## Tasks

1. Submission-order tests (commit 12e98b1): 9 API_08_* tests and a 4-case POL_05 theory, all green. Replay asserts 200, same state and reason, markers exactly [introspect, FindByRun], no new directory calls, no new proposal_event or proposal rows.
2. Contract docs and examples (see git log, docs(04-07) commit): README additive-changes section; policy-refused sibling examples registered in ContractSchemaTests; the badreason invalid example is still invalid.

## Deviations from Plan

- Second-call token marker is absent in the step 7/8 tests because the service token is cached from the first call; assertions reflect that. Test-only adjustment.
- Directory request count is cumulative on the recording resolver; step 7 asserts the marker list (one directory marker this round).
- Planning bookkeeping: per orchestrator instruction, .planning/ROADMAP.md and .planning/REQUIREMENTS.md were NOT edited (checkbox, progress row and POL-01..07/API-08 completion marking left to the orchestrator). POL-08 untouched.
- No src/ changes.

## Verification

Full `dotnet test`: 889 passed, 0 failed. `git diff --stat src/` empty.

## Known Stubs

None.

## Self-Check: PASSED
