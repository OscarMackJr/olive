---
phase: 08-identity-review-queue-kind
plan: 05
subsystem: api
tags: [decide, defer, role-gate, reviewer-api]
requires: [08-01, 08-03, 08-04]
provides:
  - IQueueStore.DecideIdentityAsync (accept, reject, defer) and DecideResult.Deferred/Withdrawn, ReleaseResult.Withdrawn
  - QueueService identity_review branch (no identity check, no payload read, no execution)
  - PayloadResult.NoPayload, rationale-stripped events for reviewers lacking the kind role
  - ProblemReasons.ItemWithdrawn (409), ProblemReasons.NoPayload (404), ruleset_version on QueueItemDto
affects: [08-06, 08-07, 08-08]
key-files:
  created:
    - tests/Olive.Tests/IdentityReviewDecisionTests.cs
  modified:
    - src/Olive.Application/IQueueStore.cs
    - src/Olive.Application/QueueService.cs
    - src/Olive.Persistence/PostgresQueueStore.cs
    - src/Olive.Domain/QueueStates.cs
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Contracts/QueueDtos.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
    - src/Olive.Host/Pages/Review/Item.cshtml.cs
    - src/Olive.Host/Pages/Review/Audit.cshtml.cs
    - tests/Olive.Tests/QueueServiceDecideTests.cs
    - tests/Olive.Tests/IdentityReviewCreateTests.cs
key-decisions:
  - "Store takes optional TimeProvider (default System); decided_at for identity decisions is read after LockItem (Pitfall 7b); the now argument is kept in the signature but not used for the stamp"
  - "Defer detail JSON built with System.Text.Json (decision, auth_source, rationale); events API strips only the rationale key for reviewers without required_role"
  - "Service gate is now registry-driven (kind registered, decision in kind decisions, rationale, role); action_approval path unchanged"
requirements-completed: [IRQ-02]
completed: 2026-10-25
---

# Phase 8 Plan 05: Identity decide and defer Summary

A steward holding role:identity_steward can claim an identity item and accept, reject or defer it over the JSON API with the exact D-09 state shape; non-stewards and cross-kind pairings are refused, withdrawn items return 409 item_withdrawn, and the defer rationale is visible only to role holders.

## Tasks

1. Identity decide/defer in QueueService and PostgresQueueStore - commit 7fa6c07 (shared with task 2: Withdrawn results, NoPayload, events stripping all touch the same service, store and interface files)
2. Withdrawn handling, refs-only payload result, events rationale gating - commit 7fa6c07
3. Reviewer API mapping, ProblemReasons, TC-IRQ-04/05/06 HTTP tests - commit 00cff40

Full suite: 1568 tests green (Docker Testcontainers).

## Deviations from Plan

**1. [Rule 3] Page models needed new switch arms.** `Item.cshtml.cs` and `Audit.cshtml.cs` have throwing default arms; without arms an identity item page would 500 on `NoPayload`. Mapped `NoPayload` to the prior NotFound behaviour, `Withdrawn` to the `item_closed` flash, `Deferred` to `released`. Plan 07 owns the real identity page.

**2. Tasks 1 and 2 share one commit** (interdependent files). Tests for events/payload/withdrawn live in `IdentityReviewDecisionTests` rather than extra cases in `QueueEventsTests`/`QueuePayloadTests`; existing tests in those files remain green and unchanged.

**3. [Environment]** After the base reset `contracts/vendored/*` showed CRLF-modified; restored with `git checkout -- contracts/vendored/`, not committed.

## Notes

- Non-steward claim risk accepted (T-08-29): claim stays role-free; the claimant can release or bluto can withdraw.
- `ruleset_version` is now an additive optional wire field on `QueueItemDto` (null omitted); collect for the bluto owner in plan 08.
- Withdrawn detection in decide/release keys on kind identity_review; action_approval keeps Closed/ProposalExpired.

## Known Stubs

None.

## Self-Check: PASSED

IdentityReviewDecisionTests.cs present; commits 7fa6c07 and 00cff40 exist; full suite green.
