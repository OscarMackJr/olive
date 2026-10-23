---
phase: 05-generic-review-queue-and-action-approval
plan: 10
subsystem: testing
tags: [demo, evidence, xunit, transcript, invariants]
requires:
  - phase: 05-09
    provides: decision route and reviewer surface
provides:
  - Beat 4 scripted demo with sentinel-clean transcript (EVD-03)
  - build/demo-beat4.sh runner
  - ProblemReasons title completeness guard
  - Reference-only queue surface invariant and no-producer-route guard
affects: [phase-6, evidence]
tech-stack:
  added: []
  patterns: [Trait Demo=beat4 filtered runner, reflection guards over contracts]
key-files:
  created:
    - tests/Olive.Tests/Beat4DemoTests.cs
    - build/demo-beat4.sh
    - tests/Olive.Tests/ProblemReasonTitleTests.cs
    - tests/Olive.Tests/QueueSurfaceInvariantTests.cs
  modified: []
key-decisions:
  - "Demo uses two hosts over one database: a collections-only catalog for the refusal, the default catalog for the in-purpose contrast (the fixture key is only entitled to crm.case_note@1)"
requirements-completed: [EVD-03]
duration: 40min
completed: 2026-10-23
---

# Phase 5 Plan 10: Beat 4 Demo and Surface Guards Summary

**Scripted beat 4 demo (refused_policy / purpose_not_permitted, traced through row, events and zero queue items) with a sentinel-clean transcript, plus guards for problem titles and the reference-only queue surface.**

## Accomplishments
- `Beat4DemoTests` (Trait Demo=beat4): 201 refusal body, stored row, `policy_evaluated` checks (`catalog=pass`, `purpose=fail`), zero `queue_item` rows, reviewer list excludes the refused proposal while a contrast in-purpose proposal is listed with one open item. Writes `TestResults/beat4-transcript.md` (gitignored), scanned for parameter, justification and session-key sentinels.
- `build/demo-beat4.sh` runs `dotnet test --filter "Demo=beat4"` and prints the transcript path; executable bit set; works under Git Bash.
- `ProblemReasonTitleTests`: every `ProblemReasons` constant has a static title other than the fallback (internal_error excepted). All reason codes from plans 05-05..05-09 already had cases, so no production change was needed.
- `QueueSurfaceInvariantTests`: DTO reflection (only `ReviewerPayloadResponse` exposes parameters/justification/on_behalf_of; none expose rationale), HTTP sentinel walk over list, decision, get, events and the role-denied payload, and a route-table check that no `POST /v1/queue/items` or `/withdraw` route exists.

## Task Commits
1. Task 1: de63666 (beat 4 demo, transcript, runner)
2. Task 2: 3410060 (title and surface invariant tests)

## Deviations from Plan
None to production code. Test detail: the plan's "out-of-purpose" proposal needed a second host with the default catalog for the contrast, because `test.open@1` is not entitled for the fixture key (`entitlement_missing`).

## Verification
- `bash build/demo-beat4.sh` exits 0, transcript contains `purpose_not_permitted`.
- Full suite: 1112 passed, 10 failed, all in StartupGuardTests and one CatalogEndpointTests startup test (known flaky under load); rerun of those classes: 62/62 pass.
- `dotnet restore --locked-mode` exits 0.
- CRLF on `contracts/vendored/` was restored to LF in the working tree; nothing under it was staged.

## Self-Check: PASSED
