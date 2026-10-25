---
phase: 07-reviewer-ui-and-entra-oidc
reviewed: 2026-10-24T00:00:00Z
depth: standard
files_reviewed: 53
files_reviewed_list:
  - .env.example
  - Directory.Packages.props
  - src/Olive.Application/AuditHashCheck.cs
  - src/Olive.Application/IAuditStore.cs
  - src/Olive.Application/IReviewerIdentitySource.cs
  - src/Olive.Contracts/ProblemReasons.cs
  - src/Olive.Domain/OidcClientSecret.cs
  - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
  - src/Olive.Host/Auth/DecisionFreshness.cs
  - src/Olive.Host/Auth/DevSignInGate.cs
  - src/Olive.Host/Auth/DraftStore.cs
  - src/Olive.Host/Auth/ReviewerClaims.cs
  - src/Olive.Host/Auth/ReviewerCookie.cs
  - src/Olive.Host/Auth/ReviewerOidc.cs
  - src/Olive.Host/Endpoints/ApprovalExecution.cs
  - src/Olive.Host/Endpoints/QueueEndpoints.cs
  - src/Olive.Host/Olive.Host.csproj
  - src/Olive.Host/Pages/Account/Failed.cshtml
  - src/Olive.Host/Pages/Account/Failed.cshtml.cs
  - src/Olive.Host/Pages/Account/SignIn.cshtml
  - src/Olive.Host/Pages/Account/SignIn.cshtml.cs
  - src/Olive.Host/Pages/Account/SignOut.cshtml
  - src/Olive.Host/Pages/Account/SignOut.cshtml.cs
  - src/Olive.Host/Pages/Account/SignedOut.cshtml
  - src/Olive.Host/Pages/Account/SignedOut.cshtml.cs
  - src/Olive.Host/Pages/Dev/SignIn.cshtml
  - src/Olive.Host/Pages/Dev/SignIn.cshtml.cs
  - src/Olive.Host/Pages/Review/Audit.cshtml
  - src/Olive.Host/Pages/Review/Audit.cshtml.cs
  - src/Olive.Host/Pages/Review/Index.cshtml
  - src/Olive.Host/Pages/Review/Index.cshtml.cs
  - src/Olive.Host/Pages/Review/Item.cshtml
  - src/Olive.Host/Pages/Review/Item.cshtml.cs
  - src/Olive.Host/Pages/Review/ReviewerPageModel.cs
  - src/Olive.Host/Pages/Shared/Display.cs
  - src/Olive.Host/Pages/Shared/FlashMessages.cs
  - src/Olive.Host/Pages/Shared/ForbiddenViewModel.cs
  - src/Olive.Host/Pages/Shared/TimeText.cs
  - src/Olive.Host/Pages/Shared/_Forbidden.cshtml
  - src/Olive.Host/Pages/Shared/_Layout.cshtml
  - src/Olive.Host/Pages/_ViewImports.cshtml
  - src/Olive.Host/Pages/_ViewStart.cshtml
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/OidcConfig.cs
  - src/Olive.Host/Startup/SecurityHeaders.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Host/wwwroot/olive.css
  - src/Olive.Integrations.Entra/EntraClaimsMapper.cs
  - src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs
  - src/Olive.Persistence/AuditSql.cs
  - src/Olive.Persistence/PostgresAuditStore.cs
findings:
  critical: 1
  warning: 7
  info: 9
  total: 17
status: issues_found
---

# Phase 7: Code Review Report

**Reviewed:** 2026-10-24
**Depth:** standard
**Files Reviewed:** 53
**Status:** issues_found

## Summary

I reviewed the Phase 7 reviewer UI (Razor Pages), the Entra OIDC integration, the decide-time freshness and draft store, the startup guards, and the read-only audit store. The security-critical parts mostly hold up:
- Tokens are not saved and only Olive claims reach the cookie.
- Return URLs are checked with `Url.IsLocalUrl`.
- Failure pages are static.
- A re-challenge checks `auth_time` against the time the challenge was issued, and it cannot switch identity.
- Drafts are oid- and item-bound and single-use.
- Every audit SQL statement keeps the tenant predicate.

