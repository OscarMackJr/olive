---
phase: 03-trace-verification
reviewed: 2026-10-22T00:00:00Z
depth: standard
files_reviewed: 56
files_reviewed_list:
  - sql/migrations/0006_trace_verification.sql
  - src/Olive.Application/CachingTraceResolver.cs
  - src/Olive.Application/IKeyIntrospector.cs
  - src/Olive.Application/IOperationalAlarm.cs
  - src/Olive.Application/IProposalStore.cs
  - src/Olive.Application/ITokenIssuer.cs
  - src/Olive.Application/ITraceResolver.cs
  - src/Olive.Application/IntrospectionCallerRejectedException.cs
  - src/Olive.Application/IntrospectionUnavailableException.cs
  - src/Olive.Application/OutboundPortAttribute.cs
  - src/Olive.Application/ServiceTokenGrant.cs
  - src/Olive.Application/ServiceTokenProvider.cs
  - src/Olive.Application/TraceVerdict.cs
  - src/Olive.Application/TraceVerifier.cs
  - src/Olive.Contracts/ProblemReasons.cs
  - src/Olive.Contracts/ProposalDtos.cs
  - src/Olive.Domain/TraceRefusalReasons.cs
  - src/Olive.Domain/TraceVerificationSources.cs
  - src/Olive.Host/Alarms/LoggingOperationalAlarm.cs
  - src/Olive.Host/Endpoints/ProposalEndpoints.cs
  - src/Olive.Host/Problems/ProblemExceptionHandler.cs
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Integrations.Hometown/FixtureTraceResolver.cs
  - src/Olive.Integrations.Hometown/Olive.Integrations.Hometown.csproj
  - src/Olive.Integrations.Hometown/TraceResolutionResponse.cs
  - src/Olive.Integrations.Popeye/StubTokenIssuer.cs
  - src/Olive.Persistence/PostgresProposalStore.cs
  - src/Olive.Persistence/ProposalSql.cs
  - tests/Olive.Tests/ApiProposalTests.cs
  - tests/Olive.Tests/ArchitectureTests.cs
  - tests/Olive.Tests/CachingTraceResolverTests.cs
  - tests/Olive.Tests/ContainmentTests.cs
  - tests/Olive.Tests/ContractSchemaTests.cs
  - tests/Olive.Tests/FaultingDoubles.cs
  - tests/Olive.Tests/IdentityEndpointTests.cs
  - tests/Olive.Tests/InMemoryLogCapture.cs
  - tests/Olive.Tests/ManualTimeProvider.cs
  - tests/Olive.Tests/MigrationRunnerTests.cs
  - tests/Olive.Tests/OliveApiFactory.cs
  - tests/Olive.Tests/PrivilegeMatrixTests.cs
  - tests/Olive.Tests/ProposalStoreRefusalTests.cs
  - tests/Olive.Tests/RecordingPorts.cs
  - tests/Olive.Tests/SecretTypeTests.cs
  - tests/Olive.Tests/SentinelScan.cs
  - tests/Olive.Tests/ServiceTokenProviderTests.cs
  - tests/Olive.Tests/StartupGuardTests.cs
  - tests/Olive.Tests/StubTokenIssuerTests.cs
  - tests/Olive.Tests/TestDoubles.cs
  - tests/Olive.Tests/TraceFailClosedTests.cs
  - tests/Olive.Tests/TraceFixtureLoader.cs
  - tests/Olive.Tests/TraceFixtureTests.cs
  - tests/Olive.Tests/TraceRefusalEndpointTests.cs
  - tests/Olive.Tests/TraceUnitDoubles.cs
  - tests/Olive.Tests/TraceVerifiedEndpointTests.cs
  - tests/Olive.Tests/TraceVerifierTests.cs
findings:
  critical: 0
  warning: 7
  info: 5
  total: 12
status: issues_found
---

# Phase 3: Code Review Report

**Reviewed:** 2026-10-22
**Depth:** standard
**Files Reviewed:** 56
**Status:** issues_found

## Summary

I reviewed the Phase 3 trace-verification changes (diff `43ab9a1..HEAD`): the token provider, trace verifier and cache, the fixture resolver and response parser, the endpoint wiring, migration 0006 and the persistence changes. Test files were read only for reliability and coverage gaps.

I found no path where a resolver or token failure stores a row or accepts a proposal. Every non-`Verified`/`Refused` verdict returns before the store is touched. Negative trace lookups are not cached, and a token is rejected when it would outlive its session key. The session key reaches only `ITokenIssuer`. The new SQL keeps the `tenant_id` predicate. `CachingTraceResolver` wrapping `FixtureTraceResolver` is still caught by the decorator-unwrapping production guard.

The defects below are fail-closed gaps that return the wrong status, liveness holes, missing binding checks, and a DB constraint that will break a later phase. None lets an unverified proposal be stored through the current endpoint, so none is classified Critical.

## Warnings

### WR-01: Token issuance (popeye) has no timeout; only the resolver call is bounded

