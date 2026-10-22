# Phase 3: Trace Verification - Research

**Researched:** 2026-10-22
**Domain:** Fail-closed trace verification (hometown resolver port + fixtures), popeye service-token issuer stub and cache, session-key containment evidence, in an ASP.NET Core / .NET 10 / Npgsql codebase
**Confidence:** HIGH on the ratified behaviour (D§14-D§17 read in full, code read in full); MEDIUM on the popeye token-issuer stub shape, because no popeye spec defines it yet

## Summary

Phase 3 inserts one new pipeline step, trace verification, between catalog/parameter validation and persistence in `ProposalEndpoints.PostAsync`. The ratified behaviour is fully specified (D§16.2-16.5 as amended by D§17.1-17.4), and the Phase 2 work already made check 5 a pure comparison: `Jcs.ProposalHash(root)` exists and `proposal_hash` is already stored on every row. What is new is (a) three ports with stubs (`ITraceResolver` fixture-backed, `ITokenIssuer` popeye stub, `IOperationalAlarm`), (b) two in-process caches (service tokens and positive trace resolutions), (c) a small orchestrator (`TraceVerifier`) that applies the five ordered checks, (d) the first stored `refused_policy` rows, which need migration 0006 and a new store method, and (e) containment evidence (recording decorators, a reflection architecture test, a sentinel search).

Three findings change how the planner should shape work. First, **no popeye spec defines token issuance**: POPEYE_v0.4.2 and v0.4.3 cover introspection and `key_id` format only, and D§17.1 says popeye's side "needs its own specification and ratification". The `ITokenIssuer` port and stub are therefore Olive-defined and must be labelled as such. Second, **the app cannot store a `refused_policy` row today**: migration 0003 deliberately excluded `state` from the `olive_app` INSERT grant, and no column holds a refusal reason or trace result. Migration 0006 and an `InsertRefusedAsync` store method are required, and Phase 4 will extend that path, so it should be designed as the single refusal-writing path now. Third, **about 45 existing integration tests POST proposals** with arbitrary `trace_ref` values and expect 201; once trace verification is mandatory they all need a matching fixture. The test factory needs an auto-matching fixture mode (details below) or the phase will start with a red suite.

**Primary recommendation:** Build `TraceVerifier` and `ServiceTokenProvider` in `Olive.Application` as logging-free, HTTP-free orchestration over three ports; keep the stubs in `Olive.Integrations.{Popeye,Hometown}` marked `INonProductionOnly`; store refusals through one new atomic store method behind migration 0006; and prove containment with a `[OutboundPort]`-attribute architecture test plus recording decorators plus a sentinel scan.

## Project Constraints (from CLAUDE.md / AGENTS.md)

CLAUDE.md's "single Domain.cs" description is stale; the solution is the eleven-project layout. Directives that bind this phase:
- No `HttpClient`, no outbound HTTP, no raw sockets, anywhere in `src` (`BannedSymbols.txt` is RS0030 error-level; `REQ_ACT_04_no_src_assembly_references_outbound_network_assemblies` inspects every built `src` assembly). The live hometown/popeye transports are v2 (LIVE-01/02/03). Phase 3 stubs are in-process.
- No write-capable credential. The session key goes only to popeye; the service token only to hometown. Tokens are never logged or stored.
- Fixtures and stubs are barred in production (`INonProductionOnly`, FND-05 guard, which also walks `IComponentDecorator.Inner`).
- Tenant predicate in every query plus forced RLS; `contracts/vendored/vocabulary.yaml` never edited.
- Secret-holding types are sealed non-record classes that redact themselves. A new type that carries a `ServiceToken` must also be a class with an overridden `ToString`, not a `record`.
- `DateTime.UtcNow`/`DateTimeOffset.UtcNow` are banned; inject `TimeProvider`.
- Tests are named by requirement ID (`TRC_02_...`, `IDN_06_...`). Contracts get a schema plus paired `.valid.json`/`.invalid.json`.
- Within `v1`, changes are additive and optional only (response shape changes need hometown's agreement for the resolution contract; Olive's own submit/GET responses may gain optional fields).
- Package allowlist is enforced by `FND_02_direct_packages_match_allowlist_in_lockfiles`; **this phase needs no new package** (see below).
- GSD workflow: edits go through a GSD command; the orchestrator commits.

<user_constraints>
## User Constraints (from CONTEXT.md)

No `03-CONTEXT.md` exists (user chose to continue without discuss-phase). The binding inputs are the ratified Decision record and the REQUIREMENTS.md rulings; where they are silent, this document gives a recommended default under "Open Questions / Recommended Defaults" for the planner to lock.

### Locked Decisions (ratified, from Decision v0.5 and REQUIREMENTS.md; do not re-open)
- D§17.3 check order, first failure wins: (1) 404 -> `422 trace_not_found`, retryable, not stored; (2) `run_id` null or different -> `refused_policy` `trace_unresolved`, stored; (3) `tenant_id` differs from the key's tenant -> `trace_tenant_mismatch`, stored; (4) `agent_id` null or differs from the service token's `delegated_agent_id` -> `trace_agent_mismatch`, stored; (5) `proposal_hash` null or differs from Olive's hash -> `trace_proposal_mismatch`, stored. Resolver unavailable/timeout -> 503, nothing stored. hometown 401/403, or popeye refusing the token -> 503 + operational alarm, nothing stored.
- D§16.4: a 200 is cached indefinitely by `(tenant_id, trace_id)`; 404, 503, 401, 403 never cached; the cache is an optimisation only.
- D§16.5: `trace_verification` in `{fixture, hometown}`; fixtures barred in production (startup refusal).
- D§17.1: popeye issues `application`-type tokens carrying `tenant_id`, immutable `delegated_agent_id`, source `key_id`, expiry <= key expiry; cache keyed `(tenant_id, delegated_agent_id, key_id)` until shortly before the earlier of the two expiries; token discarded with 503 + alarm if its `delegated_agent_id` or `tenant_id` disagrees with Olive's own introspection; session key goes only to popeye (introspection and issuance), never to hometown; token never logged, stored, or sent anywhere but hometown.
- D§15.2/Phase 2: replay (200) and conflict (409) never call the resolver; 400s precede the idempotency lookup; trace verification runs after schema/catalog validation.
- D§17.2 / Phase 2 D-08: `proposal_hash` is JCS SHA-256 over exactly `action_type`, `party_ref`, `parameters`, `justification`, as submitted; already computed and stored.
- Q19: port-level capture now; wire-level capture is LIVE-03.
- Q17: no stub of any kind may run in production.

### Claude's Discretion
Everything the spec leaves open: port signatures, fixture layout, alarm mechanism, migration column set, HTTP status for a stored refusal, reason codes for 503s, timeout value, cache bounds, test-factory strategy. Recommendations are in the Open Questions section.

