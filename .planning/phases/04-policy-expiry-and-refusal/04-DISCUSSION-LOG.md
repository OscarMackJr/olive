# Phase 4: Policy, Expiry and Refusal - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md. This log preserves the alternatives considered.

**Date:** 2026-10-22
**Phase:** 04-policy-expiry-and-refusal
**Areas discussed:** Entitlement source (Q4), Rate limit semantics, Expiry inputs, Policy refusal reasons & events

---

## Entitlement source (Q4)

| Question | Options | Selected |
|----------|---------|----------|
| D§4 stop rule reading | Proceed with a directory port / Stop and escalate / Proceed with POL-02 [ASSUMED] | Proceed: directory port |
| Entitlement model | Catalog required_entitlement / Action-id sets / Purpose-scoped groups | Catalog required_entitlement |
| Agent entitlements | Agent allowlist file / Directory port too | Agent allowlist file |
| Directory failure | Split like trace (refusal vs 503) / Always stored refusal | Split like trace |
| on_behalf_of key | Opaque string, defer Q14 / Resolve Q14 now | Opaque string, defer Q14 |
| Directory cache | No cache in Phase 4 / Short TTL cache | No cache |

## Rate limit semantics

| Question | Options | Selected |
|----------|---------|----------|
| Window | Sliding window counting rows / Fixed buckets | Sliding, count rows |
| What counts | Anything not refused / Only pending or approved | Anything not refused |
| Scope | Per tenant / Global per agent | Per tenant |
| Outcome | Stored refused_policy / 429 not stored | Stored refused_policy (`rate_limited`) |
| Order | Last policy check / First policy check | Last policy check |

## Expiry inputs

| Question | Options | Selected |
|----------|---------|----------|
| Per-action maximum | Required catalog field / Optional, default 8 h | Required `max_expiry_seconds` |
| Default when the maximum is under 8 h | min(8 h, action max, key) / Reject config | min(8 h, action max, key) |
| Key expires_at cap | Silently cap / 400 if an explicit value exceeds the key | Silently cap |
| Contract | Optional integer field / Header | Optional integer field |

## Policy refusal reasons & events

| Question | Options | Selected |
|----------|---------|----------|
| Codes | not_in_catalog, purpose_not_permitted, entitlement_missing, rate_limited / Also keep rule IDs | Use this set |
| Evaluation | First failure wins / Evaluate all, report first | First failure wins |
| not_in_catalog placement | At the schema/catalog step / Inside the policy step | At the schema/catalog step |
| Event payload | Outcome + per-check results / Outcome + reason only | Outcome + per-check results |

## Claude's Discretion

- Lifecycle CAS guards: full transition table, unit-tested now. Only the edges out of proposed are exercised end to end.
- How the API-08 order test is built.
- Port, fixture and migration placement. Whether the policy evaluator is pure domain code.

## Deferred Ideas

- Directory caching and the live Entra adapter.
- The Q14 ruling, before Phases 5 and 7.
