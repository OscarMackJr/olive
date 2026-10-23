# Phase 4: Policy, Expiry and Refusal - Research

**Researched:** 2026-10-22
**Domain:** ASP.NET Core minimal API + Dapper/Npgsql + PostgreSQL (RLS, advisory locks), brownfield extension of Phases 1-3
**Confidence:** HIGH (all findings come from reading the current code and specs; no new libraries)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
Carried forward, not re-decided: Phase 3 D-01..D-05 (a new refusal returns 201 with `{proposal_id, state, refusal_reason}`, a replay returns 200 with no re-evaluation, and the stored-refusal path is insert then CAS plus events in one tenant transaction). Q3 also carries forward: `expires_in_seconds` ≤0 or above the maximum gets 400 and is never clamped; an expired key gets 401.

- **D-01 (Q4 ruling):** Proceed; do not stop under D§4. POPEYE_v0.4.2 §2.5 and §5 assign resolving `on_behalf_of` to directory groups to Olive; vocabulary §3.2 defines the intersection rule. Record the reasoning in phase artifacts and close the STATE.md "Q4 narrowed" item.
- **D-02:** New read-only port `IDirectoryGroupResolver` in `Olive.Application`, `[OutboundPort]`. Takes `(tenant_id, on_behalf_of)`, returns the user's directory groups or a typed outcome for unknown user / unavailability. Fixture-backed stub, `INonProductionOnly`, barred in production by existing startup guards. No live Entra/Graph call, no credential.
- **D-03:** Optional `required_entitlement` (e.g. `ent:crm.case_note.write`) on each catalog entry. Authorization passes only if the user's groups contain it AND the agent's own entitlements contain it. Never a union. `crm.case_note@1` gets a value. Catalog v1 schema change is additive.
- **D-04:** Agent entitlements come from the source-controlled agent allowlist YAML: each agent entry (`liam`) gains `entitlements:`. Strict parsing, no runtime registration.
- **D-05:** Unknown user, empty groups, or missing required entitlement = stored refusal `entitlement_missing`. Resolver unavailable/timeout = 503, nothing stored, `retryable: true`, `Retry-After` (mirror Phase 3 D-06/D-09; one public reason code, cause in logs only). Rejection of Olive's own credential/registration raises an `IOperationalAlarm` with its own stable code. If an action has no `required_entitlement`, decide in planning whether that fails closed. Default: entries must declare one.
- **D-06:** `on_behalf_of` is passed to the port verbatim as an opaque string. Fixtures key on existing UPN-style placeholders. Q14 affects only the future live adapter and stays open.
- **D-07:** No caching of directory lookups in Phase 4.
- **D-08:** Sliding window counted from proposal rows. Under `pg_advisory_xact_lock` keyed on a hash of `(tenant_id, agent_id, action_id)`, count proposals with `created_at > now − window_seconds`. No counter table. Add index on `(tenant_id, agent_id, action_id, created_at)`. Limits from catalog `max_rate` (`limit`, `window_seconds`).
- **D-09:** Accepted = any state except `refused_policy`. Refusals never consume the limit.
- **D-10:** Scope is `(tenant_id, agent_id, action_id)`; the count runs inside the RLS tenant transaction.
- **D-11:** Rate-limited = stored `refused_policy` with reason `rate_limited`, 201, replay returns the same refusal. Not a 429.
- **D-12:** Rate is the last policy check. Lock taken only when purpose and entitlement passed, held through the accept CAS in the same transaction. A concurrency test proves N parallel submissions above the limit accept exactly `limit`.
- **D-13:** New **required** catalog field `max_expiry_seconds`, validated at startup (>0). `crm.case_note@1` = `28800`. Additive to catalog v1 schema.
- **D-14:** `expires_at = now + min(expires_in_seconds ?? 8h, max_expiry_seconds)`, then capped at the key's `expires_at`. Omitted `expires_in_seconds` defaults to `min(8h, max_expiry_seconds)` and never errors. `now` from injected `TimeProvider`.
- **D-15:** Cap at key's `expires_at` is silent (no 400). Q3's 400 applies only when `expires_in_seconds` ≤0 or > `max_expiry_seconds`.
- **D-16:** `expires_in_seconds` is an additive optional integer (minimum 1) in `contracts/proposal/v1`, with valid/invalid examples. Part of the JCS body (feeds `body_sha256`/idempotency), NOT part of the four-key `proposal_hash`. `GET /v1/proposals/{id}` exposes `expires_at`. The 400 happens at the schema/catalog step; nothing is stored.
- **D-17:** Reason codes in evaluation order: `not_in_catalog`, `purpose_not_permitted`, `entitlement_missing`, `rate_limited`. snake_case, stored in `refusal_reason` beside `trace_*`. Drop legacy scaffold rule IDs.
- **D-18:** First failure wins; `refusal_reason` is a single code; later checks do not run (rate lock never taken for an already refused proposal).
- **D-19:** `not_in_catalog` is stored at the schema/catalog step (API-08 step 4): an unknown `action_type` that passes the envelope schema is immediately stored `refused_policy` / `not_in_catalog`, no trace resolution, no further policy, still gets a `policy_evaluated` event.
- **D-20:** `policy_evaluated` payload `{outcome: "allowed"|"refused", reason, checks: [{name, result}]}`. Check names static (`catalog`, `purpose`, `entitlement`, `rate`); unrun checks omitted or marked not evaluated. Never contains parameter values or group lists. Emitted for allowed proposals too.

