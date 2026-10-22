---
phase: 03-trace-verification
verified: 2026-10-22T00:00:00Z
status: passed
score: 5/5 roadmap success criteria verified (SC5 JSONL half deferred to Phase 6)
overrides_applied: 0
deferred:
  - truth: "Sentinel-key search covers the simulated executor JSONL output (IDN-08 / SC5, JSONL half)"
    addressed_in: "Phase 6"
    evidence: "ROADMAP Phase 6 'Carry-over from Phase 3 (IDN-08)': extend the sentinel search to the JSONL output via SentinelScan.AddSource. JSONL output does not exist before Phase 6."
---

# Phase 3: Trace Verification Verification Report

**Phase Goal:** Only a proposal whose trace resolves to the same tenant, agent, run and content hash can proceed. Every resolver or auth failure fails closed, and the session key provably reaches only popeye.
**Status:** passed (with warnings and one known partial)
**Re-verification:** No, initial verification

## Success Criteria

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Matching fixture accepted with `trace_verification=fixture` and `trace_schema_version`. Mismatches stored as `refused_policy` with the four reason codes, in order | VERIFIED | `TraceVerifier.Check` applies run, tenant, agent, hash in that order. `ProposalEndpoints.PostAsync` passes source and schema version into `InsertAsync` or `InsertRefusedAsync`. Tests: `TRC_03_check_order_run_before_tenant_before_agent_before_hash`, `TraceVerifiedEndpointTests` and `TraceRefusalEndpointTests`. |
| 2 | Unknown trace gives 422 `trace_not_found` retryable and stores nothing. Retry after fixture appears is accepted. Positives cached by (tenant, trace) | VERIFIED | `TraceVerdict.NotFound` maps to `ProblemResults.Retryable(422)` before any store call. Test `TRC_02_retry_with_same_run_id_after_fixture_appears_is_accepted_because_not_found_is_never_cached`. `CachingTraceResolver` caches only `Found`. |
| 3 | Resolver unavailable or timeout gives 503 with nothing stored. Hometown or popeye 401/403 gives 503 plus an assertable alarm | VERIFIED | Timeout via linked CTS around the resolver. Distinct alarm codes for hometown 401/403, popeye 401/403, scope refusal and malformed response. Every non-Verified/Refused/NotFound verdict returns before the store. `TraceFailClosedTests` pass. |
| 4 | Tokens cached by (tenant, agent, key_id), never outliving the session key. Claim disagreement gives discard, 503 and alarm | VERIFIED | `ServiceTokenProvider` cache key, `Agrees()` check, `ExpiresAt > key.ExpiresAt` rejection (also on cache hit), `usableUntil` bounded by the earlier expiry minus skew. `IDN_05`, `IDN_06`, `IDN_07` tests pass. |
| 5 | Recording decorators show the session key reaching only popeye. Sentinel search at Trace level over JSONL, rows and logs finds nothing | PARTIAL (accepted) | `IDN_08_*` tests: `[OutboundPort]` architecture tests (only popeye ports take `SessionKey`, only hometown ports take grants), recording decorators, sentinel scan over responses (body and headers), Trace-level logs and stored rows on the verified, refused, 422, 503 and claims-mismatch paths. JSONL source is not covered because the simulated executor output does not exist yet. |

## Requirements Coverage

All 13 IDs appear in PLAN frontmatter (01: TRC-03, TRC-09; 02: TRC-02..08, IDN-05..07; 03: TRC-01, TRC-06, TRC-09, IDN-05; 04: TRC-03..06; 05: TRC-02, TRC-07, TRC-08, IDN-06, IDN-07; 06: IDN-08) and in REQUIREMENTS.md. There are no orphans.