### Deferred Ideas (OUT OF SCOPE)
- Live hometown HTTP transport (LIVE-01), live popeye introspection/issuance (LIVE-02), wire-level capture (LIVE-03).
- Policy checks and `not_in_catalog` as a stored refusal (Phase 4); queue and approval (Phase 5); executor and JSONL output (Phase 6).
- Introspection cache (D-14; v2).
- Shared JCS vector confirmation by hometown (Q20) is a manual hand-off, not Phase 3 code.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| TRC-01 | `TraceResolver` port, fixture implementation in `contracts/trace-resolution/`, seven-field schema-checked fixtures | Port + `FixtureTraceResolver` in `Olive.Integrations.Hometown` with embedded fixtures (pattern: `contracts/introspection/` + `StubKeyIntrospector`); strict seven-field parser shared with the future live transport |
| TRC-02 | Not found -> `422 trace_not_found`, `retryable: true`, nothing stored, retry works | `TraceLookup.NotFound` never cached; endpoint returns 422 before any store call; `ProblemResults` needs a `retryable` extension overload |
| TRC-03 | Null/mismatched `run_id` -> stored `refused_policy` `trace_unresolved`; `trace_schema_version` recorded | `TraceVerifier` check 2; migration 0006 columns; `InsertRefusedAsync` |
| TRC-04 | Tenant mismatch -> `trace_tenant_mismatch` | Check 3; fixture returns facts verbatim regardless of caller tenant so the case is reachable |
| TRC-05 | Agent null/different from token `delegated_agent_id` -> `trace_agent_mismatch` | Check 4 compares to `ServiceTokenGrant.DelegatedAgentId`, not the introspected agent (equal by IDN-07) |
| TRC-06 | JCS SHA-256 hash of four fields; null/different -> `trace_proposal_mismatch` | Reuse `Jcs.ProposalHash(root)` (already in endpoint); compute once before trace, pass into insert |
| TRC-07 | Unavailable/timeout -> 503; 401/403 -> 503 + alarm; nothing stored | `TraceLookup.Unavailable/CallerRejected`, linked-CTS timeout, `IOperationalAlarm`; introspection caller-rejection needs a new exception type |
| TRC-08 | Positive resolutions cached indefinitely by `(tenant_id, trace_id)`; negatives never | `CachingTraceResolver : ITraceResolver, IComponentDecorator`, bounded `ConcurrentDictionary` |
| TRC-09 | `trace_verification` in `{fixture, hometown}` from the answering resolver; schema rejects others | `Source` carried on the lookup result by the leaf resolver; migration 0006 CHECK |
| IDN-05 | Token-issuer port with stub; `application` tokens with `tenant_id`, `delegated_agent_id`, `key_id`, expiry | `ITokenIssuer` + `StubTokenIssuer` (reuses introspection fixtures for derivation) - shape is Olive-defined [ASSUMED] |
| IDN-06 | Token cache keyed `(tenant, delegated_agent, key_id)`, never past either expiry, never reused for a new key; over-long token rejected not cached | `ServiceTokenProvider` with skew; `TimeProvider`-driven tests |
| IDN-07 | Claim disagreement with Olive's introspection -> discard, 503, alarm | Validation inside `ServiceTokenProvider` before caching; also re-validated on cache hit |
| IDN-08 | Recording decorators on every outbound port; sentinel search of logs (Trace), JSONL, stored rows finds nothing | `[OutboundPort]` attribute + reflection architecture test + recording decorators + `SentinelScan` helper (JSONL half lands with Phase 6) |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Session-key introspection (existing) | popeye (external) via `IKeyIntrospector` | Host auth handler | Only popeye may see the key; already in place |
| Service-token issuance and claim validation | `Olive.Application` (`ServiceTokenProvider`) | `Olive.Integrations.Popeye` (stub / future transport) | Claim-vs-introspection check is Olive policy, independent of transport |
| Token cache | `Olive.Application` | - | Pure in-memory optimisation; no I/O |
| Trace resolution transport | `Olive.Integrations.Hometown` (fixture now, HTTP in v2) | - | hometown returns facts only |
| Five ordered checks | `Olive.Application` (`TraceVerifier`) | - | hometown must not issue a verdict (HOMETOWN_v0.4.1 section 2.5 purpose); Olive decides |
| Trace positive cache | `Olive.Application` (decorator) | - | Optimisation only (D§16.4) |
| Operational alarm | Port in `Olive.Application`; log-backed implementation in `Olive.Host` | test recorder | Application has no logging package; Host has `ILogger` |
| Storing refusals | `Olive.Persistence` (`InsertRefusedAsync`) | migration 0006 | Atomic insert + state transition + events inside the tenant unit of work |
| HTTP mapping (422/503, problem details) | `Olive.Host` | - | Keeps reason codes and status in one place |
| Fixture / stub production bar | `Olive.Host` startup guard (existing) | `INonProductionOnly` markers | FND-05 already handles it; decorators must expose `Inner` |

## Standard Stack

No new packages. Everything below uses the .NET 10 BCL or the ASP.NET Core shared framework already referenced.

### Core
| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `System.Collections.Concurrent.ConcurrentDictionary` | BCL (net10.0) | Token and trace caches | No package, allowlist unchanged; the caches are explicitly optimisation-only [CITED: specs/DECISION_olive-poc2-phase1_v0.5.md section 16.4] |
| `TimeProvider` | BCL | Expiry maths, timeouts (`new CancellationTokenSource(TimeSpan, TimeProvider)`) | `DateTime.UtcNow` is banned; `TimeProvider` is already registered as a singleton |
| `System.Text.Json` | BCL | Strict seven-field parse of resolver responses | Already used by `StubKeyIntrospector` |
| `System.Security.Cryptography.RandomNumberGenerator` | BCL | Stub token values | Never use `Guid` or a counter for bearer-like values |
| Existing `Olive.Domain.Jcs` | in-repo | `ProposalHash(root)` | Phase 2 deliverable |
| Existing Dapper + Npgsql 10.0.3 | pinned in `Directory.Packages.props` | `InsertRefusedAsync` | Existing store pattern |

### Supporting (tests only, no new packages)
| Component | Purpose | When to Use |
|-----------|---------|-------------|
| Hand-written `ManualTimeProvider : TimeProvider` | Deterministic token-expiry tests | Do **not** add `Microsoft.Extensions.TimeProvider.Testing`: it is not in `Directory.Packages.props`, and adding it means lockfile and allowlist changes for ten lines of code |
| Existing `JsonSchemaLite` (test helper) or `SchemaValidator` | Fixture schema checks | Same approach as `IntrospectionFixtureTests` |
| Existing `InMemoryLogCapture` | Trace-level log capture | `OliveApiFactory` already sets min level Trace |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `ConcurrentDictionary` caches | `Microsoft.Extensions.Caching.Memory` (ships in the ASP.NET Core shared framework) | Would work without a package reference in Host, but `Olive.Application` cannot see it without a package/framework reference, and the allowlist test is strict. Not worth it for two small caches |
| Logging inside Application | Add `Microsoft.Extensions.Logging.Abstractions` to Application | Requires allowlist and lockfile change. Keep Application logging-free; Host logs outcomes and implements `IOperationalAlarm` |
| Fault behaviours in fixture files | Fault-injecting test doubles | Fixtures must be schema-checked against the seven-field success response (TRC-01); fault modes (timeout, 403) are behaviours, not responses |

**Installation:** none. **Version verification:** not applicable (no packages added). `dotnet --version` on this machine reports 10.0.400, within `global.json` (10.0.204, `rollForward: latestFeature`).

## Package Legitimacy Audit

No external packages are recommended for this phase, so slopcheck was not required.

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| (none) | - | - | - | - | n/a | No new packages |

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

## Spec Reconciliation (D§4 stop rule and flagged divergences)

**Stop rule status:** `~/fiskroad/specs/POPEYE_v0.4.2.md`, `POPEYE_v0.4.3.md`, `NONO_POPEYE_SESSION_CREDENTIAL_CONTRACT_v0.1.md` are present [VERIFIED: file read this session]. HOMETOWN_v0.4.1.md is present at `~/hometown/specs/` and supersedes HOMETOWN_v0.4.md for section 2.5 [VERIFIED: file read]. Liam v0.4.1 is present at `~/liam/specs/`. Nothing blocks.