### Claude's Discretion
- **Lifecycle guards (POL-06):** full transition table in `Olive.Domain` (extend `ProposalStates`); every transition is a single `UPDATE … WHERE state = @expected` with row-count check plus a `proposal_event` insert in the same transaction. Unit-test the illegal-transition matrix completely now. End to end only `proposed → refused_policy | awaiting_approval` is exercised; later edges wired in Phases 5-6.
- **API-08 order test:** a request crafted with several faults, peeled off one at a time, asserting the first failing step's outcome each time. Recording decorators / step trace may also be used.
- Port placement, stub fixture location (e.g. `contracts/directory/` with README and schema), alarm and reason code names for the directory resolver.
- Migration number continues after `0006_trace_verification.sql`. Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` for new columns/grants.
- Whether the policy engine is a pure domain evaluator (catalog entry, principal, groups, rate count → verdict) with DB lock/count orchestrated in the store. Recommended: yes.

### Deferred Ideas (OUT OF SCOPE)
- Directory lookup caching and the live Entra/Graph adapter (after Q14 ruling).
- Q14 UPN vs oid+tid ruling (before Phase 5/7; unchanged here).
- Queueing (Phase 5), expiry enforcement on approve/execute POL-08 (Phase 5), execution (Phase 6).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| POL-01 | Out-of-purpose proposal stored `refused_policy`, never queued | Pure `PolicyEvaluator` purpose check against typed `CatalogEntry.RequiredPurpose`; reuse `InsertRefusedAsync` path |
| POL-02 | Authorization = intersection of agent and `on_behalf_of` entitlements, fail closed | `IDirectoryGroupResolver` + allowlist `entitlements`; typed outcome split (refuse vs 503) |
| POL-03 | Rate limit counts accepted only, DB-enforced under advisory lock | New store method: lock → count → insert → CAS in one tenant tx; `hashtextextended` lock key; new index |
| POL-04 | `policy_evaluated` event per evaluation with outcome and reasons | `proposal_event.detail jsonb` already exists; `InsertEvent` SQL needs a `detail` parameter |
| POL-05 | Retry of stored refusal returns same refusal, no re-evaluation | Already satisfied by `DecideExistingAsync` (200 + stored reason) which runs before policy; only needs tests for policy reasons |
| POL-06 | Every transition is CAS + `proposal_event`; illegal rejected | Transition table in Domain; generalize `TransitionAsync`; row-count check |
| POL-07 | Expiry default 8h, per-action max, shorten only, capped at key expiry, absolute | New `expires_at` column, `max_expiry_seconds` catalog field, schema additive field, 400 on out-of-range |
| API-08 | Fixed submission order asserted; first failure wins | Existing `Events` recorder in `OliveApiFactory`; add directory + policy markers |
</phase_requirements>

## Summary

Phase 4 is a brownfield extension with no new libraries. Everything needed already exists in the repo: `PostAsync` in `src/Olive.Host/Endpoints/ProposalEndpoints.cs` already runs introspect (auth handler) → principal checks → body gate → idempotency (`DecideExistingAsync`) → schema/catalog (`SubmissionValidator`) → trace (`TraceVerifier`) → insert. Phase 4 inserts a policy step between trace and persist, converts the unknown-action 400 into a stored refusal, adds `expires_at`, and generalizes `TransitionAsync` into a guarded state machine. POL-05 is already satisfied structurally (replay path precedes any evaluation); the work is tests plus making sure the new policy refusals use the same stored path.

The one non-trivial design point is the rate limit: the lock, count, insert and accept CAS must all be inside one tenant transaction, so policy cannot be a purely "decide, then call `InsertAsync`" sequence. Recommended shape: a pure domain `PolicyEvaluator` decides purpose and entitlement before any DB work; then a single new store method `InsertEvaluatedAsync` takes the advisory lock, counts, inserts the row, and either accepts (CAS `proposed → awaiting_approval`) or refuses `rate_limited`, writing all events in the same transaction.

Several findings change or sharpen the CONTEXT: the `refusal_reason` check constraint is a format regex, not a pinned list, so no constraint change is needed for new reason codes (migration 0006 comment says so); `created_at` is DB-set (`now()`, not insertable by `olive_app`), so the sliding window must use the DB clock, not the injected `TimeProvider`; `OutboundDestination` has only `Popeye` and `Hometown`, so the new `[OutboundPort]` needs a third enum value; and existing tests (`API_05_unknown_action_type_gives_non_stored_400_not_in_catalog`, `SubmissionValidatorTests`) encode the old behavior and must be rewritten.

**Primary recommendation:** Build a pure `PolicyEvaluator` in `Olive.Domain`/`Olive.Application`, an `IDirectoryGroupResolver` stub in the existing empty `Olive.Integrations.Entra` project, and one transactional store method that owns lock+count+insert+accept; extend the existing `Events` recorder for the API-08 order test.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Purpose / entitlement verdict | Domain (pure evaluator) | — | No I/O; unit-testable matrix |
| Directory group lookup | Integrations.Entra port + stub | Host (503 mapping) | Outbound read; fail-closed signalling lives in Host |
| Rate count + lock | Database / Persistence | — | Must be atomic with insert/CAS (POL-03) |
| Transition table | Domain (`ProposalStates`) | Persistence (CAS SQL) | Legality in Domain, atomicity in SQL |
| expires_at computation | Host/Application (uses TimeProvider) | Persistence (stores) | Needs catalog max + key expiry + clock |
| Submission order | Host endpoint | Tests (recorder) | Linear in `PostAsync` |
| Catalog/allowlist fields | Catalog project | — | Source-controlled YAML, strict parse |

## Standard Stack

No new packages. Reuse what is pinned (`Directory.Packages.props`, lockfiles are enforced by `FND_02_direct_packages_match_allowlist_in_lockfiles`).

| Library | Version | Purpose |
|---------|---------|---------|
| Npgsql / Dapper | existing | SQL, advisory lock, CAS |
| YamlDotNet | existing (Olive.Catalog) | Agent allowlist `entitlements`, catalog fields |
| JsonSchema.Net | existing | Catalog/envelope schema (additive fields) |
| xUnit v3 3.2.2, Testcontainers.PostgreSql 4.16.0, Mvc.Testing | existing | Tests (postgres:17-alpine) |

**Package Legitimacy Audit:** Not required, phase installs no external packages. Do not add any: `ArchitectureTests` pins direct packages per project and `REQ_ACT_04` bans HTTP/socket assemblies, so the directory stub must be fixture-only.

## Architecture Patterns

### Submission flow after Phase 4

```
POST /v1/proposals
  1 introspect (auth handler)           -> 401 key_inactive / 503
  2 principal checks                    -> 403
  3 body gate (content-type, size, I-JSON)
  4 idempotency FindByRun               -> 200 replay | 409 conflict   (POL-05: no re-evaluation)
  5 schema/catalog (SubmissionValidator)
      envelope schema violation         -> 400
      action not in catalog             -> STORE refused_policy/not_in_catalog (201), event policy_evaluated   [D-19]
      parameter schema violation        -> 400
      expires_in_seconds > max          -> 400 (nothing stored)                                                 [Q3]
  6 trace (TraceVerifier)               -> 503 | 422 | stored trace_* refusal (201)
  7 policy
      purpose (pure)                    -> refused purpose_not_permitted
      entitlement (resolver port)       -> 503 unavailable (nothing stored) | refused entitlement_missing
      rate (DB: lock, count)            -> refused rate_limited
  8 persist: insert + CAS proposed->awaiting_approval + events (same tx)  -> 201 awaiting_approval