| ID | Status | Evidence |
|----|--------|----------|
| TRC-01 | SATISFIED | `contracts/trace-resolution/` fixtures, `TRC_01_*` schema tests, `FixtureTraceResolver` |
| TRC-02 | SATISFIED | 422 path and retry test |
| TRC-03 | SATISFIED | `trace_unresolved` refusal, `trace_schema_version` stored, migration 0006, `InsertRefusedAsync` |
| TRC-04 | SATISFIED | tenant mismatch refusal |
| TRC-05 | SATISFIED | agent mismatch compared to the token's `delegated_agent_id` (ordinal) |
| TRC-06 | SATISFIED | `Jcs.ProposalHash`, and the fixture hash equality test |
| TRC-07 | SATISFIED | 503 plus alarms, nothing stored |
| TRC-08 | SATISFIED | `CachingTraceResolver` positive-only; `CachingTraceResolverTests` |
| TRC-09 | SATISFIED | migration 0006 CHECK on `fixture` or `hometown`; the production guard bars the fixture resolver |
| IDN-05 | SATISFIED | `ITokenIssuer` port and `StubTokenIssuer` |
| IDN-06 | SATISFIED | cache key and expiry bounds |
| IDN-07 | SATISFIED | claims mismatch gives discard, alarm and 503 |
| IDN-08 | PARTIAL | See below |

## IDN-08 partial: acceptable, not a gap

The JSONL half is deferred to Phase 6, and Phase 6 carries it explicitly. The roadmap carry-over line and the 03-06 SUMMARY caveat both record it. The JSONL file cannot exist before the simulated executor does. `SentinelScan.AddSource` is already in place for the Phase 6 hook. The risk is that Phase 6 forgets it. The ROADMAP carry-over line mitigates this, but the Phase 6 plan must include the task. Phase 3 does not mark IDN-08 satisfied: requirements-partial is recorded. Keep IDN-08 as Pending (not Complete) in REQUIREMENTS.md until Phase 6. Port-level capture is not wire-level capture; wire-level is LIVE-03, which the summary records.

## Code review warnings weighed against goal and locked decisions

No warning lets an unverified proposal be stored. All non-Verified/Refused verdicts return before the store, so "fail closed" in the sense of "no row, no acceptance" holds in every case.

| ID | Classification | Assessment |
|----|----------------|------------|
| WR-02 | WARNING | A thrown exception from the resolver or issuer returns 500 `internal_error`, not 503 `trace_verification_unavailable` with retryable. Nothing is stored, so it fails closed. It deviates from D-06 and D-09 (status and retryable contract). Reachable only via future live adapters, because the current stubs return typed results. Fix before live adapters (Phase 9 or LIVE work). |
| WR-03 | WARNING | A duplicate key in a resolver response yields `KeyNotFoundException`, which becomes 500 with no malformed alarm. Fails closed (nothing stored). The parser's "never throws" contract is broken. It is reachable only through fixture content now, but a live response would hit it. Cheap to fix. |
| WR-01 | WARNING | There is no timeout on the token-issuance call, only on the resolver call. TRC-07 and SC3 say "resolver unavailable or times out", which is met. A hung popeye would hold the request open, which is a liveness weakness, not an acceptance path. Does not break a locked decision (D-09 concerns the resolver). |
| WR-05 | WARNING (spec-conformant) | The cache is keyed by (tenant, trace) exactly as TRC-08 and D16.4 specify, so it is not a defect against the requirement. An agent-keyed cache would be safer (agent B in the same tenant skips hometown's authorization, and gets an existence oracle through `trace_agent_mismatch`). Raise it with the requirement owner; it needs a TRC-08 change, not a code-only fix. |

None of these is a BLOCKER. They should go into the follow-up backlog, with WR-02 and WR-03 at highest priority.

## Deviation: TRC_01_null_session_key_returns_503 not written

Accepted. `AgentKeyHeader.TryRead` returning null is checked in the endpoint (`TraceUnavailable()`), but introspection runs first through the same header, so a null key cannot reach that code over HTTP. The branch is defensive and fails closed. A unit-level test is optional.

## Behavioral spot-checks

`dotnet test tests/Olive.Tests --filter` over Trace, Containment, ServiceToken, StubTokenIssuer, CachingTrace, Architecture and ProposalStoreRefusal: 153 passed, 0 failed. The orchestrator reports a full suite pass of 735/735. No `HttpClient` exists in `src`.

## Anti-patterns

No TBD/FIXME/XXX markers were checked beyond the review. No blocker anti-patterns were found. The review (0 critical) is consistent with the code I read.

## Human verification

None required.

---

_Verified: 2026-10-22_
_Verifier: Claude (gsd-verifier)_
