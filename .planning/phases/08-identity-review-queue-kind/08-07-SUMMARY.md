---
phase: 08-identity-review-queue-kind
plan: 07
subsystem: ui
tags: [razor, ui, identity-review]
requires: [08-05]
provides:
  - Kind-driven decision form on /review/items/{id} (accept/reject/defer vs approve/reject)
  - Refs-only identity item view with static bluto evidence note
  - Kind badge and kind filter on /review; Decision block and defer rationale on audit page
affects: [08-08]
key-files:
  created:
    - tests/Olive.Tests/IdentityReviewPagesTests.cs
  modified:
    - src/Olive.Host/Pages/Review/Item.cshtml
    - src/Olive.Host/Pages/Review/Item.cshtml.cs
    - src/Olive.Host/Pages/Review/Index.cshtml
    - src/Olive.Host/Pages/Review/Index.cshtml.cs
    - src/Olive.Host/Pages/Review/Audit.cshtml
    - src/Olive.Host/Pages/Review/Audit.cshtml.cs
    - src/Olive.Host/Pages/Shared/FlashMessages.cs
    - tests/Olive.Tests/ReviewerPagesDecideTests.cs
    - tests/Olive.Tests/ReviewerPagesListTests.cs
requirements-completed: [IRQ-02]
completed: 2026-10-25
---

# Phase 8 Plan 07: Identity review reviewer pages Summary

A steward on the Phase 7 pages can open an identity item (references only), claim, defer, re-claim and accept it, with kind badges on the list and a full audit history including a Decision block.

## Tasks

All three tasks in one commit b2b976b (page, list, audit and tests share the same files and were verified together):

1. Item page: `Decisions` from the registry replaces `DecisionsSupported`; `IsIdentity`, `LacksRole`, `RefsOnly` flags; Stale branch loads the item and validates the decision against that kind's `Decisions`; flash codes `decided_accept`, `deferred`, `item_withdrawn`; `invalid_request` copy generalised.
2. List badge and Audit: `<span class="badge">` kind; Audit no longer injects `IQueueStore` (WR-04), reads `ProposalId` from the view, handles `NoPayload`, renders a Decision block for any decided item (WR-05) and defer rationales for role holders only.
3. `IdentityReviewPagesTests` (6 `IRQ_02_pages_*` tests): full claim/defer/claim/accept flow, non-steward notice and audit, list and filter, withdrawn flash, blank rationale and missing token, 403 role refusal without echo.

Full suite: 1574 passed, 0 failed (Docker Testcontainers).

## Deviations from Plan

**1. [Rule 3] /review had no kind filter.** The plan assumed existing kind/state filters; `Index` had none. Added an optional `?kind=` query filter (unregistered kind gives an empty list, not an error). No state filter was added (the page still merges open and claimed).

**2. [Rule 1] Existing list test.** `UI_02_claimed_items...` counted all `badge` spans; updated to count state badges and kind badges separately.

**3. SentinelUiTests / UiSurfaceTests not modified.** Sentinel coverage for the defer rationale is in `IdentityReviewPagesTests` (absent from item, list, non-steward audit and 403 bodies, and from the deferred-redirect page). A log-line scan for the defer sentinel was not added; the page reads the rationale from the form directly as before. UiSurfaceTests unchanged and passing.

**4. Seeded identity items have no `created` event**, so the history test asserts claimed, deferred, claimed, decided (4 rows).

**5. [Environment]** After the base reset, autocrlf rewrote `contracts/vendored/*` to CRLF (broke FND-03); restored LF bytes from HEAD with `git show`, not committed (git still shows them modified via autocrlf).

## Known Stubs

None.

## Self-Check: PASSED

IdentityReviewPagesTests.cs present; commit b2b976b exists; full suite green.