| Item | Finding | Disposition |
|------|---------|-------------|
| POPEYE v0.4.3 vs v0.4.2 | v0.4.3 is a narrow amendment: `key_id` must be a lowercase canonical UUID on every surface, including the introspection response. It changes nothing else Phase 3 depends on. `StubKeyIntrospector` already enforces the lowercase-UUID pattern on `key_id` and `tenant_id`, so it already conforms. [CITED: POPEYE_v0.4.3.md section 2] | Keep citing v0.4.2 for the introspection shape; add v0.4.3 as a second reference. The stub's `ServiceTokenGrant.KeyId` must be the same UUID string. No silent version switch needed |
| NONO_POPEYE_SESSION_CREDENTIAL_CONTRACT_v0.1 | Draft contract for nono -> popeye agent-key issuance. Not about Olive's service tokens. No dependency for Phase 3 | Ignore for this phase |
| Token issuance in popeye specs | Neither v0.4.2 nor v0.4.3 defines a token-issuance interface; `grep` for `delegated_agent_id` across popeye, fiskroad, liam and bluto finds it only in hometown specs. D§17.1 states popeye's side needs its own spec and ratification | `ITokenIssuer` shape is an Olive-side proposal. Mark `[ASSUMED]`, document in `contracts/trace-resolution/README.md`, and list as an open coordination item for the popeye owner |
| POPEYE section 2.1 non-agent key -> `active:false` vs D§7 403 | Real popeye returns only `{active:false}` for `user`/`application` keys, so against live popeye the response is `401 key_inactive`, never `403 principal_not_permitted`. Phase 1 stub fixtures return `active:true` with those types so the D§7 403 path is exercised | Recommended default: **keep the stub divergence and the 403 path unchanged**. Both outcomes fail closed. Record in `contracts/introspection/README.md` (already there) and in Phase 9 COMPATIBILITY.md that live popeye yields 401. No Phase 3 code change |
| POPEYE section 2.2 caching and "no onward use" | Introspection positives may be cached 60 s, never past `expires_at`; negatives never. A service that introspects a key may not present it to the exchange | Phase 3 does not add an introspection cache (D-14). Presenting the key to token issuance is Olive's ratified D§17.1 design and is not the exchange path, but popeye has not confirmed it - include in the popeye hand-off |
| POPEYE section 2.3 registered callers | `hometown`, `olive` registered as introspection callers; a popeye 401/403 to Olive means Olive's own registration failed | This is the "caller rejected" case for introspection. The `IKeyIntrospector` port has no such outcome today (see Pitfall 5) |
| hometown `trace_id`/`run_id` are UUIDs; `schema_version` enum | HOMETOWN_v0.4.1 section 2.5: `schema_version` `ate/0.3 | ate/0.4`, with `ate/0.2` handling an open hometown review item | Parse `schema_version` with a loose pattern (`^ate/[0-9]+\.[0-9]+$`) and store verbatim; do not hard-fail on an unlisted version while hometown's review item is open |
| hometown 401 vs 403 | 401 = missing/invalid/expired token or unregistered caller; 403 = token without `tenant_id` | Both map to 503 + alarm in Olive; no distinction needed in the HTTP response, but keep the two as separate alarm codes |
| hometown "no 400" | Malformed trace ids return the same 404 | Olive's `trace_ref` is any non-empty string (Phase 2 schema). Treat it as opaque. A live transport must percent-encode the path segment (v2). Malformed refs simply come back 404 -> 422 `trace_not_found` |
| JCS golden set (Q20) | Vectors are committed in `contracts/jcs/v1/` with a README hand-off. hometown's repo has no JCS code yet (only spec text in `HOMETOWN_v0.4.1.md`, `ANSWER_TRACE_ENVELOPE_v0.1.md`); confirmation is still a manual action | Not a Phase 3 code task. Add one Phase 3 test that makes the join concrete: each committed trace fixture ships with its submitted proposal, and a test asserts `fixture.proposal_hash == Jcs.ProposalHash(proposal)`. Those pairs double as extra cross-component vectors for hometown |

## Architecture Patterns

### System Architecture Diagram

```
POST /v1/proposals
   |
   v
[1 introspect]  AgentKeyAuthenticationHandler -> IKeyIntrospector (popeye)  -- 401 / 503(+alarm if caller rejected)
   |  ActiveKey (tenant, agent, key_id, expires_at, ...)
   v
[2 principal checks]  agent type + allowlist                                -- 403
   v
[3 read body, JsonGate]  size, I-JSON, run_id                               -- 413 / 400
   v
[4 idempotency]  FindByRun(tenant, agent, run_id)                           -- 200 replay / 409   (resolver NOT called)
   v
[5 schema + catalog]  SubmissionValidator                                   -- 400 (stored not_in_catalog = Phase 4)
   v
[6 TRACE VERIFICATION]  (new)
   |   6a ServiceTokenProvider.GetAsync(sessionKey, activeKey)
   |        cache hit? -> revalidate vs introspection -> use
   |        miss -> ITokenIssuer.IssueAsync(sessionKey)  [POPEYE ONLY SEES THE KEY]
   |             Unavailable            -> 503
   |             Refused/CallerRejected -> 503 + ALARM
   |             claims != introspection / exp > key.exp -> discard, 503 + ALARM
   |        cache store: key (tenant, delegated_agent, key_id), ttl = min(token.exp, key.exp) - skew
   |   6b ITraceResolver.ResolveAsync(grant, trace_ref)   [HOMETOWN SEES TOKEN, NEVER THE KEY]
   |        CachingTraceResolver: hit on (tenant, trace_id) -> facts, no inner call
   |        miss -> FixtureTraceResolver (now) / HTTP (v2), linked-CTS timeout
   |             NotFound               -> 422 trace_not_found (retryable), NOTHING STORED, not cached
   |             Unavailable / timeout  -> 503, nothing stored
   |             CallerRejected(401/403)-> 503 + ALARM, nothing stored
   |             Malformed response     -> 503 + ALARM, nothing stored
   |             Found(facts, source)   -> cache; continue
   |   6c checks, first failure wins:  run_id -> tenant -> agent -> proposal_hash
   |        any fail -> refusal (reason code)            ok -> Verified(source, schema_version)
   v
[7 policy]  (Phase 4 slot; not built here)
   v
[8 persist]
      Verified -> InsertAsync(... trace_verification, trace_schema_version)   state = proposed (Phase 4 later moves it)
      Refused  -> InsertRefusedAsync(...)  one tenant transaction:
                    insert (state default 'proposed') + 'submitted' event
                    -> CAS update state -> refused_policy + refusal_reason
                    -> 'trace_refused' event (detail: static reason)
      lost race (ON CONFLICT DO NOTHING returned null) -> re-enter [4] decision
   v
201 {proposal_id, state[, refusal_reason]}
```

