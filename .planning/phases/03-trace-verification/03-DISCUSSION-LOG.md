# Phase 3: Trace Verification - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-10-22
**Phase:** 03-trace-verification
**Areas discussed:** Refusal response contract, Fail-closed signalling, Popeye token stub, Existing test migration

---

## Refusal response contract

### HTTP status for a newly stored trace refusal

| Option | Selected |
|--------|----------|
| 201 Created (Recommended) | ✓ |
| 200 OK (match Liam S-OL-1) |  |

**User's choice:** 201 Created with Location

### Refusal body

| Option | Selected |
|--------|----------|
| state + refusal_reason (Recommended) | ✓ |
| Match Liam: status + reason |  |

**User's choice:** {proposal_id, state:"refused_policy", refusal_reason}; refusal_reason omitted when null

### Replay of a refused run

| Option | Selected |
|--------|----------|
| 200 with same refusal body (Recommended) | ✓ |
| Re-verify on replay |  |

**User's choice:** 200 with the same refusal body; no re-verification

### Expose on GET /v1/proposals/{id}

| Option | Selected |
|--------|----------|
| Yes, plus trace fields (Recommended) | ✓ |
| Only refusal_reason |  |
| Neither for now |  |

**User's choice:** Yes: refusal_reason, trace_verification, trace_schema_version (optional, additive)

---

## Fail-closed signalling

### Public 503 reason code

| Option | Selected |
|--------|----------|
| One generic code (Recommended) | ✓ |
| Two codes |  |

**User's choice:** One generic code: trace_verification_unavailable

### Which failures alarm

| Option | Selected |
|--------|----------|
| Credential/registration only (Recommended) | ✓ |
| Every fail-closed 503 |  |

**User's choice:** Credential/registration only (hometown/popeye 401/403, popeye refusal, token claim mismatch, token outliving key, malformed resolver response); plain outage/timeout logs Warning

### Alarm mechanism

| Option | Selected |
|--------|----------|
| IOperationalAlarm port + Critical log (Recommended) | ✓ |
| Port + log + health signal |  |

**User's choice:** IOperationalAlarm port in Application; Host impl logs structured Critical event with stable alarm code, no secrets; tests inject recording alarm

### Retry hint on 503

| Option | Selected |
|--------|----------|
| retryable: true + Retry-After (Recommended) | ✓ |
| retryable: true only |  |
| No hint |  |

**User's choice:** retryable: true in problem body + short Retry-After header

---

## Popeye token stub

### Undefined popeye issuance interface

| Option | Selected |
|--------|----------|
| Olive proposes it, marked ASSUMED (Recommended) | ✓ |
| Block until popeye specs it |  |

**User's choice:** Olive proposes ITokenIssuer.IssueAsync(SessionKey) -> {token, tenant_id, delegated_agent_id, key_id, expires_at} | refusal, marked ASSUMED, documented in contracts/trace-resolution/README.md, hand-off item for popeye owner before LIVE-02

### Token before trace-cache lookup

| Option | Selected |
|--------|----------|
| Always token first (Recommended) | ✓ |
| Skip token on trace-cache hit |  |

**User's choice:** Always obtain (cached) token first, then trace cache/resolver, then checks

### Timing and bounds

| Option | Selected |
|--------|----------|
| 5 s / 30 s / 10k (Recommended) | ✓ |
| Hard-coded constants, no new config |  |
| Let me specify |  |

**User's choice:** Resolver timeout 5 s, overridable via OLIVE_TRACE_TIMEOUT_MS; token refresh skew 30 s before earlier of token/key expiry; each cache capped at 10,000 entries, clear-on-full

### Introspection caller-rejected outcome

| Option | Selected |
|--------|----------|
| Yes, add it in Phase 3 (Recommended) | ✓ |
| No, issuance only |  |

**User's choice:** Add in Phase 3: popeye 401/403 on introspect -> 503 + alarm

---

## Existing test migration

### Existing suite vs mandatory verification

| Option | Selected |
|--------|----------|
| Factory auto-fixture mode (Recommended) | ✓ |
| Rewrite with explicit fixtures |  |

**User's choice:** OliveApiFactory default auto-fixture mode (test-only, hash via production Jcs); Phase 3 tests opt into explicit fixtures

### Fixture location/shape

| Option | Selected |
|--------|----------|
| contracts/trace-resolution/ pairs (Recommended) | ✓ |
| Test-only fixtures |  |

**User's choice:** contracts/trace-resolution/ fixtures shaped like hometown ATE response, schema + valid/invalid, each paired with its proposal; test asserts fixture.proposal_hash == Jcs.ProposalHash(proposal)

### Fault production

| Option | Selected |
|--------|----------|
| Faulting test doubles (Recommended) | ✓ |
| Fault fixtures |  |

**User's choice:** Faulting test doubles (FaultingTraceResolver, FaultingTokenIssuer); production stub fixture-only

### Fixture resolver tenant scoping

| Option | Selected |
|--------|----------|
| No, return verbatim (Recommended) | ✓ |
| Yes, mimic hometown |  |

**User's choice:** No scoping; return fixture verbatim so check 3 is exercisable; README notes live hometown 404s cross-tenant

---

## Claude's Discretion

Research recommended defaults not discussed (port placement, migration 0006 design, TRC-09 nullable reading, non-agent 403 kept, schema_version loose pattern, sealed grant classes, IDN-08 evidence approach, no new packages) — see CONTEXT.md.

## Deferred Ideas

- Health/readiness degraded signal after an alarm.
- S-OL-1 status/field alignment — Phase 9 compatibility review.
