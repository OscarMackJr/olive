---
phase: 08-identity-review-queue-kind
verified: 2026-10-25T00:00:00Z
status: gaps_found
score: 3/4 roadmap success criteria fully verified (SC-3 partial)
overrides_applied: 0
gaps:
  - truth: "bluto can poll decisions through the IR-1 mechanism (IRQ-03, SC-3) reliably, without losing decisions"
    status: partial
    reason: "CR-01 confirmed in code. DecideIdentityAsync stamps decided_at from TimeProvider after the row lock (PostgresQueueStore.cs ~line 354) but before commit, under READ COMMITTED. The poll cursor is (decided_at, item_id) keyset. A decide that commits after a later-stamped decide is skipped for good once bluto has advanced its cursor past it. Same effect from multi-instance clock skew. Silent loss on the feed bluto relies on."
    artifacts:
      - path: "src/Olive.Persistence/PostgresQueueStore.cs"
        issue: "decided_at stamped pre-commit"
      - path: "src/Olive.Persistence/QueueSql.cs"
        issue: "keyset poll predicate has no settle window or visibility guard"
    missing:
      - "Commit-order-safe cursor (settle window, advisory-lock serialization of poll vs decide, or a commit-ordered feed)"
      - "Testcontainers test: hold decide tx open across a poll, assert the decision appears on a later poll"
human_verification: []
---

# Phase 8: Identity Review Queue Kind Verification Report

**Phase Goal:** The shared queue serves a second consumer: bluto (stub traffic) files identity_review items under IR-1, reviewers decide them, bluto reads outcomes back over a service-only path.
**Status:** gaps_found (one blocker, CR-01)
**Re-verification:** No

## Independent checks run

- `dotnet test --filter "IdentityReview | CredentialRouteMatrix | ServiceToken"`: 254 passed, 0 failed. The orchestrator reports the full suite at 1688 passed.
- Read the code for the decide, poll and defer paths in `PostgresQueueStore.cs`.
- Checked the 08-03 and 08-08 SUMMARY deviations against CONTEXT D-02, D-05 and D-07.

## Success criteria

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Stub bluto token creates identity_review/candidate_pair; `tenant_id`, `producer`, `link_challenge` bodies rejected | VERIFIED | Create is covered by the green targeted tests, with registry-derived producer/tenant (D-06/D-08). Review confirms producer and tenant predicates in all new SQL, and create is idempotent via ON CONFLICT. |
| 2 | Reviewer can accept, reject or defer; decision in history | VERIFIED | `DecideIdentityAsync` handles accept, reject and defer. Defer writes a `deferred` event and returns the item to `open` (D-09). The decided event is written. The role gate is checked at decide time. |
| 3 | bluto polls decisions via IR-1 and withdraws own items | PARTIAL, blocker | Poll and withdraw exist and are tested for the sequential case. Withdraw races correctly under FOR UPDATE. The poll can permanently skip a concurrently committed decision (CR-01). |
| 4 | Agent and user credentials refused on the service path; proposal endpoint refuses service credentials; no live bluto traffic | VERIFIED, with accepted deviation | The matrix pins all pairings. The service token on `/v1/proposals` gives 401 key_inactive, not D-07's 403. SC-4 and IDN-03 require "refused", and 401 or 403 satisfies this. The stub verifier is `INonProductionOnly` and barred in production by FND-05. |

## Requirements coverage

All five IDs appear in plan frontmatter and in REQUIREMENTS.md. None is orphaned.

| Req | Plans | Status |
|-----|-------|--------|
| IRQ-01 | 01, 04, 08 | SATISFIED |
| IRQ-02 | 01, 05, 07, 08 | SATISFIED |
| IRQ-03 | 01, 06, 08 | PARTIAL: CR-01 |
| IRQ-04 | 02, 03, 08 | SATISFIED (stub token, no live traffic) |
| IDN-03 | 02, 03, 08 | SATISFIED (refusal is 401 or 403 as the requirement allows; see WR-01) |

REQUIREMENTS.md still shows these as Pending. That is a tracking update for the orchestrator.

## Assessment of review findings and deviations

| Item | Classification | Rationale |
|------|----------------|-----------|
| CR-01 poll cursor skip | **GOAL GAP (BLOCKER)** | Breaks reliable decision readback, the core of SC-3 and IRQ-03. It is silent data loss. The window is small but real, and multi-instance skew widens it. It needs a fix and a concurrency test. |
| WR-01 credential cross-routing (401 vs 403) | **Accepted deviation, with a documentation caveat** | The requirement text (IDN-03, SC-4) says "refused", and a refusal occurs. D-07 specified 403 and D-05 forbade shared code paths, so this is a departure from locked decisions. 08-03 documented it, but CONTEXT was not amended. The 403 for an agent key on producer routes relies on a fixture cross-listing, so that evidence is fixture-derived. Either amend D-05/D-07 or add a credential discriminator with popeye before real traffic. This is not a blocker while traffic is stub-only. |
| WR-02 non-steward claim lock | **Non-goal quality issue, should fix soon** | It does not defeat any success criterion. It is an availability and abuse risk, and a role check at claim time is cheap. |
| WR-03 clock sources | **Non-goal quality issue** | It contributes to CR-01 (app-clock `decided_at`). Fix it together with CR-01. |
| WR-04 stale demo transcript | **Non-goal quality issue** | It weakens D-18 evidence only. Delete the transcript before the run. |
| 08-08 UserKey gets 404 on GET /v1/proposals/{id} and 200 on GET /v1/catalog | **Pre-existing behavior, outside the phase** | The non-agent check exists only in `POST /v1/proposals`. IDN-03's text concerns the service-caller path and the service credential on the proposal endpoint. Recommend a separate follow-up on user keys reading agent GET routes. |
| 08-08 dev reviewer header on GET /v1/queue/items returns reviewer list | **Accepted by design (D-02)** | The credential selects the view. Create and withdraw stay 403 for reviewers. |

## Anti-patterns

No TBD, FIXME or XXX debt markers were checked beyond the review; the review reports none. No stub implementations were found in the paths read.

## Gaps summary

One blocker: CR-01 (IRQ-03). Everything else meets the roadmap contract. WR-01 is a recorded deviation from D-05/D-07 and should be ratified or fixed. WR-02 to WR-04 are recommended follow-ups. Close CR-01 with `/gsd-plan-phase --gaps`, and fold WR-03 into the same plan.

_Verifier: Claude (gsd-verifier)_
