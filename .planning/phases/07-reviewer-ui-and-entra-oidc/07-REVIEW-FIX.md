---
phase: 07-reviewer-ui-and-entra-oidc
fixed_at: 2026-10-25T01:51:58Z
review_path: .planning/phases/07-reviewer-ui-and-entra-oidc/07-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 7: Code Review Fix Report

**Fixed at:** 2026-10-25T01:51:58Z
**Source review:** .planning/phases/07-reviewer-ui-and-entra-oidc/07-REVIEW.md
**Iteration:** 1

**Scope:** The user selected two findings, CR-01 and WR-01. They deferred WR-02 through WR-07 and IN-01 through IN-09 to follow-up work, and this pass leaves those untouched.

**Summary:**
- Findings in scope: 2
- Fixed: 2
- Skipped: 0
- Full suite after both fixes: 1394 passed, 0 failed, 0 skipped. The baseline was 1393; this pass adds one test.

## Fixed Issues

### CR-01: Queue list shows only `open` items, so claimed items (including the reviewer's own) disappear

**Files modified:** `src/Olive.Host/Pages/Review/Index.cshtml.cs`, `tests/Olive.Tests/ReviewerPagesListTests.cs`
**Commit:** 06cf906
**Applied fix:**
- The page now calls `QueueService.ListAsync` twice, once for `open` and once for `claimed`.
- Each call goes through `QueueSql.List`, which keeps the tenant predicate and drops expired open or claimed rows.
- The page merges the two results, orders them by `(CreatedAt, ItemId)` and takes `QueueService.DefaultLimit`.
- A null state filter was not an option. `ListAsync` treats null as `open`, and the SQL would return decided and withdrawn items for a null state.
- The old test `UI_02_claimed_items_are_absent_from_the_open_list` is replaced by `UI_02_claimed_items_are_listed_with_a_claimed_badge_and_decided_or_expired_items_are_not`. For both the claimant and another reviewer, it checks three things:
  - The claimed item is listed with a `claimed` badge, in creation order.
  - The open item is listed with an `open` badge.
  - Decided, expired-open and expired-claimed items are absent.
**Status:** fixed (logic change; a human should confirm the list semantics).

### WR-01: Dev picker is always registered, so any non-production deployment has an unauthenticated sign-in path, even with Entra configured

**Files modified:** `src/Olive.Host/Program.cs`, `tests/Olive.Tests/DevSignInTests.cs`, `tests/Olive.Tests/CredentialRouteMatrixTests.cs`, `tests/Olive.Tests/SentinelUiTests.cs`
**Commit:** 33efa88
**Applied fix:**
- `DevSignInGate` is now registered only when `oidc is null` (stub mode).
- The picker page already returns 404 when the gate is absent. With Entra configured, `/dev/sign-in` is therefore 404 on GET, and a POST never issues a reviewer cookie.
- The cookie login path was already `/auth/sign-in` in OIDC mode.
- The `X-Olive-Dev-Reviewer` header scheme and `FixtureReviewerIdentitySource` stay registered. D-02 and D-15 keep the header for the JSON API, and the D-10 stub freshness path reads the fixture source.
- FND-05 production behavior is unchanged: the fixture identity source is still `INonProductionOnly`.

**Test changes:**
- New test `DevSignInOidcModeTests.UI_01_dev_sign_in_is_not_reachable_when_oidc_is_configured`, run with the mock IdP. It checks four things:
  - The gate is not in DI.
  - GET `/dev/sign-in` is 404 and shows no picker.
  - POST `/dev/sign-in` is not admitted and sets no cookie.
  - An anonymous `/review` redirects to `/auth/sign-in`.
- `CredentialRouteMatrixTests`: the intended admission changed. The matrix host runs with OIDC configured, so the `/dev/sign-in` column now expects 404 for every credential (it was 200).
  - The matrix can no longer get the DevCookie credential from the picker. The test now mints it from server-side identity and tenant, as the picker does (`auth_source=stub`), and seals it with the host's own cookie `TicketDataFormat`.
  - The DevCookie and ExpiredCookie rows keep their meaning: a stub cookie sealed by the host key is admitted to `/review`, and an expired one is redirected to sign-in.
- `SentinelUiTests`: the dev-picker pages now run on a stub-mode host over the same database. The OIDC pages stay on the mock-IdP host, and the secret scans now cover both hosts' logs. Coverage is unchanged.
- `UiSurfaceTests` needed no change. The `/dev/sign-in` page is still compiled and routed, and only its handler refuses.

**Residual note (not in scope):** with Entra configured, a stub cookie is now only possible if someone holds the host's data-protection key. `DecisionFreshness` still treats `auth_source=stub` as Fresh. A follow-up could refuse stub cookies whenever OIDC is configured, as defense in depth.

---

_Fixed: 2026-10-25T01:51:58Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
