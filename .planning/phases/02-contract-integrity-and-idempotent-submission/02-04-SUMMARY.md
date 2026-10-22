---
phase: 02-contract-integrity-and-idempotent-submission
plan: 04
subsystem: api
tags: [i-json, duplicate-keys, lossy-numbers, gate]
requires: [02-01]
provides:
  - JsonGate.TryAdmit / Walk / TryGetCleanString (whole-body Q1 gate)
affects: [02-06 schema validation, 02-07 idempotency]
key-files:
  created:
    - src/Olive.Host/Endpoints/JsonGate.cs
    - tests/Olive.Tests/JsonGateTests.cs
    - tests/Olive.Tests/ApiIntegrityTests.cs
  modified:
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/JcsVectorTests.cs
    - tests/Olive.Tests/ApiProposalTests.cs
key-decisions:
  - "Exponent lower bound widened to -324 so 5e-324 (smallest subnormal, required accepted by the plan) passes; upper bound stays 308."
requirements-completed: [API-04]
duration: 30min
completed: 2026-10-22
---

# Phase 2 Plan 04: JsonGate Summary

Whole-body I-JSON gate (single parse, AllowDuplicateProperties=false, whole-tree surrogate/NUL/number/lossless checks, run_id) extracted from the endpoint and asserted against the golden vectors.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. JsonGate + unit tests + vector gate verdicts | 286c757 | duplicate keys, escaped-equal, lone surrogates, NUL, depth, lossy numbers, budget, run_id rules |
| 2. Endpoint wiring + HTTP tests | d1387ea | ProposalEndpoints 181 lines; 400 with nothing stored and no error logs |

Full suite (including Docker integration tests): 475 passed.

## Deviations from Plan

**1. [Rule 1 - Conflict] Phase 1 test case changed.** `API_01_valid_numbers_and_strings_still_accepted` included `12345678901234567890`, which D-05 (lossy numbers refused everywhere) now rejects. Replaced with the exact `12345678901234567000`. No other API_01 test touched.

**2. [Rule 1 - Bug] Exponent range.** The inherited range check (+-308) rejected `5e-324`, which the plan requires to be admitted; lower bound set to -324.

**3. TDD cadence.** Tests and implementation committed together per task (no separate RED commit).

## Notes

- The vector gate test covers all reject cases through both `Walk` on the parsed tree and `TryAdmit` on the raw bytes with run_id spliced in, plus all accept cases through `Walk`.
- Environment: restored `contracts/vendored` (CRLF artifact) before committing; nothing under it was committed.

## Known Stubs

None.

## Self-Check: PASSED