```

### Component placement

```
src/Olive.Domain/        PolicyRefusalReasons.cs, PolicyEvaluator.cs (pure), ProposalStates transition table
src/Olive.Application/   IDirectoryGroupResolver.cs (+ result types), AlarmCodes additions, IProposalStore additions,
                         OutboundDestination.Directory
src/Olive.Integrations.Entra/  FixtureDirectoryGroupResolver (INonProductionOnly), embedded fixtures
src/Olive.Catalog/       CatalogEntry typed fields, AgentAllowlist entitlements
sql/migrations/0007_policy_expiry_rate.sql
contracts/directory/     README + schema + fixture json (mirror contracts/trace-resolution)
```

### Pattern 1: Pure policy evaluator
Inputs: `CatalogEntry` policy fields (required_purpose, required_entitlement), `ActiveKey.Purpose`, agent entitlements, user groups (or a "groups unavailable-vs-empty" typed input). Output: `PolicyVerdict { Allowed | Refused(reason) , Checks[] }`. Static check names. Purpose runs first, so the resolver is not called for out-of-purpose proposals (D-18); assert that with the recording double.

### Pattern 2: Single transactional store method for accept-or-rate-refuse
Within one `TenantUnitOfWork.ExecuteAsync` (READ COMMITTED by default in Npgsql, which is what makes lock-then-count correct):
1. `select pg_advisory_xact_lock(hashtextextended(@tenant || '|' || @agent || '|' || @action, 0))` (server-side hash, PG 11+, no C# hash drift) [ASSUMED: function availability confirmed by memory of PG docs; Testcontainers uses postgres:17 so it exists].
2. `select count(*) from proposal where tenant_id=@t and agent_id=@a and action_type=@x and state <> 'refused_policy' and created_at > now() - make_interval(secs => @window)` — taken BEFORE inserting the new row so the row does not count itself.
3. Insert (existing `ProposalSql.Insert`, `on conflict do nothing`); null → duplicate, return null (lock released at commit).
4. count >= limit → refuse path (`RefuseWithReason` CAS, reason `rate_limited`), else CAS `proposed → awaiting_approval`; events `submitted`, `policy_evaluated`, transition.
The lock is held until commit, so a second submitter blocks, then (new READ COMMITTED statement snapshot) sees the first's committed row. No SERIALIZABLE needed. Deadlock risk is nil (one lock per tx).

### Pattern 3: Generalized guarded transition
Domain: `ProposalStates.CanTransition(from,to)` backed by a static table: `proposed→{refused_policy,awaiting_approval}`, `awaiting_approval→{approved,rejected,expired}`, `approved→{executed,execution_failed,expired}`; all others terminal/illegal. Store: `TransitionAsync` calls the table first (throw `ArgumentException`/typed result before DB), then `UPDATE … WHERE state=@from` row-count check + event. Keep existing rule that `refused_policy` is only written by the refusal path (migration 0006 `proposal_refusal_state_agrees` check forces a reason with it). Unit test the full 8x8 matrix.

### Anti-Patterns
- Evaluating policy then calling `InsertAsync` then counting: races the limit. Count must be inside the locked tx.
- Counting the in-flight proposal's own row (off-by-one).
- Putting group lists, parameters or `on_behalf_of` into the `policy_evaluated` detail or logs.
- Calling the directory resolver before the purpose check or for already refused proposals.
- Using `TimeProvider` for the rate window (rows carry DB `created_at`).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| Rate limiter | In-process counters, counter table | Advisory lock + count of `proposal` rows (D-08) |
| Lock key hash | C# string hash | `hashtextextended(text, 0)` in SQL |
| YAML parsing | Line parser | Existing YamlDotNet strict path / `YamlToJson` |
| Schema validation of new catalog fields | Manual checks only | Extend `action-type.schema.json` (embedded) plus loader >0 check |
| Idempotent refusal replay | New logic | Existing `DecideExistingAsync` |
| Refusal write | New SQL path | `InsertCoreAsync` + `RefuseWithReason` |

## Runtime State Inventory
Not a rename phase. Omitted. (Note: existing rows predate `expires_at`, so the column must be nullable.)

## Common Pitfalls

1. **`created_at` is DB time, not `TimeProvider` time.** `olive_app` has no INSERT grant on `created_at` (0003). Window logic must use `now()` in SQL. Tests that need the window to slide cannot advance `ManualTimeProvider`; either use a tiny `window_seconds` with a test catalog built via `CatalogLoader.Parse`/`CatalogSet`, or backdate rows through the owner connection. Also `now()` is transaction-start time, so a tx that waited for the lock has a slightly earlier `created_at` than its commit order; harmless for a sliding window, document it. Expiry (`expires_at`) uses the injected `TimeProvider` (D-14), so those tests can use `ManualTimeProvider`.
2. **Refusal reason constraint is regex, not enum.** `proposal_refusal_reason_format` allows `^[a-z][a-z_]{0,63}$`. New codes need no constraint change; CONTEXT's "extend the check constraint" is unnecessary. Optionally add a stricter allowlist constraint, but it is not required.
3. **Existing tests encode old behavior.** `ApiIntegrityTests.API_05_unknown_action_type_gives_non_stored_400_not_in_catalog` and `SubmissionValidatorTests` (NotInCatalog tests) / the comment "Phase 4 replaces this seam" in `SubmissionValidator.LookupAction`. `SubmissionOutcome.NotInCatalog` can stay as the validator outcome; the endpoint now stores instead of returning 400. `ProblemReasons.NotInCatalog` may be reused or retired; check `CatalogEndpoints` before removing.
4. **Test catalogs without policy fields.** `ApiProposalTests` (lines ~34-35) and `SchemaValidatorTests` construct `CatalogEntry("...", JsonDocument.Parse("{}").RootElement, schema)`. Adding required constructor params breaks them. Make new typed members optional/defaulted on the record or update the call sites; with "entries must declare an entitlement" default, a `{}` entry fails closed to `entitlement_missing`, which will change those tests' outcomes (they need fully populated entries).
5. **Existing integration tests now traverse policy.** All current POST tests (liam-alpha/beta, purpose `servicing`) will hit purpose, entitlement and rate checks. Fixtures must give `analyst.alpha@example.test` and `analyst.beta@example.test` the entitlement and `liam` must list it, otherwise dozens of green tests turn into refusals. Also `crm.case_note@1` limit is 10/3600 per (tenant, agent, action): a test that posts more than 10 under one DB will start refusing. Use a test catalog with a high limit in `OliveApiFactory` (via `configureServices`) or per-test databases.
6. **`OutboundDestination` enum has only Popeye/Hometown.** `[OutboundPort(...)]` for the directory port requires adding a value (e.g. `Directory`). `ArchitectureTests` IDN_08 tests will keep passing: the port must carry neither `SessionKey` nor `ServiceTokenGrant`. The "every application port implemented in an integration assembly is marked outbound" test already scans `Olive.Integrations.Entra`. `RecordingPorts`/`OutboundCall` also assume destinations; add a `RecordingDirectoryResolver` and extend `ContainmentTests` if it enumerates calls.
7. **`expires_in_seconds` parsing.** The I-JSON gate admits numbers like `1.0`/`1e0` as valid JSON numbers; JSON Schema `type: integer` accepts `1.0`. `GetInt32()` would then throw (500). Use `TryGetInt64` and map any failure (and values above int range) to the same 400. Large values compare against `max_expiry_seconds` safely as `long`. Add schema `minimum: 1` (D-16); out-of-range-above-max is the endpoint/catalog check.
8. **Idempotency hash includes `expires_in_seconds`** (whole canonical body). Replaying the same run with a different `expires_in_seconds` is a 409 conflict by existing logic; add a test, no code needed.
9. **`not_in_catalog` stored row has no trace data.** Columns `trace_verification`, `trace_schema_version`, `proposal_hash` are nullable; `proposal_hash` is computed from the root before the trace step today (`Jcs.ProposalHash(root)`); either set it or leave null, but keep `body_sha256` so replay detection works (`DecideExistingAsync` requires `BodySha256`). The stored `parameters` for an unknown action are unvalidated JSON object: still stored (consistent with trace-refusals) but never exposed anywhere beyond `GET`.
10. **Resolver outcome mapping.** Distinguish (a) known user with groups, (b) unknown user / empty groups → `entitlement_missing` stored, (c) unavailable/timeout/throw/malformed/caller-rejected → 503 retryable, nothing stored. Copy `TraceVerifier`'s linked-CTS timeout and exception-to-Unavailable pattern, reuse `TraceVerifierOptions.Timeout` or add a sibling option; if the 503 reuses `TraceVerificationUnavailable`'s mechanism, add a distinct `ProblemReasons.DirectoryUnavailable` and a `Title` case in `ProblemResults.Title` (otherwise it falls to "internal error" title).
11. **Event ordering/typing.** Today refusal emits `submitted` then `trace_refused`. Define: `submitted`, `policy_evaluated` (detail jsonb), then transition event (`policy_refused` for refusals, `transition` for the accept). `InsertEvent` SQL has no `detail` column; add `@detail::jsonb`. Table-level INSERT grant on `proposal_event` already covers `detail`.
12. **Neither flaky-test baseline nor pool limits.** `StartupGuardTests` and `CatalogEndpointTests.API_10_*` are known flakes (ObjectDisposedException) in full-suite runs; do not chase. For the concurrency test, N parallel requests each hold a connection while waiting on the lock; keep N below the Npgsql default pool (100) or set `MaxPoolSize` via `TestDatabase.WithMaxPoolSize`.
13. **Catalog endpoint exposure.** `EntryJson` is served by `/v1/catalog`; new `required_entitlement` and `max_expiry_seconds` become visible to agents. That is additive and acceptable, but confirm `CatalogEndpointTests` do not assert exact JSON equality.

## Code Examples

### Advisory-lock rate count (inside the tenant transaction)
```csharp
// Source: PostgreSQL docs (pg_advisory_xact_lock, hashtextextended) and existing TenantUnitOfWork pattern
await conn.ExecuteAsync(new CommandDefinition(
    "select pg_advisory_xact_lock(hashtextextended(@t::text || '|' || @a || '|' || @x, 0))", new { t = tenantId, a = agentId, x = actionId }, tx, cancellationToken: ct));
