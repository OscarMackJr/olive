---
phase: 07-reviewer-ui-and-entra-oidc
plan: 05
subsystem: auth
tags: [oidc, entra, reauth, apr-03, ui-04, draft-store]
requires: [07-02, 07-04]
provides:
  - Entra decide-time freshness window (max 5 min, narrowing only)
  - Single-use oid-bound DraftStore and resume flow
  - auth_time evidence check on re-challenge callback
affects: [07-06]
key-files:
  created:
    - src/Olive.Host/Auth/DraftStore.cs
    - tests/Olive.Tests/DraftStoreTests.cs
    - tests/Olive.Tests/DecisionFreshnessEntraTests.cs
    - tests/Olive.Tests/OidcReauthTests.cs
  modified:
    - src/Olive.Host/Auth/DecisionFreshness.cs
    - src/Olive.Host/Auth/ReviewerOidc.cs
    - src/Olive.Host/Auth/ReviewerCookie.cs
    - src/Olive.Host/Pages/Review/Item.cshtml
    - src/Olive.Host/Pages/Review/Item.cshtml.cs
decisions:
  - Window config passed via DecisionFreshnessOptions registered in AddReviewerOidc, so Program.cs is untouched.
  - resume key read from Request.Query, not a bound handler argument.
metrics:
  completed: 2026-10-24
  tasks: 2
---

# Phase 7 Plan 05: Decide-time Entra re-authentication Summary

Stale Entra sessions (auth_time older than 5 min, missing, or future-dated) are re-challenged with prompt=login and max_age=0 on decide. The draft survives in a server-side single-use store, the returning token must prove a recent auth_time, and the resume GET only restores the form.

## Tasks

1. DraftStore, Entra freshness window, evidence check (commit df25f1c). 16 unit tests (7 DraftStore, 9 Entra freshness).
2. Re-challenge on decide, resume UX, mock-IdP proof (commit 444b0cd). 10 OidcReauth integration tests.

## Behaviour

- `DecisionFreshness`: `MaxFreshnessMinutes = 5`; configured value can only narrow it; auth_time missing/0/unparseable or more than 60 s in the future is Stale; no tenant claim is Denied.
- `ReviewerOidc.OnTokenValidated`: with `olive.reauth` set, auth_time must exist, be >= issued - 60 s and <= now + 60 s, otherwise `ctx.Fail("reauth_evidence_missing")` (redirect to /auth/failed, cookie untouched). Original `session_start` is preserved and a different oid fails closed.
- `Item` page: Stale arm validates, stores draft, challenges. `?resume=` restores draft only for the claimant oid; otherwise flash `draft_unavailable`. No DecideAsync in the GET.

## Deviations from Plan

- [Rule 3 - Blocking] mock-oauth2-server shows its login form for `prompt=login` even with interactiveLogin=false, so the test helper posts the login form (username) to the authorize URL to stand in for the user re-entering credentials. Test 1 still asserts the authorize URL carries prompt=login and max_age=0.
- Worktree base was corrected with `git reset --hard 9de3ca2` at start per the branch check. CRLF noise in contracts/vendored was restored with `git checkout`, never committed.

## Known Stubs

None.

## Threat Flags

None beyond the plan threat model (T-07-26..31 mitigated and tested).

## Verification

Full suite `dotnet test -c Release`: 1351 passed, 0 failed, 0 skipped.

## Self-Check: PASSED
