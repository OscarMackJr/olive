---
phase: 08-identity-review-queue-kind
plan: 08
subsystem: testing
tags: [matrix, demo, evidence]
requires: [08-06, 08-07]
provides:
  - 72-cell credential x route matrix (9 credentials x 8 routes) including producer routes and UserKey
  - Demo=identity transcript test and build/demo-identity-review.sh
  - 08-BLUTO-ADDITIVE-FIELDS.md note for the bluto owner
  - IDN-08 sentinel test for service token and defer rationale
affects: []
key-files:
  created:
    - tests/Olive.Tests/IdentityReviewDemoTests.cs
    - build/demo-identity-review.sh
    - .planning/phases/08-identity-review-queue-kind/08-BLUTO-ADDITIVE-FIELDS.md
  modified:
    - tests/Olive.Tests/CredentialRouteMatrixTests.cs
    - tests/Olive.Tests/SecretLeakageTests.cs
    - contracts/service-token/README.md
key-decisions:
  - "Matrix expectations are written from the D-04 admission table; two cells pin pre-existing behavior (below) rather than changing handlers"
requirements-completed: [IRQ-01, IRQ-02, IRQ-03, IRQ-04, IDN-03]
completed: 2026-10-25
---

# Phase 8 Plan 08: Matrix, demo and evidence Summary

The credential matrix now covers every credential against agent, queue (reviewer and producer) and browser routes with a service-verifier call-count check, an HTTP demo writes a value-free identity-review transcript, and one note lists every additive wire field for the bluto owner.

## Tasks

1. Matrix: `Cred.UserKey`, `Route.QueueCreate/QueueWithdraw/QueuePoll`, per-route method and body, 72 cells, service-verifier never called on proposals, catalog, review, dev - commit c58dbf3
2. `IdentityReviewDemoTests` (`Demo=identity`) and `build/demo-identity-review.sh`; transcript at `TestResults/identity-review-transcript.md` (nine steps, status codes and static reasons only; scanned for rationale, token, producer refs, reviewer id, logs) - commit in the demo commit after c58dbf3
3. `08-BLUTO-ADDITIVE-FIELDS.md`, service-token README note, `IDN_08_service_token_and_defer_rationale_never_appear_...` in `SecretLeakageTests` (Trace logs, responses, rows via SentinelScan, plus queue_item/queue_item_event text-column checks) - final task commit

Full suite: 1688 tests passed (Docker Testcontainers). `bash build/demo-identity-review.sh` exits 0.

## Deviations from Plan

**1. [Rule 1 - plan expectation vs actual behavior] UserKey on GET /v1/proposals and /v1/catalog is admitted** (404 and 200). The plan expected 403. This is the pre-existing behavior already documented in plan 03 (the non-agent 403 lives only in `POST /v1/proposals`). The matrix pins the actual values with a comment; handlers were not changed (out of scope, no Phase 8 change). Worth a follow-up decision: a user key can read agent GET routes.

**2. [Rule 1] DevHeader on the poll route is 200, not 403.** `GET /v1/queue/items` dispatches by credential (D-02), so a dev reviewer on the poll URL gets the reviewer list. It is admitted as a reviewer, not as a producer; create and withdraw remain 403 for reviewers.

**3. Sentinel coverage** was added as a new integration test in `SecretLeakageTests` rather than changing `SentinelScan.cs` (no change needed); the service token is a distinctive sentinel registered through the stub's dictionary constructor.

**4. [Environment]** `contracts/vendored/*` showed CRLF-modified after the base reset; restored LF bytes with `git show HEAD:...`, never committed.

## Known Stubs

None. `StubServiceTokenVerifier` remains the non-production popeye stand-in.

## Self-Check: PASSED

Files present, three task commits exist, full suite green (1688).