### Recommended Project Structure
```
src/Olive.Domain/
  TraceVerificationSources.cs      # "fixture" | "hometown" constants
  TraceRefusalReasons.cs           # trace_unresolved ... trace_proposal_mismatch
src/Olive.Application/
  ITraceResolver.cs                # port + TraceFacts + TraceLookup (sealed hierarchy)
  ITokenIssuer.cs                  # port + TokenIssueResult hierarchy
  ServiceTokenGrant.cs             # sealed CLASS (holds ServiceToken), not a record
  ServiceTokenProvider.cs          # cache + IDN-06/07 validation
  CachingTraceResolver.cs          # IComponentDecorator
  TraceVerifier.cs                 # orchestration + 5 checks -> TraceVerdict
  IOperationalAlarm.cs             # port + AlarmCodes (static strings)
  OutboundPortAttribute.cs         # [OutboundPort(OutboundDestination.Popeye|Hometown)]
src/Olive.Integrations.Hometown/
  TraceResolutionResponse.cs       # strict seven-field parser (reused by v2 HTTP transport)
  FixtureTraceResolver.cs          # INonProductionOnly; mutable fixture set for tests
src/Olive.Integrations.Popeye/
  StubTokenIssuer.cs               # INonProductionOnly; wraps StubKeyIntrospector derivation
src/Olive.Host/
  Alarms/LoggingOperationalAlarm.cs
  Endpoints/ProposalEndpoints.cs   # new step 6 + refusal/422/503 mapping
contracts/trace-resolution/
  trace-resolution-response.schema.json   # seven fields, additionalProperties:false
  index.json                              # trace_ref -> fixture file
  <case>.json + <case>.proposal.json      # response + the submitted proposal it binds
  README.md                               # fixture rules, assumptions, popeye hand-off
sql/migrations/0006_trace_verification.sql
```

### Pattern 1: Result-typed ports (no exceptions for expected outcomes)
**What:** D§16.2 names the resolver contract `found | not_found | unavailable | caller_rejected`. Model it as a sealed hierarchy; use exceptions only for programmer errors and the existing `IntrospectionUnavailableException` path.
**When to use:** all three new ports.
```csharp
// Source: derived from DECISION v0.5 section 16.2 / 17.3 (spec) and existing StubKeyIntrospector style
[OutboundPort(OutboundDestination.Hometown)]
public interface ITraceResolver
{
    Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct);
}

public abstract record TraceLookup
{
    public sealed record Found(TraceFacts Facts, string Source) : TraceLookup;   // Source: "fixture" | "hometown"
    public sealed record NotFound : TraceLookup;
    public sealed record Unavailable : TraceLookup;
    public sealed record CallerRejected : TraceLookup;                           // hometown 401/403
    public sealed record Malformed : TraceLookup;                                // contract violation
}

// Exactly the seven resolution fields; no secrets, so a record is fine here.
public sealed record TraceFacts(string TraceId, string SchemaVersion, string? RunId, Guid TenantId,
    string? AgentId, string? ProposalHash, DateTimeOffset CreatedAt);
```

### Pattern 2: Grant as a redacting class
```csharp
// Source: FND-06 (sealed non-record secret holders) + DECISION section 17.1 item 1
public sealed class ServiceTokenGrant(ServiceToken token, Guid tenantId, string delegatedAgentId, string keyId, DateTimeOffset expiresAt)
{
    public ServiceToken Token { get; } = token;
    public Guid TenantId { get; } = tenantId;
    public string DelegatedAgentId { get; } = delegatedAgentId;
    public string KeyId { get; } = keyId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "[REDACTED ServiceTokenGrant]";
}

[OutboundPort(OutboundDestination.Popeye)]
public interface ITokenIssuer
{
    Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct);   // popeye derives tenant + agent from the key
}
// TokenIssueResult: Issued(ServiceTokenGrant) | Unavailable | Refused (out-of-scope tenant/agent) | CallerRejected
```

### Pattern 3: Provider validates before it caches
```csharp
// Source: DECISION section 17.1 items 1 and 4, 17.3 last paragraph, 17.4 test table
public async Task<TokenOutcome> GetAsync(SessionKey sessionKey, ActiveKey key, CancellationToken ct)
{
    var cacheKey = (key.TenantId, key.AgentId!, key.KeyId);
    var now = time.GetUtcNow();
    if (_cache.TryGetValue(cacheKey, out var hit) && now < hit.UsableUntil && Agrees(hit.Grant, key)) { return TokenOutcome.Ok(hit.Grant); }
    _cache.TryRemove(cacheKey, out _);

    var issued = await issuer.IssueAsync(sessionKey, ct);
    // Unavailable -> 503 no alarm; Refused / CallerRejected -> 503 + alarm
    if (issued is not TokenIssueResult.Issued i) { /* map */ }
    var g = i.Grant;
    if (!Agrees(g, key))        { alarm.Raise(AlarmCodes.TokenClaimsMismatch);       return TokenOutcome.Failed(); }   // IDN-07, discard
    if (g.ExpiresAt > key.ExpiresAt) { alarm.Raise(AlarmCodes.TokenOutlivesKey);     return TokenOutcome.Failed(); }   // IDN-06, never cached
    var usableUntil = Min(g.ExpiresAt, key.ExpiresAt) - Skew;                                                         // Skew default 30 s
    if (usableUntil > now) { Store(cacheKey, g, usableUntil); }   // else use once, uncached
    return TokenOutcome.Ok(g);
}
static bool Agrees(ServiceTokenGrant g, ActiveKey k) =>
    g.TenantId == k.TenantId && string.Equals(g.DelegatedAgentId, k.AgentId, StringComparison.Ordinal)
    && string.Equals(g.KeyId, k.KeyId, StringComparison.Ordinal);
```

### Pattern 4: Verifier applies the five checks in the spec's order
```csharp
// Source: DECISION v0.5 section 17.3 table; Jcs.ProposalHash already exists (Phase 2)
// 1 NotFound handled before this method (422). 2..5 below, first failure wins.
if (f.RunId is null || !string.Equals(f.RunId, runId, StringComparison.Ordinal)) return Refuse(TraceRefusalReasons.Unresolved, f, source);
if (f.TenantId != key.TenantId)                                                  return Refuse(TraceRefusalReasons.TenantMismatch, f, source);
if (f.AgentId is null || !string.Equals(f.AgentId, grant.DelegatedAgentId, StringComparison.Ordinal))
                                                                                 return Refuse(TraceRefusalReasons.AgentMismatch, f, source);
if (f.ProposalHash is null || !string.Equals(f.ProposalHash, proposalHash, StringComparison.Ordinal))
                                                                                 return Refuse(TraceRefusalReasons.ProposalMismatch, f, source);
return Verified(source, f.SchemaVersion);
```

### Pattern 5: Where it slots into `PostAsync`
After the `validator.Validate(root)` switch and before the field extraction/`NewProposal`:
```csharp
var proposalHash = Jcs.ProposalHash(root);                       // hoist: currently computed at insert
var sessionKey = AgentKeyHeader.TryRead(http.Request.Headers[AgentKeyHeader.Name].ToString())!;  // same single owner of the header
var verdict = await verifier.VerifyAsync(sessionKey, key, runId, traceRef, proposalHash, ct);
switch (verdict)
{
    case TraceVerdict.NotFound:        log content-free; return 422 trace_not_found + retryable:true   // nothing stored
    case TraceVerdict.Unavailable:     return 503 trace_verification_unavailable                       // nothing stored
    case TraceVerdict.Refused r:       -> store.InsertRefusedAsync(...); 201 {proposal_id, state:"refused_policy", refusal_reason}
    case TraceVerdict.Verified v:      -> NewProposal with TraceVerification=v.Source, TraceSchemaVersion=v.SchemaVersion
}
```
Do not pass the `SessionKey` into anything except `ServiceTokenProvider` -> `ITokenIssuer`.

