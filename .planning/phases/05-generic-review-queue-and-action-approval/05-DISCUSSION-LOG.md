# Phase 5: Generic Review Queue and Action Approval - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-23
**Phase:** 05-generic-review-queue-and-action-approval
**Areas discussed:** Reviewer surface, Dev reviewer identity, IR-1 mapping, Beat 4 demo

---

## Reviewer surface

| Option | Description | Selected |
|--------|-------------|----------|
| Internal JSON API | /v1/queue/items list/claim/release/decide behind a reviewer auth scheme; Phase 7 Razor uses the same app service | ✓ |
| App services only | Store and service driven by tests only; HTTP/UI in Phase 7 | |
| Minimal Razor now | Bare Razor pages now; blurs the Phase 7 boundary | |

| Option | Description | Selected |
|--------|-------------|----------|
| Hard-separated | Agent on reviewer paths → 403; reviewer on /v1/proposals → 403; tested both ways | ✓ |
| Scheme-only | Rely on different schemes (401) | |

| Option | Description | Selected |
|--------|-------------|----------|
| No, Phase 8 | Producer create/withdraw come with identity_review | ✓ |
| Yes, scaffold now | Generic create/withdraw now | |

| Option | Description | Selected |
|--------|-------------|----------|
| 409 with stable codes | already_claimed / not_claimant / decision_conflict / 200 same-decision / proposal_expired / 404 | ✓ |
| You decide | Planner picks codes | |

## Dev reviewer identity

| Option | Description | Selected |
|--------|-------------|----------|
| Map UPN→oid+tid | Fixture-backed IUserIdentityResolver; zero/multiple → refused | ✓ |
| Store oid+tid at submit | Resolve at submission and store | |
| Escalate first | Treat Q14 as a stop | |

| Option | Description | Selected |
|--------|-------------|----------|
| Dev auth handler + fixture | Header-identified reviewer looked up in source-controlled fixture; auth_source=stub | ✓ |
| Signed dev tokens | Tests mint HMAC/JWT | |

| Option | Description | Selected |
|--------|-------------|----------|
| Source-controlled YAML | Strict-parsed group→tenant map validated at startup | ✓ |
| You decide | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Kind registry role, wired now | required_role checked at decide time against stub groups | ✓ |
| Authenticated + tenant only | Role check entirely in Phase 7 | |

## IR-1 mapping

| Option | Description | Selected |
|--------|-------------|----------|
| olive:proposal/<id> both | subject_ref = payload_ref = olive:proposal/<id>; producer=olive | ✓ |
| Distinct payload ref | olive:proposal-review/<id> | |
| Legacy proposal:// / payload:// | Keep scaffold schemes | |

| Option | Description | Selected |
|--------|-------------|----------|
| On claim and decide | Commit expiry + withdrawal then 409; list read-only | ✓ |
| Also on list | List writes withdrawals | |
| Plus startup sweep | Add sweep | |

| Option | Description | Selected |
|--------|-------------|----------|
| Same path | Expiry wins over claim | ✓ |
| You decide | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Replace with v1 per IR-1 | New v1 contract + kind registry; delete v0 | ✓ |
| Keep v0, add v1 | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Item fields only | Refs, state, age; no proposal content | ✓ |
| Include proposal summary | Add action_type, on_behalf_of | |

## Beat 4 demo

| Option | Description | Selected |
|--------|-------------|----------|
| Reviewer-path detail | GET /v1/queue/items/{id}/payload on reviewer scheme | ✓ |
| Open GET to reviewers | Exception to separation on /v1/proposals/{id} | |

**Notes:** Conflict surfaced by Claude: payload_ref resolves through Olive, but GET /v1/proposals is agent-only under hard separation.

| Option | Description | Selected |
|--------|-------------|----------|
| xUnit demo test + transcript | Trait-tagged HTTP test on Testcontainers, markdown transcript, build/ runner | ✓ |
| PowerShell against a running host | Live, not CI-checked | |
| Both | | |

| Option | Description | Selected |
|--------|-------------|----------|
| Row + events + absence | Proposal row, proposal_event incl. policy_evaluated, queue_item count 0, reviewer list | ✓ |
| API responses only | | |

## Claude's Discretion

- Route shapes, DTO names, DevReviewer header name, fixture/tenant-map paths and schemas.
- queue_item / queue_item_event table design within the Phase 1 RLS/grant pattern, migration numbering after 0007.
- Extra reason-code names, the concurrent-claim proof style, project placement of the registry/service/store.

## Deferred Ideas

- Startup/timer sweep for expired items.
- Live IUserIdentityResolver adapter.
- IR-1 producer endpoints and identity_review kind (Phase 8).