Defects found:
- **Functional defect (BLOCKER):** the queue list hides claimed items, so a reviewer cannot find their own claimed work again.
- **Unauthenticated sign-in path:** the dev picker is always registered. In any non-production deployment, including one with Entra configured, it is an unauthenticated sign-in path, and stub sessions skip decide-time freshness entirely.
- **Production cannot start:** the production host cannot start from `Program.cs` as composed. The UI-01 production checks are never reached in the real composition.
- **Audit page gaps:** the audit page omits decision details for rejected items, and its 404 handling is inconsistent with the item page.

The three executor-reported items are covered by WR-02 (production start), WR-04 (Audit 404) and IN-03 (log filters).

## Narrative Findings (AI reviewer)

## Critical Issues

### CR-01: Queue list shows only `open` items, so claimed items (including the reviewer's own) disappear

**Resolution:** FIXED in commit 06cf906 (see 07-REVIEW-FIX.md).

**File:** `src/Olive.Host/Pages/Review/Index.cshtml.cs:19`
**Issue:** `queue.ListAsync(Reviewer!, null, "open", null, ct)` passes `state = "open"`. `QueueSql.List` applies it as an exact match (`state = @state`). UI-SPEC section `/review` says the State column shows badge text "`open`, `claimed`", so the list must include claimed items. As written:
- Once a reviewer claims an item, it disappears from every reviewer's queue page, the claimant's included.
- The only way back to it is the browser history or a typed URL.
- A claimed item that nobody can find stays claimed until it expires, which blocks the approval path for that proposal.

The `State` badge column in `Index.cshtml:45` can only ever show `open`, which is dead UI.
**Fix:** List open and claimed items. Either make two calls and merge them ordered by `CreatedAt`, or add a service-level "active" filter:
```csharp
var open = await queue.ListAsync(Reviewer!, null, QueueStates.Open, null, ct);
var claimed = await queue.ListAsync(Reviewer!, null, QueueStates.Claimed, null, ct);
Items = (open, claimed) switch
{
    (QueueListResult.Ok o, QueueListResult.Ok c) => o.Items.Concat(c.Items).OrderBy(i => i.CreatedAt).Take(QueueService.DefaultLimit).ToList(),
    _ => throw new InvalidOperationException("Unknown list result."),
};
```
Add a test: after claiming, the item still appears on `/review` with the `claimed` badge.

## Warnings

### WR-01: Dev picker is always registered, so any non-production deployment has an unauthenticated sign-in path, even with Entra configured

**Resolution:** FIXED in commit 33efa88 (see 07-REVIEW-FIX.md).

**File:** `src/Olive.Host/Program.cs:142`, `src/Olive.Host/Pages/Dev/SignIn.cshtml.cs:12-43`, `src/Olive.Host/Auth/DecisionFreshness.cs:40-50`
**Issue:**
- `builder.Services.AddSingleton<DevSignInGate>()` runs unconditionally, and `/dev/sign-in` is `AllowAnonymousToFolder`.
- In `nonprod` with a full Entra configuration, anyone who can reach the host can POST a fixture `reviewerId` and get a reviewer cookie. Antiforgery does not help, because the GET hands out the token.
- `DecisionFreshness.ResolveAsync` then returns `Fresh` for `auth_source=stub` with no freshness or re-authentication at all.
- The result: an anonymous caller can claim and approve real queue items in that deployment. This goes against the core value ("approved by a named reviewer").
- The Program.cs comment on line 107 ("absent settings leave the dev picker as the only sign-in path") implies the picker is meant as a fallback when Entra is absent. The code keeps it even when Entra is present.

**Fix:** Register the gate (and the fixture identity source) only where it is intended, for example:
```csharp
var oliveEnv = builder.Configuration["OLIVE_ENVIRONMENT"];
if (oidc is null && oliveEnv is "local" or "ci")
{
    builder.Services.AddSingleton<DevSignInGate>();
}
```
At minimum, do not register it when `oidc is not null`. Add a test that `/dev/sign-in` returns 404 when OIDC is configured.