### Anti-Patterns to Avoid
- **Resolver sees the session key:** `ITraceResolver` must accept only `ServiceTokenGrant`. Enforce by the architecture test below, not by convention.
- **Caching a `Verdict`:** cache hometown facts only (D§16.4). Verdicts depend on the proposal.
- **Caching the not-found result "briefly":** TRC-08 forbids any negative caching, including a short TTL.
- **Skipping the token step on a trace-cache hit:** keep the order token -> resolve. The token request is also popeye's registration check for tenant and agent, and check 4 compares against the grant. The token itself is cached so this is cheap. (Recommended default; see Open Questions.)
- **A `record` for the grant or any type holding a token** (ToString would be generated; FND-06).
- **`DateTime.UtcNow` for expiry**: banned; use the injected `TimeProvider`.
- **Fixture resolver that tenant-scopes by caller:** real hometown does, but then `trace_tenant_mismatch` is unreachable in fixtures and TC for check 3 cannot run. Fixtures return facts verbatim.
- **Storing the refusal with a direct `state='refused_policy'` INSERT:** `olive_app` has no INSERT privilege on `state` (migration 0003, deliberate). Keep it; use insert-then-CAS in one transaction.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Proposal hash | A second canonicalizer | `Jcs.ProposalHash(root)` | Phase 2 deliverable; two canonicalizers would reintroduce the "three canonical forms" problem D§17.2 item 5 forbids |
| Redaction of key/token | Custom log scrubbers | `SessionKey`/`ServiceToken` self-redaction plus never exposing them to loggers | FND-06 pattern; scrubbers fail open |
| Non-production bar | A new "is fixture" flag or env switch | `INonProductionOnly` + `IComponentDecorator.Inner` | The FND-05 guard already walks decorator chains (WR-03) |
| Time | `DateTime.UtcNow`, `Task.Delay` for timeouts | Injected `TimeProvider`, `CancellationTokenSource(TimeSpan, TimeProvider)` | Banned API; testable |
| State transition + audit | Ad-hoc SQL in the endpoint | New `InsertRefusedAsync` in `PostgresProposalStore` inside `TenantUnitOfWork` | Keeps RLS tenant setting and predicate discipline |
| Response parsing | Binding resolver JSON to a DTO | Strict parser: exactly seven fields, types checked, else `Malformed` | Same fail-closed pattern as `StubKeyIntrospector`; the live transport reuses it |
| Cache library | A TTL/eviction framework | Bounded `ConcurrentDictionary` with lazy expiry | Two tiny caches; losing them changes latency only |

**Key insight:** every control in this phase already has a spec-fixed rule; the risk is in wiring (ordering, what is stored, what may see the key), not in algorithms. Keep the logic in small pure classes in `Olive.Application` so each ratified test-table row is a unit test, and reserve integration tests for the HTTP/DB surface.

## Runtime State Inventory

Not a rename/refactor/migration phase. One adjacent note: migration 0006 adds columns to a table that may already contain Phase 1/2 rows in developer databases. Those rows have no trace data, so the new columns must be nullable and the application, not NOT NULL, guarantees presence for new rows (see Common Pitfalls).

## Common Pitfalls

### Pitfall 1: The existing suite goes red the moment trace verification is mandatory
**What goes wrong:** about 45 tests across `ApiProposalTests`, `ApiIntegrityTests`, `IdentityEndpointTests`, `SecretLeakageTests`, `IdempotencyTests` POST bodies with `trace_ref` such as `trace://t1` and expect 201. With no matching fixture they would get 422.
**Why it happens:** the resolver is mandatory and the fixtures carry a hash of the proposal, which varies per test body (`BodyFor(runId, partyRef: ...)`).
**How to avoid (recommended):** give `OliveApiFactory` a trace mode. Default for legacy tests = **auto-match**: a test-only `IStartupFilter` middleware (registered via `ConfigureTestServices`) that buffers the request body, computes `run_id` and the four-field hash with the production `Jcs`, and registers a matching fact set in the test `FixtureTraceResolver` for `(key tenant, trace_ref)` and the introspected agent before the endpoint runs. Phase 3's own tests construct the factory with `TraceMode.Explicit` and seed fixtures by hand, so they never rely on the auto-match. Alternative if the planner dislikes middleware: add `factory.SeedTraceFor(body, key)` and call it from `BodyFor`-using tests (more churn).
**Warning signs:** a wave of 422s in unrelated test classes.

### Pitfall 2: Storing a refusal is impossible with current grants
**What goes wrong:** `INSERT ... (state)` fails with 42501, and no column holds `trace_verification`, `trace_schema_version`, or the reason.
**How to avoid:** migration 0006 (forward-only; 0001-0005 are checksum-pinned). Sketch:
```sql
alter table proposal add column trace_verification text, add column trace_schema_version text, add column refusal_reason text;
alter table proposal add constraint proposal_trace_verification_values check (trace_verification is null or trace_verification in ('fixture','hometown'));
alter table proposal add constraint proposal_trace_schema_version_format check (trace_schema_version is null or (trace_verification is not null and trace_schema_version ~ '^ate/[0-9]+\.[0-9]+$'));
alter table proposal add constraint proposal_refusal_reason_format check (refusal_reason is null or refusal_reason ~ '^[a-z][a-z_]{0,63}$');
alter table proposal add constraint proposal_refusal_state_agrees check ((state = 'refused_policy') = (refusal_reason is not null));
grant insert (trace_verification, trace_schema_version) on proposal to olive_app;
grant update (refusal_reason) on proposal to olive_app;     -- additive to the state-only UPDATE grant from 0002
```
`proposal_refusal_state_agrees` holds for the insert (state `proposed`, reason null) and after the single UPDATE that sets both. It deliberately does not pin the reason list, so Phase 4 can add reasons without another constraint change. Extend `PrivilegeMatrixTests` (INSERT column list near line 134, UPDATE column list) and `MigrationRunnerTests` expectations in the same plan.
**Warning signs:** `PrivilegeMatrixTests` column enumerations failing after the migration.

### Pitfall 3: `trace_verification` is not on every row
**What goes wrong:** TRC-09 says "every proposal records `trace_verification`". Under the fixed pipeline order, Phase 4's stored `not_in_catalog` refusal happens at step 5, before any trace call, so it has no trace source. Pre-Phase-3 rows also have none.
**How to avoid:** keep the column nullable with the value CHECK (the schema "rejects any other value"); the application always sets it for any proposal that reaches step 6. Record the interpretation in Open Questions so Phase 4 does not try to force a value.

### Pitfall 4: Distinguishing client abort from resolver timeout
**What goes wrong:** treating `OperationCanceledException` from `RequestAborted` as a resolver outage raises false 503s/log noise; treating a timeout as an abort lets a hung resolver hang the request.
**How to avoid:** `using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);` then `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` -> `Unavailable`; let a genuine abort propagate. Default timeout 5 s, configurable by an optional setting; tests use a hanging double with a tens-of-milliseconds timeout (see Open Questions).

### Pitfall 5: Popeye 401/403 on *introspection* is not modelled
**What goes wrong:** TRC-07 says a 401/403 "from hometown or popeye" yields 503 plus an alarm. `IKeyIntrospector` only has `Active/Inactive` and `IntrospectionUnavailableException`, whose handler returns 503 with no alarm. Olive's own rejected credential would look like a plain outage.
**How to avoid:** add `IntrospectionCallerRejectedException : IntrospectionUnavailableException` (or a boolean on it) thrown by a stub in a new fixture mode; `ProblemExceptionHandler` raises the alarm for it. Keep exception messages static (handler logs only the type name today).

