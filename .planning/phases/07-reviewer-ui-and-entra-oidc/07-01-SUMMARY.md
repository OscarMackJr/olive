---
phase: 07-reviewer-ui-and-entra-oidc
plan: 01
subsystem: ui
tags: [razor-pages, cookie-auth, antiforgery, csp, dev-sign-in]
requires: []
provides:
  - Reviewer cookie session (__Host-olive-reviewer, 30 min sliding, 8 h absolute cap)
  - Page shell (layout, olive.css, CSP/no-store headers, flash map, 403 panel)
  - /dev/sign-in fixture picker, POST /auth/sign-out, /signed-out
  - /review own-tenant open-items list
  - Reason codes role_not_held, groups_overage, no_reviewer_tenant
affects: [07-02, 07-03, 07-04, 07-05, 07-06]
tech-stack:
  added: []
  patterns: [route-group auth isolation (AgentKey only on /v1/proposals and /v1/catalog), PRG-ready shell, static-code flash map]
key-files:
  created:
    - src/Olive.Host/Auth/ReviewerCookie.cs
    - src/Olive.Host/Auth/DevSignInGate.cs
    - src/Olive.Host/Startup/SecurityHeaders.cs
    - src/Olive.Host/Pages/Review/ReviewerPageModel.cs
    - src/Olive.Host/Pages/Review/Index.cshtml
    - src/Olive.Host/Pages/Dev/SignIn.cshtml
    - src/Olive.Host/Pages/Shared/_Layout.cshtml
    - src/Olive.Host/Pages/Shared/FlashMessages.cs
    - src/Olive.Host/wwwroot/olive.css
    - tests/Olive.Tests/DevSignInTests.cs
    - tests/Olive.Tests/ReviewerPagesListTests.cs
    - tests/Olive.Tests/ReviewerSessionTests.cs
  modified:
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Auth/ReviewerClaims.cs
    - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
    - src/Olive.Application/IReviewerIdentitySource.cs
    - tests/Olive.Tests/OliveApiFactory.cs
    - tests/Olive.Tests/StartupGuardTests.cs
key-decisions:
  - "DevSignInGate is registered unconditionally as INonProductionOnly, so FND-05 bars production start (as the plan states)."
  - "SignOut page uses the ReviewerUi policy so its antiforgery token (bound to the signed-in user) validates."
requirements-completed: [UI-01, UI-02]
duration: ~2h
completed: 2026-10-24
---

# Phase 7 Plan 01: Reviewer cookie session, page shell, dev sign-in and /review list

A fixture reviewer signs in at /dev/sign-in, gets a hardened cookie session and sees only their own tenant's open items at /review; tenant-less sessions get a static 403.

## Commits
- 81596e4: cookie session, AgentKey route isolation, reason codes, picker data source
- b97c9e5: page shell, picker, sign-out, security headers
- 3139e31: /review list and end-to-end tests

## Verification
Full suite: 1245 passed, 0 failed (includes new ReviewerSession, DevSignIn, ReviewerPagesList tests plus ArchitectureTests, StartupGuardTests, ProblemReasonTitleTests). Greps: no `<script`, `style="`, `Html.Raw` in Pages; olive.css 3988 bytes; no `ReviewerCookie.Scheme` under Endpoints.

## Deviations from Plan

**1. [Rule 3 - Blocking] Existing production positive-control tests broke**
- **Issue:** Registering DevSignInGate (INonProductionOnly) made three StartupGuardTests production positive controls fail with FND-05.
- **Fix:** `ProductionSafePorts` in StartupGuardTests now removes DevSignInGate, modelling a production host without the picker. Also added `All()` to that file's ProductionReviewerStub.
- **Commit:** b97c9e5

**2. [Rule 1 - Bug] Sign-out returned 400 with a valid token**
- **Issue:** `/Account` was anonymous, so the POST saw an anonymous user while the token was issued to the signed-in user.
- **Fix:** `AuthorizePage("/Account/SignOut", "ReviewerUi")`, `AllowAnonymousToPage("/Account/SignedOut")`. An expired session posting sign-out is redirected to sign-in (harmless).
- **Commit:** 3139e31

**3. Minor additions:** `ForbiddenViewModel.cs` and `Display.cs` (UI-SPEC date/age formats) in Pages/Shared; `Pages/Account/SignOut.cshtml` (required for a routable page); the cap/idle tests use a minimal TestServer host with ManualTimeProvider rather than the full factory (no database needed).

**4. Environment:** the worktree checkout had `contracts/vendored/*` converted to CRLF (autocrlf), tripping FND-03. Restored with `git checkout -- contracts/vendored/vocabulary.yaml contracts/vendored/VENDORED.md` (no content change committed). Worktree base was also reset to 50d5d53 as instructed.

## Known Stubs
None. Index does not yet render flash messages or item detail links' targets (/review/items/{id} arrives in plan 02); FlashMessages is built and unit-covered by later plans.

## Threat Flags
None beyond the plan's threat model.

## Self-Check: PASSED
