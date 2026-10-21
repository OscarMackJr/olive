---
phase: 01-foundation-and-walking-skeleton
plan: 08
subsystem: api
tags: [validation, json, utf-16, gap-closure]
requires: []
provides:
  - "Lone-surrogate and NUL strings in proposal bodies return 400 invalid_request"
affects: [src/Olive.Host/Endpoints/ProposalEndpoints.cs]
key-files:
  modified:
    - src/Olive.Host/Endpoints/ProposalEndpoints.cs
    - tests/Olive.Tests/ApiProposalTests.cs
    - tests/Olive.Tests/InMemoryLogCapture.cs
requirements-completed: [API-01]
duration: 20min
completed: 2026-10-21
---

# Phase 1 Plan 08: CR-01 lone-surrogate 400 Summary

Guarded string extraction (TryGetCleanString/IsWellFormed) plus a recursive AllStringsValid walk over `parameters` (names and values) turn invalid UTF-16 and NUL into 400 invalid_request with nothing stored and no Error log.

## Tasks

1. Tests: `API_01_lone_surrogate_and_nul_escapes_give_400_and_store_nothing` (6 rows: run_id, optional field, parameters value, key, nested key, NUL key) and `InMemoryLogCapture.HasErrorOrAbove`. Commits 91ceca1 (initial), plus a follow-up test fix.
2. Fix in `ProposalEndpoints.cs`: TryGetCleanString, IsWellFormed, TryGetPropertyName, AllStringsValid. Full `ApiProposalTests` pass (29/29); Release build clean.

## Deviations from Plan

**[Rule 1 - Bug] Test bodies initially had single-backslash escapes.** The C# `"\ud800"` became a real lone surrogate, which the HTTP encoder replaced with U+FFFD, so the first RED run was not a true RED (observed failures were for the wrong reason, and the same bodies returned 201 once the fix was in). Corrected to `"\\ud800"` and the suite passed. I did not re-run the corrected tests against the unfixed endpoint (git tooling restriction in the worktree), so true RED on the final bodies is unverified; the CR-01 review documents the 500 behavior.

Also: the worktree checkout produced CRLF-only diffs in `contracts/vendored/*`; reverted with `git checkout` of those two files (not committed).

## Self-Check: PASSED