### WR-02: The production host cannot start from `Program.cs`, and the UI-01 production checks are unreachable in the real composition

**File:** `src/Olive.Host/Program.cs:113,142`, `src/Olive.Host/Startup/StartupGuardFilter.cs:17-19`, `src/Olive.Host/Startup/StartupGuards.cs:120-123`
**Issue:**
- `Program.cs` unconditionally registers `DevSignInGate`, `FixtureReviewerIdentitySource` and the `DevReviewer` scheme. All three are refused in production (FND-05 and Q17).
- No production composition removes them. The only removal is in the test `StartupGuardTests.cs:208` (`RemoveAll<DevSignInGate>`).
- `CheckNoNonProductionComponents` (FND-05) runs before `CheckOidcConfiguration` (UI-01). So with `OLIVE_ENVIRONMENT=production`, the real host always fails with FND-05. The UI-01 checks for authority, https and the picker never run against the shipped composition. They are only exercised by tests that build their own service collections.

This assessment confirms the executor's report. The production refusal is consistent with Phase 1 being simulation-only, since the stub introspector and fixture resolvers are also refused. However, nothing proves that a correctly configured production build would pass UI-01, and DevSignInGate is redundant with FixtureReviewerIdentitySource as the refusal trigger.
**Fix:** Make the non-production registrations conditional on `OLIVE_ENVIRONMENT` (see WR-01), so the composition itself differs by environment. Add a host-level test that boots `Program` with `OLIVE_ENVIRONMENT=production` and a complete https OIDC configuration, and asserts that the only refusals are the documented Phase 1 ones (stub introspector and fixtures), not UI-01 or the picker. Alternatively, run `CheckOidcConfiguration` before FND-05, so a missing OIDC setting is reported deterministically.

### WR-03: Partial Entra configuration is silently ignored outside production, and the host falls back to the picker

**File:** `src/Olive.Host/Startup/OidcConfig.cs:15-18`
**Issue:** `OidcConfig.Read` returns `null` when any one of authority, client id or secret is missing. The host then starts with the dev picker as the only sign-in path and logs nothing. For example, an operator who sets authority and client id but misspells `OLIVE_OIDC_CLIENT_SECRET` in `nonprod` gets the weaker, unauthenticated picker with no signal. Only the production guard catches it, and production cannot start anyway (WR-02).
**Fix:** If any `OLIVE_OIDC_*` value is present, require all three and throw a `StartupGuardException("UI-01", "...incomplete...")` that names the missing setting. Treat only "all absent" as "Entra not configured".

### WR-04: Audit page returns a bare 404 and bypasses `QueueService`

**File:** `src/Olive.Host/Pages/Review/Audit.cshtml.cs:7,31,35-36,47,63`
**Issue:**
- `AuditModel` returns `NotFound()`, an empty-body 404 with no layout. `ItemModel.NotFoundPage()` renders the shared D-19 "Item not found" panel.
- Within the audit page, a missing item and another tenant's item are both a bare 404, so this does not create a cross-tenant oracle (the executor's concern is not confirmed). It is still inconsistent with D-19 and gives the reviewer a blank page.
- The class comment says "Data access goes through QueueService and IAuditStore only", but line 35 calls `IQueueStore.GetAsync` directly to get `ProposalId`. That skips any present or future checks in `QueueService` and contradicts the documented boundary.

**Fix:** Reuse the shared not-found rendering. For example, add `ItemNotFound` to `AuditModel` and render the same panel, or move `NotFoundPage()` into `ReviewerPageModel`. Have `QueueService` expose the proposal id, or reuse `ItemModel`'s `PayloadRef` parsing, instead of injecting `IQueueStore`.

### WR-05: Audit page never shows the decision for rejected items, or for approved items with no execution row yet

