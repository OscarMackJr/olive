---
phase: 01-foundation-and-walking-skeleton
plan: 10
subsystem: ci
tags: [ci, security-gate, gap-closure, WR-05]
requires: []
provides: [fail-closed vulnerable package gate]
affects: [.github/workflows/ci.yml, tests/Olive.Tests/CiAndDevConfigTests.cs]
key-files:
  modified:
    - .github/workflows/ci.yml
    - tests/Olive.Tests/CiAndDevConfigTests.cs
decisions:
  - "Gate requires positive evidence: count of 'The given project' lines must be non-zero and equal the count of 'has no vulnerable packages' lines."
metrics:
  tasks: 2
  files: 2
  completed: 2026-10-21
requirements: [FND-07]
---

# Phase 1 Plan 10: Fail-closed vulnerable package gate Summary

WR-05 closed: the CI vulnerable-package step now runs under explicit `shell: bash` with `set -euo pipefail`, so a failing `dotnet list package` fails the job, and unrecognized or incomplete reports also fail.

## Commits
- 3fb9ea9: test(01-10) RED, `FND_07_vulnerable_gate_fails_closed` (failed: `shell: bash` missing)
- ffe54fd: fix(01-10) GREEN, hardened ci.yml step

## Verification
- `CiAndDevConfigTests`: 9 passed (existing `FND_07_workflow_fails_on_vulnerable_packages` still holds).
- Local `dotnet list package --vulnerable --include-transitive` confirmed wording: "The given project `X` has no vulnerable packages given the current sources."
- Simulated gate runs (scratchpad script): empty file -> 1; project lines with no clean line -> 1; partial (2 projects, 1 clean) -> 1; fully clean (2/2) -> 0.
- `uses: actions/` count unchanged at 2; `|| echo 0` absent; dotnet list line has no `|| true`.

## Deviations from Plan
None in scope. Note: after the required `git reset --hard` to the expected base, the working tree showed `contracts/vendored/VENDORED.md` and `vocabulary.yaml` as modified (apparently line-ending normalization). They were not touched or committed by this plan.

## Self-Check: PASSED
