---
phase: 03-trace-verification
fixed_at: 2026-10-22T19:38:47Z
review_path: .planning/phases/03-trace-verification/03-REVIEW.md
iteration: 1
findings_in_scope: 7
fixed: 6
skipped: 1
status: partial
---

# Phase 3: Code Review Fix Report

**Fixed at:** 2026-10-22T19:38:47Z
**Source review:** .planning/phases/03-trace-verification/03-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 7 (Critical/Warning; the five Info findings are out of scope)
- Fixed: 6
- Skipped: 1

**Verification:** I ran `dotnet test tests/Olive.Tests` (Docker/Testcontainers) after all fixes: 748 passed, 0 failed. On the first full run, `CatalogEndpointTests.API_10_non_invalid_operation_catalog_fault_refuses_with_static_message` failed once. It uses the same `StartupGuardTests.TryStartAsync` helper as the known intermittent startup-guard flake. It passed when its class was rerun alone, and the whole suite passed on a second full run. It is unrelated to these fixes. `contracts/vendored/*` was not touched.

## Fixed Issues

### WR-01: Token issuance (popeye) has no timeout; only the resolver call is bounded

**Files modified:** `src/Olive.Application/TraceVerifier.cs`, `tests/Olive.Tests/FaultingDoubles.cs`, `tests/Olive.Tests/TraceUnitDoubles.cs`, `tests/Olive.Tests/TraceVerifierTests.cs`, `tests/Olive.Tests/TraceFailClosedTests.cs`
**Commit:** eeb2d14
**Applied fix:** `ServiceTokenProvider.GetAsync` now runs under its own linked CTS with `options.Timeout`, separate from the resolver's budget. A timeout during issuance returns `Unavailable("token_issuer_timeout")` and raises no alarm. Client aborts still propagate. Tests: `HangingTokenIssuer` and `TokenFault.Hang` doubles, unit test `TRC_07_token_issuer_hang_times_out_to_unavailable_and_resolver_is_never_called`, and integration test `TRC_07_popeye_token_hang_times_out_to_503_without_alarm` (503, retryable, nothing stored, resolver never called).

### WR-02: Exceptions thrown by the resolver or issuer surface as 500 internal_error, not 503 retryable

