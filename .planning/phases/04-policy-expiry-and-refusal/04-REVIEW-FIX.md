---
phase: 04-policy-expiry-and-refusal
fixed_at: 2026-10-23T00:00:00Z
review_path: .planning/phases/04-policy-expiry-and-refusal/04-REVIEW.md
iteration: 1
findings_in_scope: 4
fixed: 4
skipped: 0
status: all_fixed
---

# Phase 04: Code Review Fix Report

**Fixed at:** 2026-10-23
**Source review:** .planning/phases/04-policy-expiry-and-refusal/04-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 4 (WR-01 to WR-04; IN-01 and IN-02 are out of scope for `critical_warning`)
- Fixed: 4
- Skipped: 0

**Verification:** `dotnet build` passes with 0 errors. The full `dotnet test` run passes 901 of 901: the 889 tests from before these fixes plus 12 new ones. Two consecutive full runs passed and showed no flakes.

## Fixed Issues

### WR-01: Advisory-lock key is ambiguous and can collide across scopes

**Files modified:** `src/Olive.Persistence/ProposalSql.cs`, `tests/Olive.Tests/RateLimitTests.cs`
**Commit:** f62f8af
**Applied fix:** I added a `ProposalSql.RateLockKey` expression. It has the form `hashtextextended(tenant::text || ':' || length(agent)::text || ':' || agent || ':' || action, 0)`, and `LockRate` is built from it. The tenant uuid text has a fixed length and the agent id has a length prefix, so two different (tenant, agent, action) scopes can never encode to the same string. A comment records that a 64-bit hash collision only serializes two unrelated scopes and never skips a lock. The two lock-holding tests now use `ProposalSql.LockRate` instead of a hard-coded copy of the old key. The new theory `POL_03_rate_lock_key_is_unambiguous_across_scopes` covers `a|b`/`c` against `a`/`b|c` and the `:` cases. This is a code-only SQL change with no migration.

### WR-02: Event type for refused rows depends on whether `policyDetailJson` is null

**Files modified:** `src/Olive.Application/IProposalStore.cs`, `src/Olive.Persistence/PostgresProposalStore.cs`, `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `tests/Olive.Tests/TestDoubles.cs`, `tests/Olive.Tests/ApiProposalTests.cs`, `tests/Olive.Tests/RateLimitTests.cs`, `tests/Olive.Tests/ProposalStoreRefusalTests.cs`
**Commit:** f78cc9f
**Applied fix:** I added `public enum RefusalKind { Trace, Policy }` and made it an explicit parameter of `IProposalStore.InsertRefusedAsync`. The `policy_evaluated` event and the `trace_refused`/`policy_refused` choice now follow the kind, not whether the detail is null. A mismatched call throws `ArgumentException` before any database work: a `Policy` refusal without detail, a `Trace` refusal with detail, or an undefined enum value. The endpoint passes `RefusalKind.Policy` for policy refusals and `RefusalKind.Trace` for trace refusals. The new test is `POL_04_refusal_kind_and_detail_mismatch_is_rejected_and_nothing_is_stored`. `IProposalStore` is an internal application port, not a contract, so change control does not apply.

### WR-03: `DirectoryVerifier` reuses `TraceVerifierOptions.Timeout`

**Files modified:** `src/Olive.Application/DirectoryVerifier.cs`, `src/Olive.Host/Program.cs`, `.env.example`, `tests/Olive.Tests/OliveApiFactory.cs`, `tests/Olive.Tests/DirectoryVerifierTests.cs`, `tests/Olive.Tests/PolicyEndpointTests.cs`, `tests/Olive.Tests/StartupGuardTests.cs`
**Commit:** 50de0c3
**Applied fix:**
- **Options:** I added `DirectoryVerifierOptions(TimeSpan Timeout)` with the same 5 s default, and `DirectoryVerifier` now depends on it.
- **Setting:** `Program.cs` reads a new optional `OLIVE_DIRECTORY_TIMEOUT_MS` setting. It is validated at startup, and a bad value stops startup with the message `OLIVE_DIRECTORY_TIMEOUT_MS must be a positive integer.`. A shared `ReadTimeoutMs` helper now parses both settings, and the trace setting's error message is unchanged. The new variable is documented in `.env.example`, commented out.
- **Test factory:** `OliveApiFactory` passes the new setting through `UseSetting`, as it already did for the trace timeout.
- **Existing test changed:** the directory-fault tests in `PolicyEndpointTests` (including the Hang case) used `OLIVE_TRACE_TIMEOUT_MS=100` to shorten the directory timeout. They now use `OLIVE_DIRECTORY_TIMEOUT_MS=100`.
- **New tests:**
  - An options default and independence test.
  - `POL_02_valid_directory_timeout_is_applied_at_start_and_leaves_trace_timeout_alone`.
  - `POL_02_invalid_directory_timeout_refuses_start`, with 3 cases.
  - An extra assertion in `TRC_07_valid_trace_timeout_is_applied_at_start` that the directory timeout stays at 5 s.

### WR-04: Accepted proposal can be stored with `expires_at` in the past, and the unreachable guard hides a contract

**Files modified:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `src/Olive.Persistence/PostgresProposalStore.cs`, `tests/Olive.Tests/ExpiryTests.cs`, `tests/Olive.Tests/RateLimitTests.cs`
**Commit:** 1459f02
**Status:** fixed: requires human verification (logic change)
**Applied fix:**
- **Endpoint check:** the endpoint reads the injected clock once when it computes the expiry. If `key.ExpiresAt <= now`, it returns `401 key_inactive` with `WWW-Authenticate: Bearer` and stores nothing. This is the same reason and status the auth handler already returns for an expired key, so no new contract value was added.
- **Store guard:** the auth handler already rejects keys that have expired at authentication time. So this check covers the remaining window, where the key expires while the trace and directory calls are in flight. In `InsertEvaluatedAsync` I kept the existing `ActionType is null` guard as a deliberate invariant guard, and added an `ExpiresAt is null` guard next to it, because an accepted row with no expiry would never expire. Both throw `ArgumentException`, which matches the project's existing approach to invariant violations.
- **New tests:**
  - `POL_07_key_expired_during_request_is_401_key_inactive_and_nothing_stored`: a directory double advances the clock mid-request.
  - `POL_07_key_still_live_after_in_flight_delay_is_accepted_and_capped`: the positive control.
  - `POL_07_evaluated_insert_without_expiry_is_rejected_and_nothing_stored`.

**Not done, deliberately:**
- **`NewProposal.ActionType` stays nullable.** The record is shared with the trace-refusal, unknown-action and legacy insert paths. Splitting out a separate record type for the evaluated path would be a wider refactor than this finding needs. The guard is now documented as an invariant check instead.
- **No database CHECK constraint (`expires_at > created_at`).** It would need a new migration, so I left it out under change control.
- **Status for this case:** I kept 401 `key_inactive`, which is already part of the response taxonomy, rather than inventing a new reason. A human should confirm that 401 is the right status here; the alternative would be a stored refusal.

## Out of scope (not attempted)

- IN-01 (duplicated insert parameter object) and IN-02 (Trace-level logging of refusals) are Info findings, so `fix_scope: critical_warning` excluded them.

---

_Fixed: 2026-10-23_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