### Pitfall 6: Replay body for a stored refusal
**What goes wrong:** replay returns `{proposal_id, state}` only, so a replayed refusal loses its reason, and Phase 4's POL-05 ("same refusal") has nothing to return.
**How to avoid:** add `refusal_reason` to `ProposalRunRecord`/`FindByRun` and to the 200 replay body. Serialize with `JsonIgnoreCondition.WhenWritingNull` so accepted responses stay exactly `{proposal_id, state}` (the existing `API_01` test asserts that exact property set). Update `proposal-submit-response.schema.json` and `proposal.schema.json` (both `additionalProperties:false`) with the optional fields plus valid/invalid examples.

### Pitfall 7: Secrets reaching logs through side doors
**What goes wrong:** the sentinel test passes today only because nothing logs a token yet. New candidates: Npgsql parameter logging (off by default; keep it off), exception text from stubs, structured log templates that capture the grant, `ToString()` of the grant.
**How to avoid:** Application layer stays logging-free; Host logs ids only (tenant, agent, proposal id, 12-char hash prefix, reason code, never trace_ref values or tokens); stub exceptions carry static reason strings. Add Trace-level log lines in the Host step on purpose (so the Trace-level scan has something to prove) and include a test that logs a `ServiceTokenGrant` and a `SessionKey` as structured arguments and asserts `[REDACTED...]`.

### Pitfall 8: Token near key expiry
**What goes wrong:** a key with seconds left makes `min(token.exp, key.exp) - skew` non-positive; caching it would immediately evict, or worse, hand out a token past the key.
**How to avoid:** if `usableUntil <= now`, use the freshly issued token for this request only and do not cache. Never extend `usableUntil` beyond either expiry. The auth handler already rejects keys with `ExpiresAt <= now`.

### Pitfall 9: Cache growth
**What goes wrong:** an indefinite trace cache keyed by caller-supplied `trace_ref` is a memory-growth vector, and only *found* traces enter it, but a caller with a valid tenant can still fill it.
**How to avoid:** hard cap (e.g. 10,000 entries per cache); when full, clear or evict an arbitrary entry. Correctness never depends on the cache (D§16.4). Test: cap behaviour does not change a verdict.

### Pitfall 10: Alarm that is just a log line a test cannot assert
**How to avoid:** `IOperationalAlarm.Raise(AlarmCode code, string component)` with static codes. `LoggingOperationalAlarm` writes a Critical log with category `Olive.Alarm`; tests replace it with a recorder. Alarms must carry no request data.

## Code Examples

### Recording decorator capturing destination and secret exposure (IDN-08, Q19 port level)
```csharp
// Source: pattern of existing RecordingKeyIntrospector in tests/Olive.Tests/TestDoubles.cs
public sealed record OutboundCall(string Port, OutboundDestination Destination, bool CarriedSessionKey, bool CarriedServiceToken);

public sealed class RecordingTokenIssuer(ITokenIssuer inner, List<OutboundCall> calls, List<string> events) : ITokenIssuer, IComponentDecorator
{
    public object Inner => inner;
    public async Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct)
    {
        lock (calls) { calls.Add(new("ITokenIssuer", OutboundDestination.Popeye, CarriedSessionKey: true, CarriedServiceToken: false)); events.Add("token"); }
        return await inner.IssueAsync(key, ct);
    }
}
// The resolver recorder sets CarriedServiceToken: true, CarriedSessionKey: false (it cannot compile otherwise).
// Test: calls.Where(c => c.CarriedSessionKey).All(c => c.Destination == OutboundDestination.Popeye)
```

### Architecture test: only popeye-destined ports take a `SessionKey`; only hometown-destined take a `ServiceTokenGrant`/`ServiceToken`
```csharp
// Source: Q19 default ("port-level capture, backed by architecture tests"); mirrors ArchitectureTests style
var ports = typeof(Olive.Application.AssemblyMarker).Assembly.GetTypes()
    .Where(t => t.IsInterface && t.GetCustomAttribute<OutboundPortAttribute>() is not null);
foreach (var port in ports)
{
    var dest = port.GetCustomAttribute<OutboundPortAttribute>()!.Destination;
    foreach (var p in port.GetMethods().SelectMany(m => m.GetParameters()))
    {
        if (p.ParameterType == typeof(SessionKey))
            Assert.Equal(OutboundDestination.Popeye, dest);
        if (p.ParameterType == typeof(ServiceToken) || p.ParameterType == typeof(ServiceTokenGrant))
            Assert.Equal(OutboundDestination.Hometown, dest);
        Assert.True(p.ParameterType != typeof(string) || dest is not OutboundDestination.Hometown || p.Name == "traceId"); // no stringly-typed smuggling
    }
}
// Also assert: every interface in Olive.Application that is implemented by a class in Olive.Integrations.* carries [OutboundPort].
```

### Sentinel scan across everything an observer or auditor could read
```csharp
// Source: DECISION section 17.4 last row; existing SecretLeakageTests.EverythingAsync
// Build: unique session key registered in StubKeyIntrospector(map) + StubTokenIssuer with a recorded token value.
// Scan: HTTP bodies + all headers + factory.Logs.Text (min level Trace) + every text column of proposal and
//       proposal_event (select row_to_json(t)::text from proposal t) read as olive_reader per tenant.
// Assert none contain sentinelKey or sentinelToken. Make the helper reusable: Phase 6 adds the JSONL file.
```

