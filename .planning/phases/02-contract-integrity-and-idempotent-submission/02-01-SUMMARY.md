---
phase: 02-contract-integrity-and-idempotent-submission
plan: 01
subsystem: domain
tags: [jcs, rfc8785, sha256, golden-vectors, hashing]
requires: []
provides:
  - Jcs.Canonicalize, Jcs.Sha256Hex, Jcs.CanonicalizeProposalFields, Jcs.ProposalHash, Jcs.FormatNumber
  - JsonNumbers.IsLossless (lossless-number rule, D-05)
  - contracts/jcs/v1 golden vector set with two-verdict manifest
affects: [02-04 gate, idempotency body hash, proposal_hash]
tech-stack:
  added: []
  patterns: [BCL-only static helpers in Olive.Domain, manifest-driven vector tests]
key-files:
  created:
    - src/Olive.Domain/Jcs/Jcs.cs
    - src/Olive.Domain/Jcs/JsonNumbers.cs
    - tests/Olive.Tests/JcsTests.cs
    - tests/Olive.Tests/JcsVectorTests.cs
    - contracts/jcs/v1/README.md
    - contracts/jcs/v1/vectors.json
    - contracts/jcs/v1/vectors.schema.json
  modified:
    - .gitattributes
key-decisions:
  - "Assumption A1: lossless means the token's significant decimal digits and exponent equal the shortest round-trip text of its double (0.1, 1.0, 1e0 accepted; 9007199254740993 rejected)."
  - "Two-verdict manifest: canonicalize (pure RFC 8785) and gate (Olive I-JSON policy); cyberphone values.json canonicalizes but gates as reject_lossy_number."
requirements-completed: [API-09]
duration: 25min
completed: 2026-10-22
---

# Phase 2 Plan 01: JCS canonicalizer and golden vectors Summary

In-house BCL-only RFC 8785 canonicalizer with the four-key proposal_hash builder, a lossless-number rule, and a committed byte-exact golden vector set for hometown.

## Tasks

| Task | Commit | Result |
|------|--------|--------|
| 1. Jcs, JsonNumbers, unit tests | 48b4560 | Appendix B table (24 values) and edge cases pass |
| 2. contracts/jcs/v1 vectors and vector tests | 14614be | 13 canonical vectors, 6 reject cases, manifest and schema |

Full non-integration suite: 212 passed.

## Assumptions recorded

- A1 (lossless-number reading) and the two-verdict design are the adopted RESEARCH recommendations, not yet confirmed by hometown. Both are stated in `contracts/jcs/v1/README.md`.

## Deviations from Plan

**1. [Rule 3 - Blocking] .gitattributes ordering.** The plan said to append `contracts/jcs/v1/* -text`; an existing architecture test (`FND_03_gitattributes_vendored_rule_is_last`) requires the vendored rule to stay the last line, so the new rule was inserted immediately before it. Commit 14614be.

**2. TDD cadence.** Tests and implementation were written together and committed as one `feat` commit per task (no separate RED commit), so the plan-level RED gate is not visible in git history. Tests passed on first run.

**3. Vector check scope.** `vectors.schema.json` uses `if/then/else` and `minItems`, which the test-only `JsonSchemaLite` does not evaluate; the conditional (reject cases have no canonical/sha256, others have both) is asserted by hand in `API_09_manifest_conforms_to_schema`.

## Notes for downstream plans

- Duplicate-key and lone-surrogate gate verdicts are NOT asserted here; plan 04 gate tests must assert them against `vectors.json`.
- Hometown manual verification of the folder is still outstanding (hand `contracts/jcs/v1/` to the hometown owner, record the pass).
- `Jcs.FormatNumber` is public (needed by Appendix B tests; no InternalsVisibleTo in the repo).
- Environment note: after `git reset --hard`, `contracts/vendored/*` appeared modified (line-ending artifact); restored with `git checkout -- <those two files>`; no vendored change is committed.

## Known Stubs

None.

## Self-Check: PASSED