var accepted = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
    "select count(*) from proposal where tenant_id=@t and agent_id=@a and action_type=@x and state <> 'refused_policy' " +
    "and created_at > now() - make_interval(secs => @w)", new { t = tenantId, a = agentId, x = actionId, w = windowSeconds }, tx, cancellationToken: ct));
if (accepted >= limit) { /* RefuseWithReason 'rate_limited' + events */ } else { /* UpdateState proposed->awaiting_approval + events */ }
```

### expires_at computation
```csharp
var requested = expiresIn ?? 28800;                        // 8h default
if (expiresIn is { } e && (e <= 0 || e > entry.MaxExpirySeconds)) return 400;   // Q3, nothing stored
var ttl = Math.Min(requested, entry.MaxExpirySeconds);
var expiresAt = Min(now + TimeSpan.FromSeconds(ttl), key.ExpiresAt);             // silent key cap (D-15)
```

### Migration 0007 (sketch; 0001-0006 checksum-pinned, do not edit)
```sql
alter table proposal add column expires_at timestamptz null;
grant insert (expires_at) on proposal to olive_app;
create index proposal_rate_idx on proposal (tenant_id, agent_id, action_type, created_at);
-- optional: check (expires_at is null or expires_at > created_at)
```
Note `action_type` is the existing column name (D-08 says action_id; same value). Update `PrivilegeMatrixTests` insert-column list (line ~134) and `MigrationRunnerTests` version/checksum expectations. RLS policies already cover the table; no new table, so no new RLS.

### Catalog/allowlist additions
```yaml
# catalog/crm.case_note@1.yaml (add)
required_entitlement: ent:crm.case_note.write
max_expiry_seconds: 28800
# config/agents.yaml
agents:
  - agent_id: liam
    entitlements: [ent:crm.case_note.write]