**Files modified:** `src/Olive.Application/TraceVerdict.cs`, `src/Olive.Application/ServiceTokenProvider.cs`, `src/Olive.Application/TraceVerifier.cs`, `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `tests/Olive.Tests/FaultingDoubles.cs`, `tests/Olive.Tests/TraceUnitDoubles.cs`, `tests/Olive.Tests/TraceVerifierTests.cs`, `tests/Olive.Tests/TraceFailClosedTests.cs`
**Commit:** ad6ebd3
**Applied fix:**
- `ServiceTokenProvider` catches non-cancellation exceptions from `ITokenIssuer` and returns `TokenOutcome.Failed("token_issuer_exception", typeName)`.
- `TraceVerifier` catches non-cancellation exceptions from the resolver (`trace_resolver_exception`) and, as a backstop, from token acquisition. These catches apply only while the request token is not cancelled.
- `TraceVerdict.Unavailable` and `TokenOutcome.Failed` gained an optional `ExceptionType` (additive). It holds the type name only, never the message.
- The endpoint's Warning log now includes `exception_type=`. The response remains the shared 503 `trace_verification_unavailable` with `retryable: true` and `Retry-After: 5`, and nothing is stored. No alarm fires, because a thrown fault is not a credential or registration failure (D-07). `Olive.Application` stays logging-free.

Tests:
- Unit: `TRC_07_resolver_exception_is_unavailable_with_type_name_only_and_no_alarm` and `TRC_07_token_issuer_exception_is_unavailable_with_type_name_only_and_resolver_is_never_called`.
- Integration: `TRC_07_resolver_exception_gives_503_retryable_and_stores_nothing` and `TRC_07_popeye_token_exception_gives_503_retryable_and_stores_nothing`. Both assert that the exception type is logged, the exception message is not, and no Error-level log is written.
- `ResolverFault.Throw` was added to `TRC_07_503_body_and_headers_never_expose_the_cause`.

### WR-03: TraceResolutionResponse.Parse throws on duplicate keys despite its "never throws" contract

**Files modified:** `src/Olive.Integrations.Hometown/TraceResolutionResponse.cs`, `tests/Olive.Tests/TraceFixtureTests.cs`
**Commit:** dd53dfa
**Applied fix:** The field loop now records each name in a `HashSet` and returns `Malformed` when a name repeats. The completeness check uses `seen.Count`. `Str` uses `TryGetProperty`, so a missing field returns `Malformed` and cannot throw `KeyNotFoundException`. `Malformed` already maps to Unavailable plus the `trace_response_malformed` alarm in the verifier, which `TRC_07_malformed_is_unavailable_with_malformed_alarm` covers. Test: `TRC_07_parser_returns_malformed_for_duplicate_keys_and_never_throws` covers the reviewer's case (seven properties, `trace_id` twice, `created_at` missing) and a duplicate on top of a complete set.

### WR-04: Resolved trace is never bound to the requested trace_ref

**Files modified:** `src/Olive.Application/TraceVerifier.cs`, `tests/Olive.Tests/TraceVerifierTests.cs`
**Commit:** e90c611
**Status:** fixed: requires human verification (alarm choice)
**Applied fix:** A `Found` whose `Facts.TraceId` differs from the requested `traceRef` (ordinal comparison) is now treated as a protocol violation. The verdict is `Unavailable("trace_id_mismatch")` (503, nothing stored), and the existing `trace_response_malformed` alarm is raised before any of the four D17.3 checks run. No new alarm code was introduced. Test: `TRC_03_answer_for_a_different_trace_id_is_unavailable_with_malformed_alarm`.

Two things to confirm:
1. Is reusing `trace_response_malformed` (rather than a new code) the right alarm?
2. `CachingTraceResolver` still caches a mismatched `Found` under the requested key. This is now safe, because the verifier rejects it on every hit. It is a liveness issue only: that trace returns 503 until restart or until the cache clears. I left the cache unchanged to keep scope narrow, because the existing `CachingTraceResolverTests` deliberately use trace ids that do not match their facts.

### WR-06: Token provider returns already-expired (or within-skew) freshly issued tokens

**Files modified:** `src/Olive.Application/ServiceTokenProvider.cs`, `tests/Olive.Tests/ServiceTokenProviderTests.cs`
**Commit:** b9b6627
**Status:** fixed: requires human verification (scope narrowed)
**Applied fix:** On the fresh-issue path, if `min(grant.ExpiresAt, key.ExpiresAt) <= now`, the provider returns `TokenOutcome.Failed("token_already_expired")`. It raises no alarm and caches nothing, so hometown never receives a dead token and Olive's credentials are not wrongly blamed through the `trace_resolver_401` alarm.

I deliberately did not reject tokens that are still valid but within the 30 s skew. The existing test `IDN_05_near_expiry_token_is_used_once_and_not_cached` encodes the designed behaviour: a token with 20 s left is used once and not cached, which is enough for a resolver call bounded at 5 s. Tests: `IDN_06_freshly_issued_token_already_expired_fails_without_alarm_and_is_not_cached` (expiry at now and in the past) and `IDN_06_session_key_already_expired_at_issue_fails_without_alarm`.

### WR-07: Migration 0006 constraint breaks state transitions to refused_policy and can fail on existing data

**Files modified:** `src/Olive.Persistence/PostgresProposalStore.cs`, `src/Olive.Application/IProposalStore.cs`, `tests/Olive.Tests/ProposalStoreRefusalTests.cs`
**Commit:** 6c0fbd7
**Applied fix:** Following the orchestrator's guidance, this is a code-level guard only, with no schema change. `0006_trace_verification.sql` is untouched, no `0007` was added, and `MigrationRunnerTests`/`PrivilegeMatrixTests` are unchanged. `PostgresProposalStore.TransitionAsync` now throws `ArgumentException` before any database work when either `fromState` or `toState` is `refused_policy`. Refusals are written only by `InsertRefusedAsync` (D-05), and `refused_policy` is terminal. The `IProposalStore` comment documents this. Test: `TRC_03_transition_into_or_out_of_refused_policy_is_rejected_before_the_database`, which checks both directions are rejected, rows and refusal reasons are unchanged, and no events are written.

On the "existing data" half of the finding: no caller has ever called `TransitionAsync` into `refused_policy`, and there is no deployed database (Phase 1 is simulation-only, and every test database is migrated fresh). So no legacy `refused_policy` row with a NULL reason can exist for 0006 to trip on, and no backfill or `NOT VALID` change is needed. If a database populated outside Olive's code ever appears, a forward-only `0007` backfill would be the remedy.

## Skipped Issues

### WR-05: Trace cache is keyed by tenant only, so agents in the same tenant skip hometown authorization

**File:** `src/Olive.Application/CachingTraceResolver.cs:15-19`
**Reason:** Skipped on orchestrator/verifier instruction. The current cache key `(tenant_id, trace_id)` is exactly what requirement TRC-08 specifies, so adding `DelegatedAgentId` to the key changes a requirement and needs the requirement owner's agreement. Raise it with the TRC-08 owner (and note it for hometown, since live hometown authorization per service token is what the cache bypasses).
**Original issue:** The cache key is `(TenantId, traceId)`. Agent B in the same tenant gets agent A's cached `Found` without hometown ever seeing B's service token. B then receives a stored `refused_policy` row with `trace_agent_mismatch`, which confirms the trace exists and belongs to another agent: an authorization bypass and existence oracle.

---

_Fixed: 2026-10-22T19:38:47Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
