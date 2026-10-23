---
phase: 05-generic-review-queue-and-action-approval
plan: 07
subsystem: reviewer-api
tags: [que-09, apr-01, d-05, d-19]
requires: [05-03, 05-05, 05-06]
provides: [GET /v1/queue/items/{id}/payload, GET /v1/queue/items/{id}/events, QueueService.GetPayloadAsync, QueueService.EventsAsync]
affects: [plans 08-10 (claim, decide, withdraw tests can assert history over HTTP)]
key-files:
  created:
    - tests/Olive.Tests/QueuePayloadTests.cs
    - tests/Olive.Tests/QueueEventsTests.cs
  modified:
    - src/Olive.Application/QueueService.cs
    - src/Olive.Contracts/QueueDtos.cs
    - src/Olive.Host/Endpoints/QueueEndpoints.cs
requirements-completed: [QUE-09, APR-01]
completed: 2026-10-23
---

# Phase 5 Plan 07: Reviewer payload and audit history endpoints Summary

Role-gated reviewer payload read (required_role taken from the kind registry) and a tenant-scoped, seq-ordered, rationale-free per-item event history.

## Commits
- 81c3d48: service methods (PayloadResult/EventsResult unions), DTOs, both routes, payload tests
- 97e5c32: QUE_09 events tests

## Behaviour
- Payload: 404 item_not_found for missing or cross-tenant (identical bodies), 403 role_not_permitted when the reviewer lacks the registry role for the item's kind, otherwise exactly action_type, parameters, justification, on_behalf_of, expires_at, trace_ref (nulls omitted). Agent keys get 403 principal_not_permitted from the existing scheme handling.
- Events: any tenant-mapped reviewer; ordered by seq (store SQL already `order by seq`); DTO has no rationale member.
- No role string is hard-coded in src.

## Deviations from Plan
- [Rule 3] The Role test swaps the registry's role only after host startup, because the QUE-02 startup drift guard (catalog required_approval == registry role) rejects a registry with a different role at boot.
- Endpoint switches carry no discard arm as required; CS8509 is suppressed locally with a pragma and comment because the abstract record hierarchies cannot be proven closed by the compiler.
- Both tasks' production code landed in one commit (shared files); tests are split per task.

## Verification
Full suite 1024 passed, 0 failed. contracts/vendored restored to LF and never staged.

## Known Stubs
None.

## Self-Check: PASSED
