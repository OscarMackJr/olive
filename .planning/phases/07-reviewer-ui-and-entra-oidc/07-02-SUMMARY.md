---
phase: 07-reviewer-ui-and-entra-oidc
plan: 02
subsystem: ui
tags: [razor-pages, antiforgery, prg, decide-time-freshness, simulated-execution]
requires: [07-01]
provides:
  - /review/items/{id} item page (claim, release, decide, role-gated payload, fixture-trace banner)
  - ApprovalExecution.RunAsync shared by the JSON API and the page
  - IDecisionFreshness (stub fixture re-read; Entra branch fails closed until plan 05)
affects: [07-04, 07-05, 07-06]
key-files:
  created:
    - src/Olive.Host/Endpoints/ApprovalExecution.cs
    - src/Olive.Host/Auth/DecisionFreshness.cs
    - src/Olive.Host/Pages/Review/Item.cshtml
    - src/Olive.Host/Pages/Review/Item.cshtml.cs
    - src/Olive.Host/Pages/Shared/TimeText.cs
    - tests/Olive.Tests/DecisionFreshnessTests.cs
    - tests/Olive.Tests/ReviewerPagesPostTests.cs
    - tests/Olive.Tests/ReviewerPagesDecideTests.cs
    - tests/Olive.Tests/DecisionTimeRoleTests.cs
  modified:
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - src/Olive.Host/Auth/ReviewerCookie.cs
    - src/Olive.Host/wwwroot/olive.css
requirements-completed: [UI-03, UI-04, APR-03, UI-05]
completed: 2026-10-24
---

# Phase 7 Plan 02: Item page, decide-time role freshness, shared execution helper

A reviewer can claim, release, approve or reject from the page; approve runs the simulated executor once through the same helper as the JSON API, and a role removed after the claim gives a 403 `role_not_held` with the item still claimed.

## Commits
- 7c657f4: shared ApprovalExecution helper, IDecisionFreshness, DecisionFreshnessTests
- 07830db: Item page, TimeText, CSS additions, page tests

## Verification
`dotnet test` filter ReviewerPages | DecisionTimeRole | DecisionFreshness | ExecutionEndpoint | QueueDecision | Architecture | StartupGuard: 139 passed, 0 failed. No `Html.Raw` in Item.cshtml; no NpgsqlDataSource/Dapper in Item.cshtml.cs; ExecuteAfterApprovalAsync is gone from QueueEndpoints.

## Deviations from Plan

**1. [Rule 2 - Security] Rationale appeared in logs**
- **Issue:** Binding `rationale` as a Razor Pages handler parameter made the framework log "Executing handler method ... with arguments (..., <rationale>, ...)" at Debug level; the sentinel scan caught it.
- **Fix:** OnPostDecide takes only `id` and reads `decision` and `rationale` from `Request.ReadFormAsync`. The plan said "handler parameters (strings only)"; this keeps D-19.
- **Commit:** Task 2 commit.

**2. Minor:** The fixture banner reads the proposal id from `payload_ref` (`olive:proposal/<id>`) rather than QueueItemDetail.ProposalId, because QueueService.GetAsync returns QueueItemView, which has no ProposalId, and the page must not bypass QueueService. TimeText delegates to the existing `Display` formats from plan 01. Added `textarea`, `fieldset` and `.field-label` rules to olive.css (no inline styles allowed by CSP). Expired items show the expired text with no buttons.

**3. Environment:** The worktree started at a different base and was reset to da8f736 per instructions. `contracts/vendored/*` CRLF drift was restored with `git checkout --`, not committed.

## Known Stubs
None. The "View audit history" link targets `/review/items/{id}/audit`, delivered by plan 03. The `Stale` freshness outcome throws until plan 05.

## Threat Flags
None beyond the plan's threat model.

## Self-Check: PASSED