### Fixture file set (committed under `contracts/trace-resolution/`)
```jsonc
// trace-resolution-response.schema.json: exactly 7 required properties, additionalProperties:false
// match.json
{ "trace_id": "0a1b2c3d-0000-4000-8000-0000000000a1", "schema_version": "ate/0.4",
  "run_id": "run-fixture-1", "tenant_id": "5325ffe9-5727-4d88-9832-aebabb8d0538",
  "agent_id": "liam", "proposal_hash": "<64 hex>", "created_at": "2026-10-20T10:00:00Z" }
// match.proposal.json = the submitted four fields; test asserts proposal_hash == Jcs.ProposalHash(this)
// Cases: match, run-null-ate03, run-differs, tenant-differs, agent-null, agent-differs, hash-null, hash-differs
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Not-found stored as a permanent refusal (D§14.3, D§15.2) | `422 trace_not_found`, not stored, retryable | D§16.3, 2026-10-20 | Retry under the same `run_id` must work; never cache 404 |
| Check order run -> tenant -> agent (D§15.1/16.2) | run -> tenant -> agent -> proposal hash | D§17.3 | Four stored reason codes |
| Tenant from a request parameter / credential without tenant (D§16.1) | popeye tenant-scoped service tokens with `delegated_agent_id` | D§17.1 | Olive needs a token issuer port, cache and claim validation |
| `principal_type = service` | `application` (vocabulary has no `service`) | D§17.1 correction | Token principal is `application` |
| POPEYE `key_id` free-form | Lowercase canonical UUID | POPEYE_v0.4.3 | Stub already conforms |

**Deprecated/outdated:** `trace_verification: mismatch/verified` values (rejected in D§16.5); the hand-written 403 for non-agent keys only exists because of stub fixtures (see Spec Reconciliation).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The popeye token-issuance interface is `IssueAsync(SessionKey) -> {token, tenant_id, delegated_agent_id, key_id, expires_at}` or a refusal; no popeye spec defines it | Pattern 2, Standard Stack | popeye's eventual contract differs; only the stub and v2 transport change, the Application orchestration survives. Needs popeye owner review before LIVE-02 |
| A2 | The stub issuer derives tenant and agent by introspecting the key via `StubKeyIntrospector` fixtures and applies an Olive registration table (three POC tenants, agent `liam`) | Standard Stack | Low: it is a stub; wording in D§17.1 item 5 supports it |
| A3 | Default token cache skew of 30 s ("shortly before") | Pattern 3 | Low; any positive skew satisfies "never past either expiry" |
| A4 | Default resolver timeout of 5 s | Open Questions | Low; tunable |
| A5 | 201 Created (not 200) for a newly stored `refused_policy` proposal, with `refusal_reason` in the body | Open Questions | Liam's S-OL-1 stub contract says `200 {status: refused_policy, reason}`; the compatibility review (Phase 9) must map it. Medium |
| A6 | One public 503 reason `trace_verification_unavailable` for all fail-closed causes | Open Questions | Low; alarms/logs carry the cause |
| A7 | `trace_ref` is used verbatim as hometown's `trace_id`; fixtures key on it | Spec Reconciliation | If hometown later wants a different mapping, the live transport adapts |
| A8 | Microsoft.Extensions.Caching.Memory would be available in the shared framework (not used) | Alternatives Considered | None; not recommended |
| A9 | Real popeye returns `active:false` (so 401) for non-agent keys; Phase 3 keeps the stub divergence | Spec Reconciliation | Cosmetic: 401 vs 403 against live popeye |

## Open Questions / Recommended Defaults

Planner: lock each of these (or send back to the user). All are Phase 3 scope.

1. **Port shapes and where they live.** Default: `ITraceResolver`, `ITokenIssuer`, `IOperationalAlarm` in `Olive.Application`; stubs in `Olive.Integrations.Hometown` / `.Popeye`; `[OutboundPort]` attribute in Application. Rationale: dependency direction (FND-01) and the Q19 architecture test.
2. **Stored-refusal HTTP shape.** Default: `201 Created` + `Location` + `{proposal_id, state:"refused_policy", refusal_reason}`; replay `200` same body. Rationale: consistent with Phase 1 D-09/Phase 2 replay semantics; the refusal is a created resource. Flag for Phase 9 COMPATIBILITY.md: Liam's S-OL-1 stub expects `200` for refusals and `202` for acceptance. Phase 4 reuses this path for policy refusals.
3. **503 reason codes.** Default: one public reason `trace_verification_unavailable`; distinct alarm codes: `trace_resolver_caller_rejected`, `token_issuer_caller_rejected`, `token_issuer_refused_scope`, `token_claims_mismatch`, `token_outlives_session_key`, `trace_response_malformed`, `introspection_caller_rejected`. Plain unavailability and timeouts do not alarm (they are logged at Warning), because D§16.2 reserves the alarm for Olive's own credential or registration failing.
4. **Token-before-resolver ordering on a trace-cache hit.** Default: always obtain (cached) token first, then consult the trace cache. Alternative (skip the token when the trace is cached) saves a call but skips popeye's registration check and the check-4 reference value.
5. **Fixture resolver tenant scoping.** Default: no scoping; return the fixture verbatim. README states real hometown 404s cross-tenant and check 3 "should never fire".
6. **Fault injection.** Default: test doubles (`FaultingTraceResolver`: unavailable, hang, 401, 403, malformed; `FaultingTokenIssuer`: unavailable, refused tenant, refused agent, mismatched claims, over-long expiry), not fixture files.
7. **Resolver timeout.** Default 5 s, optional setting `OLIVE_TRACE_TIMEOUT_MS`; tests pass a small value via `extraConfig`. If the planner prefers zero new configuration, hard-code the constant and expose it only to tests via a constructor parameter.
8. **Interpretation of TRC-09 "every proposal".** Default: every proposal that reaches trace verification. Rows refused earlier (Phase 4 `not_in_catalog`) and legacy rows keep `trace_verification` null.
9. **Cache bounds.** Default 10,000 entries per cache, clear-on-full.
10. **Introspection caller-rejected outcome.** Default: add it now (small) so TRC-07's "popeye 401/403 -> 503 + alarm" covers introspection, not only the new issuer.
11. **Non-agent key 403 vs POPEYE section 2.1.** Default: leave as is; document in COMPATIBILITY.md at Phase 9 (see Spec Reconciliation).
12. **JCS golden set.** Default: no new vectors, but ship the fixture/proposal pairs as additional cross-checks and add them to the hand-off note to the hometown owner. The Q20 confirmation remains a manual item that Phase 2 already recorded.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.400 (global.json pins 10.0.204 with rollForward latestFeature) | - |
| Docker (Testcontainers Postgres) | integration tests, migration 0006 tests | client present (29.7.2); daemon state not verified this session | - | Run non-integration subset: `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| Live popeye / hometown | LIVE-01/02 | no (not needed) | - | In-process stubs (this phase's scope) |
| `~/fiskroad/specs/POPEYE_v0.4.2.md` | D§4 stop rule | yes | v0.4.2 (and v0.4.3) | - |
| slopcheck | package audit | not run (no packages) | - | - |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** Docker daemon unverified; Phase 2 ran 548 tests with Docker, so assume available on the dev machine and CI.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing 10.0.13 |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration`, `Category=Architecture`) |
| Quick run command | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| Full suite command | `dotnet test tests/Olive.Tests` (needs Docker) |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| TRC-01 | Fixtures conform to the seven-field schema; fixture hash equals `Jcs.ProposalHash` of its proposal | unit | `dotnet test --filter "FullyQualifiedName~TRC_01"` | Wave 0 (new) |
| TRC-02 | Unknown trace -> 422, `retryable:true`, zero rows; fixture added; same `run_id` retry -> 201 | integration | `--filter "FullyQualifiedName~TRC_02"` | Wave 0 |
| TRC-03 | Null `run_id` with `ate/0.3` and differing `run_id` -> stored `trace_unresolved`; `trace_schema_version` recorded | integration | `~TRC_03` | Wave 0 |
| TRC-04 | Forced tenant mismatch -> `trace_tenant_mismatch` | integration | `~TRC_04` | Wave 0 |
| TRC-05 | `agent_id` null / differs from token `delegated_agent_id` -> `trace_agent_mismatch` | integration | `~TRC_05` | Wave 0 |
| TRC-06 | One char changed in `justification`; hash null; key-reordered `parameters` still passes; unknown key in `parameters` is hashed not stripped | integration + unit | `~TRC_06` | Wave 0 |
| TRC-07 | Resolver unavailable/hang/401/403/malformed -> 503, alarm only where specified, zero rows; popeye 403 on introspection -> 503 + alarm | integration | `~TRC_07` | Wave 0 |
| TRC-08 | Second resolution uses cache (inner call count 1); 404 then 200 reaches resolver twice; 503/401/403 never cached | unit | `~TRC_08` | Wave 0 |
| TRC-09 | Source is `fixture` from the fixture resolver; CHECK rejects other values (direct SQL as owner); production + fixture resolver refuses start | integration | `~TRC_09` | Wave 0 |
| IDN-05 | Stub issues `application`-claim grants with all four fields; unregistered tenant/agent refused | unit | `~IDN_05` | Wave 0 |
| IDN-06 | Cache hit; new `key_id` -> new token; expiry-after-key rejected and uncached; expired token never reused (`ManualTimeProvider`) | unit | `~IDN_06` | Wave 0 |
| IDN-07 | Tenant or agent disagreement -> discarded, 503, alarm, zero rows | unit + integration | `~IDN_07` | Wave 0 |
| IDN-08 | Reflection test on `[OutboundPort]`; recorded calls show key only on popeye ports; sentinel scan of responses, Trace logs, stored rows | architecture + integration | `~IDN_08` | Wave 0 |
| Order evidence (feeds API-08 in Phase 4) | Events list shows introspect -> FindByRun -> token -> resolve -> insert; replay/409/400 produce no `token`/`resolve` events | integration | `~TRC_ORDER` | Wave 0 |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **Per wave merge:** `dotnet test tests/Olive.Tests`
- **Phase gate:** full suite green, `dotnet restore --locked-mode`, vulnerable-package gate in CI, before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/Olive.Tests/ManualTimeProvider.cs`, `RecordingPorts.cs` (resolver/issuer/alarm recorders, fault doubles), `SentinelScan.cs`
- [ ] `OliveApiFactory` trace mode (auto-match default for legacy tests, explicit for Phase 3 tests) and registration of recording decorators on all ports
- [ ] Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` for 0006
- [ ] New `StartupGuardTests` case: production + `FixtureTraceResolver` (and + `StubTokenIssuer`), including behind a `CachingTraceResolver` decorator
- [ ] Contract examples for the changed submit/GET response schemas; `ContractSchemaTests` additions
- [ ] No framework install needed

## Security Domain

`security_enforcement` is enabled in config (ASVS level 1, block on high).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes (service-to-service) | popeye-issued, tenant-scoped, short-lived service tokens; claim cross-check against Olive's own introspection; fail closed on 401/403 |
| V3 Session Management | yes (credential lifetime) | Token never outlives the session key; cache keyed on `key_id`; introspection on every request means revocation takes effect immediately |
| V4 Access Control | yes | Tenant from introspection only; check 3 re-verifies tenant (second layer next to RLS); agent binding via `delegated_agent_id` |
| V5 Input Validation | yes | Strict seven-field parse of resolver responses; proposal validated non-mutating before hashing; reason codes are static |
| V6 Cryptography | yes | SHA-256 over JCS (existing); `RandomNumberGenerator` for stub tokens; never custom crypto |
| V7 Error handling and logging | yes | Static problem details; ids only in logs; secrets redacted; sentinel scan |
| V9 Communications | deferred | No outbound transport in Phase 3 (LIVE-01/02/03) |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Agent carries another agent's traced suggestion | Spoofing | Check 4 against token `delegated_agent_id`, which popeye derives from the introspected key |
| Agent alters `parameters`/`justification` after tracing | Tampering | Check 5 four-field JCS hash, no mutation before hashing |
| Stale/over-long token outlives revoked or expired session | Elevation of privilege | Cache key includes `key_id`; expiry = min of both minus skew; over-long token rejected not cached |
| Popeye/Olive bug yields a token for the wrong tenant or agent | Elevation of privilege | Claims re-checked against Olive's introspection; discard + 503 + alarm |
| Session key leaks to hometown or logs | Information disclosure | Port signatures (architecture test), recording decorators, redacting types, sentinel scan at Trace level |
| Probing for other tenants' traces | Information disclosure | Identical 404/422 for missing and foreign traces (hometown rule 3); Olive's 422 body is static |
| Resolver outage turns into accepted proposals | Denial/Tampering | Fail closed 503, nothing stored; no fallback to unverified |
| Not-found turned into a permanent refusal | Denial of service | 422 not stored, never cached (TRC-02/08) |
| Fixture resolver in production | Spoofing | `INonProductionOnly` + guard walks decorators; test with a wrapped resolver |
| Memory exhaustion via distinct trace refs | Denial of service | Bounded caches |
| Secrets in exception messages | Information disclosure | Static exception reasons; handler logs only the type name |

## Sources

### Primary (HIGH confidence)
- `specs/DECISION_olive-poc2-phase1_v0.5.md` sections 4, 6, 7, 13-17 (read in full for this phase)
- `~/fiskroad/specs/POPEYE_v0.4.2.md` (all) and `POPEYE_v0.4.3.md` (all, diffed against v0.4.2)
- `~/fiskroad/specs/NONO_POPEYE_SESSION_CREDENTIAL_CONTRACT_v0.1.md` (relevance check)
- `~/hometown/specs/HOMETOWN_v0.4.1.md` section 2.5 (resolution endpoint, rules 1-6, REQ-PROV-19..27)
- `~/liam/specs/LIAM_DESIGN_SPEC_v0.4.1.md` sections 7.2-7.4 (S-OL-1 response handling)
- Repository code read this session: `ProposalEndpoints.cs`, `AgentKeyAuthenticationHandler.cs`, `StubKeyIntrospector.cs`, `IKeyIntrospector.cs`, `IComponentDecorator.cs`, `SessionKey.cs`, `ServiceToken.cs`, `PostgresProposalStore.cs`, `ProposalSql.cs`, `StartupGuards.cs`, `Program.cs`, migrations 0001, 0003-0005, `OliveApiFactory.cs`, `TestDoubles.cs`, `SecretLeakageTests.cs`, `ArchitectureTests.cs`, `PrivilegeMatrixTests.cs` (grep), `BannedSymbols.txt`, `Directory.Packages.props`
- `.planning/REQUIREMENTS.md`, `STATE.md`, `ROADMAP.md`, Phase 1/2 CONTEXT and summaries

### Secondary (MEDIUM confidence)
- Absence searches: no popeye spec mentions service-token issuance (`grep -rl delegated_agent_id` across popeye, fiskroad, liam, hometown, bluto found only hometown specs). A negative claim; reasonably thorough but not exhaustive of unpublished drafts.

### Tertiary (LOW confidence)
- Docker daemon availability (client version seen, daemon not probed).

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - no new dependencies; every building block already exists in the repo.
- Architecture: HIGH for ordering, storage, and check logic (spec-fixed); MEDIUM for the token-issuer port shape (Olive-defined, popeye unspecified).
- Pitfalls: HIGH - each is traceable to a specific file or constraint read this session (grants in 0003, exact-property assertion in `API_01`, absent introspection caller-rejected outcome, banned APIs, allowlist test).

**Research date:** 2026-10-22
**Valid until:** 30 days, or until popeye publishes a token-issuance spec or hometown ratifies section 2.5 (whichever is first)

## Suggested Plan Decomposition (for the planner)

1. **Contracts and domain constants (Wave 1):** `contracts/trace-resolution/` schema, fixtures + paired proposals, README (includes popeye hand-off and assumptions); `TraceVerificationSources`, `TraceRefusalReasons`; response/problem schema additions with examples.
2. **Ports, orchestration, caches (Wave 1, parallel):** Application types (`ITraceResolver`, `ITokenIssuer`, `ServiceTokenGrant`, `ServiceTokenProvider`, `CachingTraceResolver`, `TraceVerifier`, `IOperationalAlarm`, `OutboundPortAttribute`) with unit tests for TRC-06/08 and IDN-06/07 using `ManualTimeProvider`.
3. **Stubs (Wave 2):** `FixtureTraceResolver` + strict parser (Hometown), `StubTokenIssuer` (Popeye), introspection caller-rejected outcome; `INonProductionOnly`; startup-guard tests.
4. **Persistence (Wave 2, parallel with 3):** migration 0006, `InsertRefusedAsync`, `FindByRun` returning `refusal_reason`, privilege-matrix and migration-runner test updates.
5. **Endpoint integration (Wave 3):** step 6 in `PostAsync`, problem mapping (422 `retryable`, 503), `LoggingOperationalAlarm`, DI wiring in `Program.cs`, `OliveApiFactory` trace modes and legacy-test auto-match; TRC-02..09 and order-evidence integration tests.
6. **Containment evidence (Wave 4):** `[OutboundPort]` architecture test, recording decorators on all ports, `SentinelScan` over responses, Trace logs and stored rows (JSONL half noted for Phase 6); STATE.md coordination items (popeye issuance spec, Q20 hand-off, 201-vs-200 for compatibility).