**File:** `src/Olive.Host/Pages/Review/Audit.cshtml.cs:57`, `src/Olive.Host/Pages/Review/Audit.cshtml:73-107`
**Issue:**
- `ShowChain` is true only when `item.Decision == "approve"`, and every decision field (Decision, Decided by, Rationale, Auth source, Decided at) is rendered from `Record.Chain`.
- `Record.Chain` comes from the `execution_chain` view. That view is driven by `execution_ledger ... where s.phase = 'started'`, so it has no row until execution starts.
- **Rejected items:** the section is omitted, so the reviewer, rationale and decision time never appear. This is despite the flash text `decided_reject`: "The decision is recorded in the audit history." UI-SPEC section 5 says the chain is shown for "decided action items", not only approved ones.
- **Approved items whose execution has not started** (for example a lock timeout or a fault in `ApprovalExecution`): the page shows only "No execution recorded yet." It does not show who approved or why.

**Fix:** Render the decision fields from `QueueItemView`/`QueueItemDetail` (`Decision`, `DecidedBy`, `DecidedAt`, `AuthSource`, and the rationale from the queue item). Show them for every decided item. Use `Chain` only for the policy decision and the execution fields.

### WR-06: Decide-time freshness trusts cookie groups and roles for Entra sessions, which contradicts the D-10 comments

**File:** `src/Olive.Host/Auth/DecisionFreshness.cs:8,54-68`, `src/Olive.Host/Pages/Review/Item.cshtml.cs:156`
**Issue:** The comments say "cookie roles are not trusted at decide time ... the identity is re-resolved". For `auth_source=entra`, `ResolveEntra` uses `user.ToReviewerContext()` (tenant and groups from the cookie) and checks only `auth_time`. Nothing is re-resolved. A role or group removed in Entra stays effective for up to the 5-minute window plus 60 s of skew. The window is small, but the code documents a stronger property than it implements. A future change to `MaxFreshnessMinutes` or to cookie lifetime handling could rely on the comment.
**Fix:** Correct the comments to say that for Entra, freshness is bounded by `auth_time` within the window and the claims are those issued at that authentication. Or re-resolve groups through a directory lookup at decide time if D-10 requires it.

### WR-07: The `Stale` re-challenge assumes the Entra scheme is registered, so a surviving Entra cookie causes a 500

**File:** `src/Olive.Host/Pages/Review/Item.cshtml.cs:158-171`
**Issue:** With `OLIVE_DATA_PROTECTION_PATH` set, cookies survive restarts. If the host restarts without the OIDC settings, an existing `auth_source=entra` cookie still authenticates. When `DecisionFreshness` returns `Stale`, `Challenge(props, ReviewerOidc.Scheme)` throws `InvalidOperationException` (no handler for scheme "Entra"). The result is an unhandled 500, and the draft stays orphaned in `DraftStore`.
**Fix:** In `ResolveAsync`, return `Denied` for an Entra session when the Entra scheme is not registered. Or check `IAuthenticationSchemeProvider.GetSchemeAsync(ReviewerOidc.Scheme)` before calling `drafts.Put` and `Challenge`, and redirect with a static flash code otherwise.

## Info

### IN-01: The global `DraftStore` capacity lets one reviewer evict everyone's drafts