**File:** `src/Olive.Application/TraceVerifier.cs:13-32`
**Issue:** `tokens.GetAsync(sessionKey, key, ct)` runs on the request token only. The linked CTS with `options.Timeout` wraps only `resolver.ResolveAsync`. A hung popeye token endpoint therefore holds the request open indefinitely instead of failing closed with 503 `trace_verification_unavailable`. `FaultingTokenIssuer` has no `Hang` mode, so no test covers this.
**Fix:** Create the linked CTS before token acquisition and apply it to both calls (or give each its own budget). Map a timeout during issuance to `TraceVerdict.Unavailable("token_issuer_timeout")`:
```csharp
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(options.Timeout);
TokenOutcome token;
try { token = await tokens.GetAsync(sessionKey, key, cts.Token); }
catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new TraceVerdict.Unavailable("token_issuer_timeout"); }
```
Add a `TokenFault.Hang` test.

### WR-02: Exceptions thrown by the resolver or issuer surface as 500 internal_error, not 503 retryable

**File:** `src/Olive.Application/TraceVerifier.cs:24-31`, `src/Olive.Application/ServiceTokenProvider.cs:51`
**Issue:** The verifier catches only `OperationCanceledException`. Any other exception from a port (`HttpRequestException`, `IOException`, a `KeyNotFoundException` from WR-03, a `JsonException` from a future live adapter) reaches `ProblemExceptionHandler`. That handler maps it to 500 `internal_error` with no `retryable` flag and no `Retry-After`. Nothing is stored, so the outcome is still fail-closed. However, the documented D-06 contract ("every other verdict -> 503 trace_verification_unavailable") is broken, and callers are told the error is non-retryable. Today's tests cover only result-typed faults, never thrown ones.
**Fix:** Wrap both port calls and convert unexpected exceptions to `TraceVerdict.Unavailable("trace_resolver_exception")` / `TokenOutcome.Failed("token_issuer_exception")`. Log only the exception type name. Let `IntrospectionUnavailableException` keep propagating if that mapping is intended. Add `ResolverFault.Throw` / `TokenFault.Throw` cases.

### WR-03: TraceResolutionResponse.Parse throws on duplicate keys despite its "never throws" contract

**File:** `src/Olive.Integrations.Hometown/TraceResolutionResponse.cs:35-48, 74`
**Issue:** The field check counts properties and checks that each name is allowed, but it does not check uniqueness. A body such as `{"trace_id":..,"trace_id":..,"schema_version":..,"run_id":..,"tenant_id":..,"agent_id":..,"proposal_hash":..}` (with `created_at` missing) passes `count == 7`. `root.GetProperty("created_at")` at line 74 then throws `KeyNotFoundException`. Only `JsonException` is caught, so the exception escapes and becomes a 500 with no `trace_response_malformed` alarm. Duplicate keys in a security-relevant response should also be rejected outright, because which duplicate wins is parser-dependent.
**Fix:** Track the names already seen and return `Malformed` on a repeat. In `Str`, use `TryGetProperty` instead of `GetProperty`:
```csharp
var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (var p in root.EnumerateObject())
    if (Array.IndexOf(Fields, p.Name) < 0 || !seen.Add(p.Name)) return new TraceLookup.Malformed();
if (seen.Count != Fields.Length) return new TraceLookup.Malformed();
...
if (!root.TryGetProperty(name, out var v)) return false;
```

### WR-04: Resolved trace is never bound to the requested trace_ref

**File:** `src/Olive.Application/TraceVerifier.cs:51-75`
**Issue:** `TraceFacts.TraceId` is parsed and schema-required but never compared with `traceRef`. A live resolver bug, a proxy mix-up or a cache-key collision could return facts for a different trace. Those facts would be accepted whenever run, tenant, agent and hash happen to match. The proposal hash limits the impact, but the "traces to a verified hometown answer" chain should bind the answer to the trace that was asked for. Nothing records which trace actually verified.
**Fix:** Check the trace id first and refuse on a mismatch (or treat it as `Malformed` -> Unavailable, since it is a protocol violation):
```csharp
if (!string.Equals(f.TraceId, traceRef, StringComparison.Ordinal)) return new TraceVerdict.Unavailable("trace_id_mismatch");
```
Pass `traceRef` into `Check`.

### WR-05: Trace cache is keyed by tenant only, so agents in the same tenant skip hometown authorization

**File:** `src/Olive.Application/CachingTraceResolver.cs:15-19`
**Issue:** The cache key is `(TenantId, traceId)`. Agent B in the same tenant gets agent A's cached `Found` without hometown ever seeing B's service token. Hometown may 403 or 404 that lookup for B. Through the cache, B instead receives a stored `refused_policy` row with `trace_agent_mismatch`, which confirms that the trace exists and belongs to another agent. This turns the "optimisation only" cache (D16.4) into an authorization bypass and an existence oracle.
**Fix:** Include the delegated agent in the key: `var key = (grant.TenantId, grant.DelegatedAgentId, traceId);`. Add a test showing that two agents in one tenant each reach the inner resolver.

