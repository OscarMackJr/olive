# Phase 8: Identity Review Queue Kind - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md. This log keeps the alternatives that were considered.

**Date:** 2026-10-24
**Phase:** 08-identity-review-queue-kind
**Areas discussed:** Shared /v1/queue/items path, Service-token auth and producer registry, Identity items for reviewers, Polling and withdraw details

---

## Shared /v1/queue/items path

| Option | Description | Selected |
|--------|-------------|----------|
| Dispatch by credential | Keep IR-1's paths exactly. The credential picks the producer endpoints or the reviewer endpoints, and create and withdraw are service-only | ✓ |
| Move reviewer API elsewhere | New prefix for the reviewer JSON API. Churns the Phase 5 and 7 tests and the matrix | |
| Ask bluto for a new prefix | IR-1 change, re-ratification, blocks the phase | |

**Producer GET:** Decided only, own items ✓ (alternatives: any state of its own items; you decide)
**Reviewer list:** Both kinds together, with a kind badge ✓ (alternatives: only kinds you can decide; separate tabs)
**Matrix:** Extend the Phase 7 credential×route matrix ✓ (alternative: a separate IDN-03 test class)

---

## Service-token auth and producer registry

| Option | Description | Selected |
|--------|-------------|----------|
| Bearer + new inbound verifier port | `IServiceTokenVerifier` with an INonProductionOnly fixture stub, separate from the agent-key introspector, fails closed with 503 | ✓ |
| Reuse key introspector | Mixes session keys and service tokens | |
| You decide | | |

**Producer source:** Source-controlled `config/producers.yaml` ✓ (alternatives: hardcode bluto; extend queue-kinds.yaml)
**Wrong caller on a producer route:** `403 principal_not_permitted` ✓ (alternative: 401)
**Unknown tenant in a valid token:** Refuse and fail closed with 403 and a static code ✓ (alternative: you decide)

---

## Identity items for reviewers

| Option | Description | Selected |
|--------|-------------|----------|
| Clear claim, back to open | Defer works like release plus a recorded decision event; any steward can claim next | ✓ |
| Keep claimant | Soft hold. Not in IR-1, and lease-like semantics conflict with Q7 | |

**Item view:** References and ruleset version only, no evidence link ✓ (alternative: clickable evidence link)
**Decision form:** Choices driven by the kind, plus IR-1's warning against record values; rationale required for all three ✓ (alternative: same form with generic text)
**Steward fixtures:** New fixtures plus the Entra role allowlist ✓ (alternative: give existing supervisors both roles)

---

## Polling and withdraw details

| Option | Description | Selected |
|--------|-------------|----------|
| Opaque token in decided_after | base64url encoding of (decided_at, item_id), with next_cursor in the response | ✓ |
| Two params | decided_after plus after_item; IR-1 doesn't name the second one | |
| Timestamp only | Ties can be skipped or repeated | |

**Poll fields:** Decision fields without rationale ✓ (alternative: include rationale)
**Defers in poll:** No, only final decisions ✓ (alternative: expose defer events)
**Withdraw a claimed item:** Yes, any undecided item; the claimant then gets 409 ✓ (alternative: open items only)
**Demo:** Yes, an end-to-end transcript test ✓ (alternative: tests only)
**Page size:** Fixed at 100, follow next_cursor ✓ (alternative: client limit with a cap)

---

## Claude's Discretion

- The dispatch mechanism (forwarding scheme or endpoint filters)
- The verifier fixture format and the producers.yaml schema
- Reason-code names not fixed by IR-1 or Phase 5
- The 0010 migration design (body_sha256, deferred event)
- How a defer is represented in the event table and on the audit page
- The reviewer payload endpoint for identity items
- Re-claim right after one's own defer
- Whether the cursor is HMAC-protected

## Deferred Ideas

- link_challenge (IR-1 §7.1)
- A clickable evidence link
- A client-chosen page size, and producer listing of non-decided states
- Live popeye tokens and the bluto registry entry
- Naming the role:identity_steward Entra group
- IR-1 §7 items 2–4