**File:** `src/Olive.Host/Auth/DraftStore.cs:17,36-40`
**Issue:** `MaxEntries = 1000` applies across all users with oldest-first eviction. An authenticated Entra reviewer with a stale session can POST Decide repeatedly without following the redirect. Each POST adds an entry, which flushes other reviewers' drafts (they get `draft_unavailable`). The impact is limited to retyping a draft.
**Fix:** Add a per-oid cap (for example 5 entries per oid, evicting that oid's oldest), and keep the global cap as a backstop.

### IN-02: POST handlers return `Page()` in several paths, which contradicts the UI-SPEC interaction contract

**File:** `src/Olive.Host/Pages/Review/Item.cshtml.cs:113,125,130,143,148,195,204`
**Issue:** UI-SPEC says every POST "Never returns `Page()`". `RoleNotPermitted` is a documented exception (D-09). `TryGetReviewer()` failures and `NotFoundPage()` on POST also render directly, so a browser refresh re-POSTs the form. These paths are harmless, since nothing changes, but they are undocumented deviations.
**Fix:** Document these as accepted exceptions, or redirect to the GET page, which renders the same 403 and 404 bodies.

### IN-03: The log filters are code rules that a provider-specific configuration rule can override

**File:** `src/Olive.Host/Program.cs:21-25`
**Issue:** The filters do what the executor reports. `Microsoft.AspNetCore.Hosting.Diagnostics` is raised to Warning, which removes the request-start line that carries `?code=` on `/signin-oidc` and `?resume=` on the item page. The `OpenIdConnectHandler` is raised to Information, which removes the Trace/Debug "message received" line that carries the code. They are provider-less rules, though. A provider-specific setting such as `Logging__Console__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Debug` outranks them and turns the code logging back on. The filters also suppress all request-start and request-finished logs, which removes useful operational telemetry.
**Fix:** Add a startup guard that refuses any effective level below Warning or Information for these two categories. Or switch to `AddFilter<TProvider>` for each registered provider. Document the trade-off in `.env.example`.

### IN-04: Entra sign-out sends no `id_token_hint`

**File:** `src/Olive.Host/Pages/Account/SignOut.cshtml.cs:18`, `src/Olive.Host/Auth/ReviewerOidc.cs:37`
**Issue:** With `SaveTokens = false`, the end-session request has no `id_token_hint`, so Entra shows an account picker during sign-out. The local cookie is cleared first, so this is UX only.
**Fix:** Accept and document this, or store only the id_token's `sid` and send `logout_hint`.

### IN-05: The TempData cookie does not get the same hardening as the other cookies

**File:** `src/Olive.Host/Program.cs:127-132`
**Issue:** Antiforgery and the reviewer cookie use `__Host-` names with `SecurePolicy.Always`. The cookie TempData provider keeps its defaults (`SameAsRequest`, no `__Host-` prefix). It carries only static codes, so the risk is low.
**Fix:** `builder.Services.Configure<CookieTempDataProviderOptions>(o => { o.Cookie.Name = "__Host-olive-td"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.SameSite = SameSiteMode.Lax; });`

### IN-06: Two `ParseParameters` implementations render nested values differently

**File:** `src/Olive.Host/Pages/Review/Audit.cshtml.cs:71-85`, `src/Olive.Host/Pages/Review/Item.cshtml.cs:237-255`
**Issue:** The item page serialises nested values with `JsonSerializer.Serialize` (compact). The audit page uses `GetRawText()` (stored formatting). The same parameter can look different on the two pages, which UI-SPEC says should match ("key/value table as on the item page").
**Fix:** Extract a single shared helper in `Pages/Shared`.

### IN-07: The layout hard-codes the auth-source strings

**File:** `src/Olive.Host/Pages/Shared/_Layout.cshtml:4,30`
**Issue:** It uses `"entra"` and `"stub"` literals instead of `QueueStates.Entra` and `QueueStates.Stub`. If the constants change, the dev banner silently stops showing.
**Fix:** Use the constants (`@using Olive.Domain` in `_ViewImports.cshtml`).

### IN-08: The production guard does not require `OLIVE_OIDC_TENANT_ID`

**File:** `src/Olive.Host/Startup/StartupGuards.cs:93-124`, `src/Olive.Integrations.Entra/EntraClaimsMapper.cs:44`
**Issue:** The `tid` pin is optional. With a single-tenant authority, issuer validation pins the tenant already. Nothing stops a production operator from configuring `organizations` or `common` together with a custom issuer setup and no pin.
**Fix:** Require `OLIVE_OIDC_TENANT_ID` in production, or require the authority path to contain a GUID tenant.

### IN-09: The global Dapper setting is changed from a static constructor

**File:** `src/Olive.Persistence/PostgresAuditStore.cs:9`
**Issue:** `DefaultTypeMap.MatchNamesWithUnderscores = true` mutates process-wide state as a side effect of first use. Correct mapping then depends on which store was touched first.
**Fix:** Set it once at composition (persistence registration) instead of in each store's static constructor.

---

_Reviewed: 2026-10-24_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