### WR-06: Token provider returns already-expired (or within-skew) freshly issued tokens

**File:** `src/Olive.Application/ServiceTokenProvider.cs:73-90`
**Issue:** After issuance the code rejects only `grant.ExpiresAt > key.ExpiresAt`. When `usableUntil <= now`, the grant is not cached, yet it is still returned as `Ok`. Examples are a token issued with `ExpiresAt` in the past, or a session key within 30 s of expiry. Hometown will then 401 the call. That raises the Critical `trace_resolver_401` alarm, which blames Olive's credentials for what is really an expired token. The cached path applies `now < hit.UsableUntil`, but the fresh path has no equivalent check.
**Fix:** Fail without alarming (or with a distinct code) when `usableUntil <= now`:
```csharp
if (usableUntil <= now) return new TokenOutcome.Failed("token_expires_too_soon");
```

### WR-07: Migration 0006 constraint breaks state transitions to refused_policy and can fail on existing data

**File:** `sql/migrations/0006_trace_verification.sql:10`; `src/Olive.Persistence/ProposalSql.cs:22-23`
**Issue:** `proposal_refusal_state_agrees` is added as a validated constraint. Migration 0002 granted `olive_app` UPDATE on `state`. Any existing row already in `refused_policy` with a NULL `refusal_reason` therefore makes the migration fail to apply. Going forward, `TransitionAsync(..., toState: "refused_policy", ...)` remains a public `IProposalStore` path, but it now fails with SQLSTATE 23514, which surfaces as a 500. Transitions out of `refused_policy` fail the same way unless `refusal_reason` is also cleared. Phase 4 policy refusals reusing `TransitionAsync` will hit this. The comments say Phase 4 must use `InsertRefusedAsync`, but nothing enforces it.
**Fix:** Have `TransitionAsync` reject `toState == RefusedPolicy` with `ArgumentException` (the reason must come from `InsertRefusedAsync`). Before adding the constraint, backfill legacy rows (`update proposal set refusal_reason = 'legacy_refusal' where state = 'refused_policy' and refusal_reason is null`), or add it `NOT VALID` and validate it later.

## Info

### IN-01: The database does not enforce "no proposed row without trace verification"

**File:** `sql/migrations/0006_trace_verification.sql:5-7`; `src/Olive.Application/IProposalStore.cs:23`
**Issue:** `NewProposal.TraceVerification` defaults to `null`, and the column is nullable for every state. Any future caller of `IProposalStore.InsertAsync` can store a `proposed` row that was never verified, and only the endpoint's code ordering prevents it. The core value says this chain must be provable from the record.
**Fix:** Add `check (trace_verification is not null or state <> 'proposed') NOT VALID` (legacy rows are exempt), or make `TraceVerification` required on `InsertAsync`.

### IN-02: Parser hardcodes Source = "fixture"

**File:** `src/Olive.Integrations.Hometown/TraceResolutionResponse.cs:63`
**Issue:** `Parse` is the strict hometown response parser, but it always tags results `TraceVerificationSources.Fixture`. A live resolver that reuses it would write `trace_verification = 'fixture'` on real verifications, which corrupts the provenance column.
**Fix:** Take the source as a parameter (`Parse(string json, string source)`), or return `TraceFacts` and let each resolver build the `Found`.

### IN-03: created_at parsing is culture-lenient, not RFC 3339

**File:** `src/Olive.Integrations.Hometown/TraceResolutionResponse.cs:57`
**Issue:** `DateTimeOffset.TryParse` with `AssumeUniversal` accepts values such as `"1/2/2026"` or `"2026-01-02"`, while the schema requires `format: date-time`. The strict parser is therefore looser than its contract.
**Fix:** Use `DateTimeOffset.TryParseExact` with ISO-8601 round-trip formats that require an offset or `Z`.

### IN-04: TraceVerifierOptions accepts invalid timeouts outside Program.cs

**File:** `src/Olive.Application/TraceVerdict.cs:12-15`
**Issue:** Only `Program.cs` validates the timeout. `new TraceVerifierOptions(TimeSpan.Zero)` or a negative value (other than -1 ms) makes `CancelAfter` throw `ArgumentOutOfRangeException` per request, which becomes a 500.
**Fix:** Validate in the record constructor: `Timeout > TimeSpan.Zero ? ... : throw new ArgumentOutOfRangeException(...)`.

### IN-05: Positive trace cache has no TTL and caches Found results that lead to refusal

**File:** `src/Olive.Application/CachingTraceResolver.cs:22-30`
**Issue:** A `Found` with incomplete facts (for example `run_id: null`, the ATE-03 case) is cached until restart or until the cache reaches capacity and is cleared. If hometown later fills those facts in, Olive keeps refusing with `trace_unresolved`. The schema-version check (0006 line 7) is also one-directional: `trace_verification` may be set while `trace_schema_version` is null.
**Fix:** Cache only `Found` results whose facts are complete (all of run, agent and hash non-null), or add a bounded TTL. Consider requiring `trace_schema_version` when `trace_verification` is set.

---

_Reviewed: 2026-10-22_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
