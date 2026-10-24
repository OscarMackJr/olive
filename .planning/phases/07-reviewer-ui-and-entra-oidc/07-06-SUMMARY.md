---
phase: 07-reviewer-ui-and-entra-oidc
plan: 06
subsystem: testing
tags: [ui-06, ui-07, credential-matrix, csp, sentinel-scan, uat]
requires: [07-04, 07-05]
provides:
  - 7 credential x 5 route matrix (UI-07), including a real-handler OIDC cookie
  - UI surface pinned to nine pages (UI-06)
  - Consolidated sentinel and header scan over all page types (IDN-08 extension)
  - Manual UAT list for live Entra and browser behaviour
affects: []
key-files:
  created:
    - tests/Olive.Tests/CredentialRouteMatrixTests.cs
    - tests/Olive.Tests/UiSurfaceTests.cs
    - tests/Olive.Tests/SentinelUiTests.cs
    - .planning/phases/07-reviewer-ui-and-entra-oidc/07-HUMAN-UAT.md
  modified: []
requirements-completed: [UI-06, UI-07]
metrics:
  tasks: 3
  completed: 2026-10-24
---

# Phase 7 Plan 06: Credential matrix, UI surface and sentinel evidence Summary

Test-only plan: no production code changed. The matrix, surface and sentinel tests all passed against the existing implementation, so no production deviation was needed.

## Tasks

1. Credential x route matrix, commit ce78696. A shared host in `ci` with the mock IdP and one credential of each kind; 35 theory cells (None, AgentKey, DevHeader, DevCookie, OidcCookie, ForgedCookie, ExpiredCookie x proposals, catalog, queue items, review, dev sign-in) plus the production-config checks (FND-05 on `DevSignInGate`, UI-01 on an `http://` authority). Introspector call count is asserted unchanged for every cell except an agent key on the agent routes. The expired row replays a real cookie after advancing the clock 9 hours, then restores it.
2. UI surface and sentinel scan, commit ca38ac8. `UiSurfaceTests` (4 tests) pins the non-API route set to the nine pages, bans feature words in file and route names, forbids script, inline style, `Html.Raw`, absolute URLs and a second stylesheet, and checks page-model constructors. `SentinelUiTests` drives 28 captured responses across both the dev picker and the real OIDC handler, including a full prompt=login re-challenge decide.
3. Manual UAT, commit 9b90fb3. `07-HUMAN-UAT.md` written with the five numbered checks, the Entra prerequisites, the Q-A group-key note and the EVD-06 credential-audit note.

## Manual UAT status

Pending human response (non-blocking; orchestrator will collect approved/deferred). No STATE.md edit was made for the deferral.

## Verification

- `dotnet test -c Release`: 1393 passed, 0 failed, 0 skipped.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable packages in any project.

## Deviations from Plan

**1. [Rule 1 - Plan inaccuracy] Catalog path.** The plan names `/v1/catalog/v1`, but the handler takes a numeric version (`/v1/catalog/1`; `v1` is a 404 `catalog_version_not_found`). The matrix uses `/v1/catalog/1` so the agent-key cell pins a real 200.

**2. [Rule 1 - Plan inaccuracy] Redirect target.** `/review` redirects are absolute (`https://localhost/auth/sign-in?returnUrl=...`), so the check compares the path.

**3. [Rule 1 - Plan inaccuracy] role_not_held is not a 403 page.** A reviewer without the decision role (`alpha-norole`, OIDC `role-absent`) gets 200 with the references-only item view (APR-03); the 403 pages are `no_reviewer_tenant` and `groups_overage`. The sentinel test captures the restricted item page and asserts "references only".

**4. [Rule 1 - Plan inaccuracy] Rationale and parameter sentinels are not absent from every page by design.** The restored draft page echoes the reviewer's own rationale (07-05), and the audit chain shows the recorded rationale and parameter references to the role holder. Those pages are flagged and exempt for those two sentinels only; every other page, all headers, cookies and logs must be clean. Tokens, codes, client secret and agent key are scanned across everything with no exemption.

**5. [Rule 1 - Plan inaccuracy] Page-model dependency rule.** `ItemModel` and `AuditModel` inject `IProposalStore` and `IQueueStore`. The test therefore forbids Npgsql, Dapper, System.Data, Olive.Persistence and System.Net.Http types in constructors and requires that pages call only `proposals.GetAsync`, rather than the narrower list in the plan.

**6. Route set.** The nine routes include `/` (a redirect `MapGet`), as the plan states.

**7. Environment.** The worktree base was reset to de5f4e9 at start. `contracts/vendored/*` were CRLF in the working tree, which broke FND-03 host startup; they were rewritten from the committed LF blobs and never committed.

## Known Stubs

None.

## Threat Flags

None. T-07-32 to T-07-36 are mitigated and tested; T-07-37 is accepted and covered by the manual UAT.

## Self-Check: PASSED

Files exist: the three test files and `07-HUMAN-UAT.md`. Commits ce78696, ca38ac8 and 9b90fb3 exist.