```
`action-type.schema.json` has `additionalProperties:false`; add both properties there and to `EntryDoc` (loader uses `UnmappedMemberHandling.Disallow`, so a missing `EntryDoc` property would fail startup). Make `max_expiry_seconds` `required` in schema (D-13) and `required_entitlement` required by schema as well if the fail-closed default is taken. Update `contracts/catalog/v1/action-type.valid.json`/`.invalid.json`.

## Recommendations on open planning points

- **Action without `required_entitlement`:** require it in the schema and loader (startup refusal). This removes the null-grants-fail-open path by construction. The evaluator should still treat a null as `entitlement_missing` defensively.
- **Fixture location:** `contracts/directory/{index.json, <user>.json, README.md, schema}` embedded into `Olive.Integrations.Entra` the same way `Olive.Integrations.Hometown` embeds `contracts/trace-resolution/*.json`. Fixture shape: `{ "tenant_id": ..., "on_behalf_of": ..., "groups": [...] }`. Include fixtures for unknown user and empty groups. Key on `(tenant_id, on_behalf_of)` verbatim.
- **Alarm code:** add `AlarmCodes.DirectoryResolver401/403` or a single `directory_caller_rejected`, plus `directory_response_malformed`, following `TraceResolver401` naming.
- **Which tenant to pass:** `key.TenantId` from the introspected key only (IDN-04).
- **Expiry cap for refused rows:** store `expires_at` only on accepted proposals; refusals carry null.
- **GET response:** add `expires_at` (nullable, omit when null) to `ProposalResponse`/`ProposalRecord`/`Row`/`ProposalSql.Get` and the `contracts/proposal/v1/proposal.schema.json` + examples; also update `proposal-submit-response` examples for new reasons (the badreason invalid example should be re-checked against any reason enum in that schema).

## State of the Art
Not applicable (no external ecosystem change). Postgres advisory transaction locks plus READ COMMITTED re-counting is the standard pattern for serialized per-key admission.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `hashtextextended(text,bigint)` exists and is stable enough for lock keys on PG 17 (undocumented-ish internal function, used widely) | Pattern 2 | If unavailable, use `hashtext` or two-int `pg_advisory_xact_lock(int,int)`; verify with a Testcontainers test |
| A2 | Npgsql default isolation for `BeginTransactionAsync` is READ COMMITTED | Pattern 2 | If SERIALIZABLE, add retry on 40001; verify in `TenantUnitOfWork` test (code shows no isolation argument) |
| A3 | `CatalogEndpointTests` do not assert exact entry JSON | Pitfall 13 | Tests need updating |
| A4 | Upstream spec section numbers (POPEYE_v0.4.2 §2.5/§5, vocab §3.2) as cited by CONTEXT; line 55 of POPEYE_v0.4.2.md and vocab line 76 confirmed present in ~/fiskroad/specs | D-01 | A newer POPEYE_v0.4.3.md exists in the same directory; confirm it does not move entitlement resolution before closing Q4 |

## Open Questions

1. **POPEYE_v0.4.3 vs v0.4.2.** The specs folder contains a v0.4.3. Not read. Recommendation: planner includes a quick check task that §2.5 still says "the calling service resolves groups" before recording the Q4 closure.
2. **Reason enum in submit-response schema.** The `proposal-submit-response.schema.json` may list allowed `refusal_reason` values (a `badreason` invalid example exists). Needs update for the four new codes. Not inspected in detail.
3. **Whether `trace_refused` events for existing rows should stay as is.** Recommended yes; only new reasons use `policy_refused`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/tests | yes | 10.0.400 | — |
| Docker | Testcontainers Postgres | yes | 29.7.2 | integration tests cannot run without it |

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0 (postgres:17-alpine), Mvc.Testing |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration`, `Category=Architecture`) |
| Quick run command | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| Full suite command | `dotnet test tests/Olive.Tests` (needs Docker, ~25 s, ~750 tests baseline) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| POL-01 | Wrong purpose → 201 stored `purpose_not_permitted`, zero queue-eligibility; unknown action → stored `not_in_catalog`, no trace/resolver call | unit + integration | `--filter "FullyQualifiedName~POL_01"` | Wave 0 (new `PolicyEvaluatorTests.cs`, `PolicyEndpointTests.cs`) |
| POL-02 | Intersection matrix (user only / agent only / both / neither / null / empty); unknown user refused; resolver fault → 503 nothing stored; credential rejection raises alarm | unit + integration | `--filter "FullyQualifiedName~POL_02"` | Wave 0 (+ `FaultingDirectoryResolver` in `FaultingDoubles.cs`) |
| POL-03 | Refusals do not consume limit; limit+1 → `rate_limited`; N parallel above limit accepts exactly `limit`; window slides; per-tenant and per-agent scope; lock not taken for refused | integration | `--filter "FullyQualifiedName~POL_03"` | Wave 0 |
| POL-04 | `policy_evaluated` event with outcome/reason/checks for allowed, each refusal, `not_in_catalog`; no parameter values or groups in detail (sentinel scan) | integration | `--filter "FullyQualifiedName~POL_04"` | Wave 0 (reuse `SentinelScan`) |
| POL-05 | Replay of policy refusal under same key → 200 same reason, resolver/evaluator call count unchanged, event count unchanged; different body → 409 | integration | `--filter "FullyQualifiedName~POL_05"` | Wave 0 |
| POL-06 | Full 8x8 transition matrix; CAS false on stale `from`; each success writes exactly one event; refusal-only-via-refusal-path | unit + integration | `--filter "FullyQualifiedName~POL_06"` | Wave 0 |
| POL-07 | Default 8h; shorten; ≤0 or >max → 400 nothing stored; key-expiry cap silent; `expires_at` absolute (unchanged after clock advance); GET exposes it; idempotency conflict on changed value | integration (ManualTimeProvider) | `--filter "FullyQualifiedName~POL_07"` | Wave 0 |
| API-08 | Multi-fault request peeled one fault at a time; recorder asserts call order and absence of later calls | integration | `--filter "FullyQualifiedName~API_08"` | Wave 0 (extend `Events` in `OliveApiFactory`, `RecordingPorts.cs`) |
| Schema/migration | Catalog schema new fields, proposal schema `expires_in_seconds` valid/invalid; migration 0007 privilege matrix and checksum | contract + integration | `--filter "FullyQualifiedName~ContractSchema\|PrivilegeMatrix\|MigrationRunner"` | extend existing |
| Guards | Production + `FixtureDirectoryGroupResolver` refuses start (incl. behind decorator); architecture tests accept new destination | integration/arch | `--filter "FullyQualifiedName~StartupGuard\|Architecture"` | extend existing |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **Per wave merge:** `dotnet test tests/Olive.Tests`
- **Phase gate:** full suite green plus `dotnet restore --locked-mode` before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `PolicyEvaluatorTests.cs`, `PolicyEndpointTests.cs`, `RateLimitTests.cs`, `ExpiryTests.cs`, `LifecycleTransitionTests.cs`, `SubmissionOrderTests.cs`
- [ ] `FaultingDirectoryResolver` + `RecordingDirectoryResolver` and factory wiring (default fixtures granting liam/alpha/beta the entitlement so legacy tests stay meaningful)
- [ ] Test catalog with high rate limit for legacy API tests, and a small-window variant
- [ ] Fix/rewrite `API_05_unknown_action_type_gives_non_stored_400_not_in_catalog` and the `ApiProposalTests`/`SchemaValidatorTests` `CatalogEntry` constructions
- [ ] Contract examples for changed proposal request/response, catalog entry, directory fixtures
- [ ] No framework install needed

## Security Domain

### Applicable ASVS Categories (level 1)
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no (unchanged; introspection handler) | existing |
| V4 Access Control | yes | Intersection of agent and user entitlements, fail closed; no union fallback; null/empty = deny |
| V5 Input Validation | yes | Envelope schema `expires_in_seconds` integer ≥1, `TryGetInt64`, I-JSON gate, strict YAML |
| V7 Error/Logging | yes | Static reason codes, value-free logs, alarm codes only; no group lists or parameters in events/logs |
| V8 Data Protection | yes | `policy_evaluated` detail static; RLS + application tenant predicate on every new statement |
| V6 Cryptography | no | none added |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Rate limit bypass by concurrent submissions | Elevation/DoS | Advisory lock + in-tx count; concurrency test |
| Refusal flooding to exhaust limit of another path | DoS | Refusals excluded from count |
| Entitlement fail-open on null/empty data | Elevation | Deny by default; schema requires `required_entitlement`; evaluator treats null as deny |
| Cross-tenant directory/rate leakage | Info disclosure | Resolver keyed on `key.TenantId`; count under tenant RLS and predicate |
| Expiry tampering (client-lengthened) | Tampering | Server-computed absolute `expires_at`; reject > max with 400; cap by key expiry |
| Sensitive data in events/logs | Info disclosure | Static check names/reasons; sentinel scan test |
| Illegal state jump (re-approval, resurrecting refused) | Tampering | Domain transition table + CAS + `state` not in INSERT grant + refusal-agrees check |
| Directory outage causing silent allow | Elevation | 503 retryable, nothing stored |
| Production wiring of fixture resolver | Spoofing | `INonProductionOnly` + FND-05 guard test |

## Project Constraints (from CLAUDE.md)
- CLAUDE.md is stale (describes the scaffold `Domain.cs`); the code now spans 11 projects under `src/`. Constraints still in force: no write credential and no `HttpClient`/socket assemblies (REQ-ACT-04, enforced by architecture tests); queue payloads are references only; tenant isolation via application predicate AND forced RLS; never edit `contracts/vendored/vocabulary.yaml`; catalog is file-only, no runtime registration; changes within `v1` contracts additive and optional only; fixtures barred in production; policy detail text static and never includes parameter values; tests cite requirement IDs in names (`POL_0x_...`, `API_08_...`); time injected, not read inside services; fail closed on resolver/auth failure; tokens never logged or stored.
- GSD workflow: file changes go through a GSD command.

## Sources

### Primary (HIGH)
- Repo code read in this session: `ProposalEndpoints.cs`, `PostgresProposalStore.cs`, `ProposalSql.cs`, `IProposalStore.cs`, `TenantUnitOfWork.cs`, `CatalogLoader.cs`, `CatalogModels.cs`, `SubmissionValidator.cs`, `AgentAllowlist.cs`, `TraceVerifier.cs`, `StartupGuards.cs`, `ProblemResults.cs`, `OliveApiFactory.cs`, `ArchitectureTests.cs`, migrations 0001-0006, `catalog/crm.case_note@1.yaml`, `config/agents.yaml`, introspection fixtures
- `~/fiskroad/specs/POPEYE_v0.4.2.md` lines 30, 55, 91 and `PROGRAM_SHARED_VOCABULARY_v0.4.md` line 76 (entitlement source and intersection)
- `specs/DECISION_olive-poc2-phase1_v0.5.md` §9 (expiry), §15.2 (order), §16.3 (not-found not stored)
- `.planning/phases/03-trace-verification/03-VALIDATION.md` (test conventions, known flakes)

### Secondary (MEDIUM)
- PostgreSQL advisory lock semantics (training knowledge; verify via Testcontainers test, see A1/A2)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH, no new dependencies
- Architecture: HIGH, follows existing Phase 3 patterns verified in code
- Pitfalls: HIGH for code-derived items; MEDIUM for A1/A2

**Research date:** 2026-10-22
**Valid until:** 30 days (stable internal codebase)
