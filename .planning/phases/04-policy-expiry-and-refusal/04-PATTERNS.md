# Phase 4: Policy, Expiry and Refusal - Pattern Map

**Mapped:** 2026-10-22
**Files analyzed:** 27 (13 new, 14 modified), plus tests
**Analogs found:** 27 / 27 (one is partial: the advisory-lock rate store method has no in-repo analog for the lock itself)

CLAUDE.md is stale (it describes the scaffold `Domain.cs`). All paths below are the real multi-project layout under `src/`. Paths are relative to `C:\Users\OMack\olive`.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `src/Olive.Domain/PolicyRefusalReasons.cs` (new) | utility (constants) | transform | `src/Olive.Domain/TraceRefusalReasons.cs` | exact |
| `src/Olive.Domain/ProposalStates.cs` (mod: transition table) | model | transform | itself (`ProposalStates`) | exact |
| `src/Olive.Domain/PolicyEvaluator.cs` (new, pure) | service | transform | `TraceVerifier.Check` in `src/Olive.Application/TraceVerifier.cs` (lines 79-103) | role-match |
| `src/Olive.Application/IDirectoryGroupResolver.cs` (new) | port | request-response | `src/Olive.Application/ITraceResolver.cs` | exact |
| `src/Olive.Application/OutboundPortAttribute.cs` (mod: add `Directory`) | config | n/a | itself | exact |
| `src/Olive.Application/IOperationalAlarm.cs` (mod: `AlarmCodes`) | config | event-driven | itself | exact |
| `src/Olive.Application/PolicyVerifier.cs` (new, optional orchestrator for resolver timeout and outcome mapping) | service | request-response | `src/Olive.Application/TraceVerifier.cs` | exact |
| `src/Olive.Application/IProposalStore.cs` (mod: `NewProposal.ExpiresAt`, `ProposalRecord.ExpiresAt`, new method) | port | CRUD | itself | exact |
| `src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs` (new) | adapter (stub) | request-response | `src/Olive.Integrations.Hometown/FixtureTraceResolver.cs` | exact |
| `src/Olive.Integrations.Entra/Olive.Integrations.Entra.csproj` (mod: embed fixtures) | config | n/a | `src/Olive.Integrations.Hometown/Olive.Integrations.Hometown.csproj` | exact |
| `contracts/directory/*` (new: README, schema, index.json, user fixtures) | contract fixtures | n/a | `contracts/trace-resolution/*` (README, `index.json`, `*.json`, schema, valid/invalid) | exact |
| `src/Olive.Catalog/CatalogModels.cs` (mod: typed policy fields on `CatalogEntry`) | model | transform | itself | exact |
| `src/Olive.Catalog/CatalogLoader.cs` (mod: `EntryDoc` + validation) | service | file-I/O | itself (`EntryDoc`, `ParseEntry`) | exact |
| `contracts/catalog/v1/action-type.schema.json` (+ valid/invalid) | contract | n/a | itself | exact |
| `catalog/crm.case_note@1.yaml` (mod) | config | n/a | itself | exact |
| `src/Olive.Catalog/AgentAllowlist.cs` (mod: `entitlements`) + `config/agents.yaml` | service/config | file-I/O | itself | exact |
| `src/Olive.Application/IAgentAllowlist.cs` (mod: expose agent entitlements) | port | transform | itself | exact |
| `src/Olive.Catalog/SubmissionValidator.cs` (mod: unknown action and expires_in range) | service | transform | itself | exact |
| `contracts/proposal/v1/proposal-request.schema.json` (+ valid/invalid), `proposal.schema.json` (+ examples), `proposal-submit-response.*` | contract | n/a | themselves | exact |
| `sql/migrations/0007_policy_expiry_rate.sql` (new) | migration | n/a | `sql/migrations/0006_trace_verification.sql`, `0003_proposal_insert_columns.sql` | exact |
| `src/Olive.Persistence/ProposalSql.cs` (mod) | model (SQL consts) | CRUD | itself | exact |
| `src/Olive.Persistence/PostgresProposalStore.cs` (mod: `InsertEvaluatedAsync`, generalized transition, `expires_at`) | repository | CRUD, transactional | itself (`InsertCoreAsync`, `TransitionAsync`) | exact |
| `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (mod: policy step, not_in_catalog store, expiry) | controller | request-response | itself (`PostAsync`) | exact |
| `src/Olive.Contracts/ProblemReasons.cs`, `ProposalDtos.cs`, `src/Olive.Host/Problems/ProblemResults.cs` (mod) | utility | request-response | themselves | exact |
| `src/Olive.Host/Program.cs` (mod: DI) | config | n/a | itself (line 39 `ITraceResolver`) | exact |
| `src/Olive.Host/Startup/StartupGuards.cs` (verify only) | middleware | n/a | itself | exact |
| Tests: `PolicyEvaluatorTests`, `PolicyEndpointTests`, `RateLimitTests`, `ExpiryTests`, `LifecycleTransitionTests`, `SubmissionOrderTests` (new) | test | request-response | `tests/Olive.Tests/TraceRefusalEndpointTests.cs`, `TraceVerifierTests.cs`, `TraceFailClosedTests.cs` | exact |
| Test doubles: `FaultingDirectoryResolver` (`FaultingDoubles.cs`), `RecordingDirectoryResolver` (`RecordingPorts.cs`), factory wiring (`OliveApiFactory.cs`) | test infra | n/a | `FaultingTraceResolver`, `RecordingTraceResolver`, `OliveApiFactory` | exact |

## Pattern Assignments

### `src/Olive.Domain/PolicyRefusalReasons.cs` (utility, transform)

**Analog:** `src/Olive.Domain/TraceRefusalReasons.cs` (whole file, 9 lines)

```csharp
namespace Olive.Domain;

public static class TraceRefusalReasons
{
    public const string Unresolved = "trace_unresolved";
    public const string TenantMismatch = "trace_tenant_mismatch";
    // ...
}
```
Copy as `PolicyRefusalReasons` with `NotInCatalog = "not_in_catalog"`, `PurposeNotPermitted = "purpose_not_permitted"`, `EntitlementMissing = "entitlement_missing"`, `RateLimited = "rate_limited"`. These must match `^[a-z][a-z_]{0,63}$` (constraint `proposal_refusal_reason_format`, 0006 line 9). No constraint change is needed.

---

### `src/Olive.Domain/ProposalStates.cs` (model, transform) - transition table (POL-06)

**Analog:** itself (lines 4-17). Extend with a static table and `CanTransition`.

```csharp
public static class ProposalStates
{
    public const string Proposed = "proposed";
    public const string RefusedPolicy = "refused_policy";
    // ... AwaitingApproval, Approved, Rejected, Executed, ExecutionFailed, Expired
    public static readonly IReadOnlyList<string> All = [Proposed, RefusedPolicy, AwaitingApproval, Approved, Rejected, Executed, ExecutionFailed, Expired];
}
```
Add (per RESEARCH Pattern 3): `proposed -> {refused_policy, awaiting_approval}`, `awaiting_approval -> {approved, rejected, expired}`, `approved -> {executed, execution_failed, expired}`; all else illegal. Unit-test the full 8x8 matrix over `All`.

---

### `src/Olive.Domain/PolicyEvaluator.cs` (service, pure transform)

**Analog:** `TraceVerifier.Check` (`src/Olive.Application/TraceVerifier.cs` lines 79-103): a pure first-failure-wins `if / else if` chain that returns a typed verdict.

```csharp
string? reason = null;
if (f.RunId is null || !string.Equals(f.RunId, runId, StringComparison.Ordinal)) { reason = TraceRefusalReasons.Unresolved; }
else if (f.TenantId != key.TenantId) { reason = TraceRefusalReasons.TenantMismatch; }
// ...
return reason is null ? new TraceVerdict.Verified(...) : new TraceVerdict.Refused(reason, ...);
```
Apply: purpose check (`key.Purpose` in entry required_purpose) first, then entitlement (`userGroups.Contains(req) && agentEntitlements.Contains(req)`; null required entitlement or null/empty groups = `entitlement_missing`, never a union). Use `string.Equals(..., StringComparison.Ordinal)`. Output a verdict carrying static `checks: [{name, result}]` (names `catalog`, `purpose`, `entitlement`, `rate`). Never include group lists or parameter values. Where the evaluator lives: Domain cannot reference `Olive.Catalog`, so take primitive inputs (required purposes, required entitlement, strings) or place it in `Olive.Application`.

---

### `src/Olive.Application/IDirectoryGroupResolver.cs` (port, request-response)

**Analog:** `src/Olive.Application/ITraceResolver.cs` (lines 1-16)

```csharp
public abstract record TraceLookup
{
    public sealed record Found(TraceFacts Facts, string Source) : TraceLookup;
    public sealed record NotFound : TraceLookup;
    public sealed record Unavailable : TraceLookup;
    public sealed record CallerRejected(int Status) : TraceLookup;
    public sealed record Malformed : TraceLookup;
}

[OutboundPort(OutboundDestination.Hometown)]
public interface ITraceResolver { Task<TraceLookup> ResolveAsync(ServiceTokenGrant grant, string traceId, CancellationToken ct); }
```
Apply: `DirectoryLookup { Found(IReadOnlyList<string> Groups), UnknownUser, Unavailable, CallerRejected(int Status), Malformed }`; `[OutboundPort(OutboundDestination.Directory)] interface IDirectoryGroupResolver { Task<DirectoryLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct); }`. The port must carry no `SessionKey` and no `ServiceTokenGrant` (IDN_08 architecture tests). Add `Directory` to `OutboundDestination` in `OutboundPortAttribute.cs` line 3: `public enum OutboundDestination { Popeye, Hometown, Directory }`.

---

### `src/Olive.Application/PolicyVerifier.cs` (or equivalent resolver-call wrapper) (service, request-response)

**Analog:** `src/Olive.Application/TraceVerifier.cs` lines 38-76 (linked-CTS timeout, exception-to-Unavailable mapping, alarm on caller rejection).

```csharp
using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
{
    cts.CancelAfter(options.Timeout);
    try { lookup = await resolver.ResolveAsync(grant, traceRef, cts.Token); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new TraceVerdict.Unavailable("trace_resolver_timeout"); }
    catch (Exception e) when (!ct.IsCancellationRequested) { return new TraceVerdict.Unavailable("trace_resolver_exception", e.GetType().Name); }
}
switch (lookup)
{
    case TraceLookup.CallerRejected rejected:
        alarm.Raise(rejected.Status == 403 ? AlarmCodes.TraceResolver403 : AlarmCodes.TraceResolver401, Component);
        return new TraceVerdict.Unavailable(...);
    case TraceLookup.Malformed:
        alarm.Raise(AlarmCodes.TraceResponseMalformed, Component);
        return new TraceVerdict.Unavailable("trace_response_malformed");
```
Apply: `Component = "directory"`; the exception message is never used, type name only. Reuse `TraceVerifierOptions.Timeout` or add a sibling options record (same file `TraceVerdict.cs` lines 13-16 shows the shape). Unknown user maps to a refusal outcome (not 503). Add alarm codes to `AlarmCodes` (`IOperationalAlarm.cs` lines 6-17), e.g. `DirectoryResolver401`, `DirectoryResolver403`, `DirectoryResponseMalformed`.

---

### `src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs` (adapter stub, request-response)

**Analog:** `src/Olive.Integrations.Hometown/FixtureTraceResolver.cs` (lines 11-50)

```csharp
public sealed class FixtureTraceResolver : ITraceResolver, INonProductionOnly
{
    private readonly ConcurrentDictionary<string, TraceFacts> _facts = new(StringComparer.Ordinal);
    public static FixtureTraceResolver CreateDefault()
    {
        var asm = typeof(FixtureTraceResolver).Assembly;
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "trace-resolution/index.json")) ?? throw ...;
        foreach (var (traceId, file) in index) { ... resolver.Register(traceId, found.Facts); }
        return resolver;
    }
    public void Register(string traceId, TraceFacts facts) => _facts[traceId] = facts;
    private static string ReadResource(Assembly asm, string name) { using var s = asm.GetManifestResourceStream(name) ?? throw ...; ... }
}
```
Apply: key on `(tenantId, onBehalfOf)` verbatim and ordinal (D-06). Include `INonProductionOnly` (FND-05 guard auto-picks this up via `StartupGuards.cs` line 58). Embed `contracts/directory/*.json` in the csproj exactly like `Olive.Integrations.Hometown.csproj` line 7:

```xml
<ItemGroup>
  <EmbeddedResource Include="..\..\contracts\trace-resolution\*.json" LogicalName="trace-resolution/%(Filename)%(Extension)" />
</ItemGroup>
```
Entra csproj currently has only the two ProjectReferences (no embeds), so add the ItemGroup. No new packages and no HttpClient (REQ_ACT_04, arch tests). The fixture must grant `analyst.alpha@example.test` and `analyst.beta@example.test` the entitlement, so existing POST tests keep passing (RESEARCH pitfall 5).

---

### `src/Olive.Catalog/CatalogModels.cs` + `CatalogLoader.cs` (model/service, file-I/O)

**Analog:** itself. `CatalogEntry` is a positional record `(string ActionId, JsonElement EntryJson, SchemaValidator ParametersSchema)`; tests construct it with 3 args (`ApiProposalTests` ~34-35, `SchemaValidatorTests`), so add new members as optional/defaulted params at the end.

`EntryDoc` (CatalogLoader.cs lines 28-37) uses strict deserialization (`UnmappedMemberHandling.Disallow`, line 15), so every new YAML key must be added here or startup fails:

```csharp
[JsonPropertyName("required_approval")] public string? RequiredApproval { get; set; }
[JsonPropertyName("max_rate")] public RateDoc? MaxRate { get; set; }
// add:
[JsonPropertyName("required_entitlement")] public string? RequiredEntitlement { get; set; }
[JsonPropertyName("max_expiry_seconds")] public int? MaxExpirySeconds { get; set; }
```
`RateDoc` (lines 39-43) already holds `limit` and `window_seconds`, but `ParseEntry` (lines 106-118) discards them. Surface `RequiredPurpose`, `MaxRate`, `RequiredEntitlement`, `MaxExpirySeconds` as typed members on `CatalogEntry`. Validation pattern to copy (lines 112-115): static message, throw `InvalidOperationException(Invalid)`.

```csharp
if (entry.RequiredPurpose is not { Count: > 0 } req || req.Any(p => !purposes.Contains(p))) { throw new InvalidOperationException(Invalid); }
```
Add: `max_expiry_seconds` required and >0; `required_entitlement` required and non-blank (fail-closed default).

**Schema** `contracts/catalog/v1/action-type.schema.json` (single-line JSON, `additionalProperties:false`, `required` list on line 3): add `required_entitlement` (string, minLength 1) and `max_expiry_seconds` (integer, minimum 1) to `properties` and to `required`. Update `action-type.valid.json` / `.invalid.json`.

**Catalog** `catalog/crm.case_note@1.yaml`: add `required_entitlement: ent:crm.case_note.write` and `max_expiry_seconds: 28800`.

---

### `src/Olive.Catalog/AgentAllowlist.cs` + `config/agents.yaml` (service, file-I/O)

**Analog:** itself (lines 16-18, 42-51). Strict YamlDotNet deserialization (no `IgnoreUnmatchedProperties`), so add the property to `AgentEntry` or `entitlements:` fails the parse.

```csharp
private sealed class AgentEntry { public string? AgentId { get; set; } }
// add: public List<string>? Entitlements { get; set; }
```
Store `Dictionary<string, IReadOnlySet<string>>` in place of `HashSet<string> _ids`; keep `IsAllowed`. Add an `Entitlements(string agentId)` accessor and expose it on `IAgentAllowlist` (`src/Olive.Application/IAgentAllowlist.cs`). Validate with the same `throw new InvalidOperationException(Invalid)` shape. Existing tests in `AgentAllowlistTests.cs` call `Parse` with YAML lacking `entitlements`; decide whether it is optional (treat missing as empty set, which denies). `config/agents.yaml`:

```yaml
agents:
  - agent_id: liam
    entitlements: [ent:crm.case_note.write]
```

---

### `src/Olive.Catalog/SubmissionValidator.cs` (service, transform)

**Analog:** itself (lines 43-69). `NotInCatalog` stays a validator outcome (line 54) but the endpoint now stores it instead of returning 400; update the stale comment on line 66 ("Phase 4 replaces this seam"). Outcome hierarchy to extend:

```csharp
public sealed record Ok(CatalogEntry Entry) : SubmissionOutcome;
public sealed record Violation(SchemaViolation Detail) : SubmissionOutcome;
public sealed record NotInCatalog : SubmissionOutcome;
```
Add the `expires_in_seconds` range check here or in the endpoint, after the entry is found. Use `TryGetInt64` (pitfall 7: `1.0`/`1e0` pass schema `integer`, and `GetInt32` would throw a 500). Map any failure, <=0, or above `MaxExpirySeconds` to `Violation` or a dedicated 400 with nothing stored. Add `expires_in_seconds` (`type: integer, minimum: 1`) to `contracts/proposal/v1/proposal-request.schema.json` `properties` (line 8-16; `additionalProperties:false`, line 6), plus valid and invalid examples.

---

### `sql/migrations/0007_policy_expiry_rate.sql` (migration)

**Analogs:** `0006_trace_verification.sql` (additive columns plus grants, header comment explaining checksum pinning) and `0003_proposal_insert_columns.sql` (column-level INSERT grants; `revoke insert` then explicit column grant, so every new insertable column needs its own grant).

```sql
-- 0006 style
alter table proposal add column trace_verification text, ...;
grant insert (trace_verification, trace_schema_version) on proposal to olive_app;
grant update (refusal_reason) on proposal to olive_app;
```
Apply (from RESEARCH): `alter table proposal add column expires_at timestamptz null;` (nullable: pre-existing rows), `grant insert (expires_at) on proposal to olive_app;`, `create index proposal_rate_idx on proposal (tenant_id, agent_id, action_type, created_at);`. Do not edit 0001-0006 (checksum-pinned). Column is `action_type`, not `action_id`. `created_at` is DB-set and not insertable, so the window uses `now()` in SQL. Update `PrivilegeMatrixTests` (insert-column list) and `MigrationRunnerTests` (version/checksum expectations).

---

### `src/Olive.Persistence/ProposalSql.cs` (SQL constants)

**Analog:** itself (lines 6-26). Every statement keeps the `tenant_id = @tenant_id` predicate. Changes:

```csharp
public const string InsertEvent =
    "insert into proposal_event (tenant_id, event_id, proposal_id, event_type, from_state, to_state, actor) " +
    "values (@tenant_id, @event_id, @proposal_id, @event_type, @from_state, @to_state, @actor)";
public const string UpdateState = "update proposal set state = @to_state where tenant_id = @tenant_id and proposal_id = @proposal_id and state = @from_state";
public const string RefuseWithReason = "update proposal set state = 'refused_policy', refusal_reason = @reason where tenant_id = @tenant_id and proposal_id = @proposal_id and state = 'proposed'";
```
- `Insert`: add `expires_at` column and `@expires_at`.
- `InsertEvent`: add `detail` column with `@detail::jsonb` (the `proposal_event.detail jsonb` column already exists; table-level INSERT grant covers it). Existing callers pass `detail = (string?)null`.
- `Get`: add `expires_at`.
- New `LockRate` (`select pg_advisory_xact_lock(hashtextextended(@t::text || '|' || @a || '|' || @x, 0))`), and `CountAccepted` (`state <> 'refused_policy' and created_at > now() - make_interval(secs => @w)`), both exactly as in RESEARCH "Advisory-lock rate count". Count before inserting the new row.

---

### `src/Olive.Persistence/PostgresProposalStore.cs` (repository, CRUD transactional)

**Analog:** itself. `InsertCoreAsync` (lines 41-105) is the template: a single `uow.ExecuteAsync` doing insert, `submitted` event, then CAS, then a transition event, all in one tenant tx.

```csharp
uow.ExecuteAsync<Guid?>(tenantId, async (conn, tx) =>
{
    var id = Guid.NewGuid();
    var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(ProposalSql.Insert, new { tenant_id = tenantId, proposal_id = id, ... }, tx, cancellationToken: ct));
    if (inserted is null) { return null; }   // duplicate run, caller re-decides replay/conflict
    await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new { ..., event_type = "submitted", from_state = (string?)null, to_state = "proposed", actor = p.AgentId }, tx, cancellationToken: ct));
    if (refusalReason is not null)
    {
        var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.RefuseWithReason, new { tenant_id = tenantId, proposal_id = id, reason = refusalReason }, tx, cancellationToken: ct));
        if (n != 1) { throw new InvalidOperationException("Refusal compare-and-set did not affect exactly one row."); }
        await conn.ExecuteAsync(... event_type = "trace_refused", from_state = "proposed", to_state = "refused_policy" ...);
    }
    return id;
}, ct);
```
Apply:
1. Policy refusals (`purpose_not_permitted`, `entitlement_missing`, `not_in_catalog`) reuse `InsertRefusedAsync` with a `policy_evaluated` event (detail jsonb) emitted between `submitted` and the transition event. Use event type `policy_refused` for the refusal transition (existing rows keep `trace_refused`; make the event type a parameter or derive it from the reason prefix).
2. New `InsertEvaluatedAsync` (accept-or-rate-refuse): inside one `uow.ExecuteAsync`, first `LockRate`, then `CountAccepted`, then `Insert` (null means duplicate, return null and the lock releases at commit), then if `count >= limit` do `RefuseWithReason(rate_limited)` else CAS `proposed -> awaiting_approval` (`UpdateState`) with row-count check (`n != 1` throws, same message pattern). Events: `submitted`, `policy_evaluated`, transition. READ COMMITTED (the Npgsql default) is what makes lock-then-count correct (assumption A2; verify).
3. `TransitionAsync` (lines 135-166): the current guard (lines 140-143) throws `ArgumentException` for any `refused_policy` edge. Keep that rule, add a `ProposalStates.CanTransition(from, to)` check before DB work, keep the `n == 0 -> false` CAS, and keep one `transition` event per success.
4. `Row`/`GetAsync` (lines 10-31, 107-115): add `public DateTime? ExpiresAt`, mapped via `Utc`. `DefaultTypeMap.MatchNamesWithUnderscores = true` (line 8) maps `expires_at` to `ExpiresAt`.

Detail payload for `policy_evaluated`: serialize `{outcome, reason, checks:[{name,result}]}` only; never parameters or groups.

---

### `src/Olive.Application/IProposalStore.cs` (port)

**Analog:** itself. `NewProposal` (line 24) and `ProposalRecord` (line 28) are single-line positional records with optional trailing params; append `DateTimeOffset? ExpiresAt = null` (and a `Detail`/evaluation payload carrier or separate method args) so existing call sites compile. Add XML-free `//` comments in the established style (see lines 8-10).

---

### `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (controller, request-response)

**Analog:** itself (`PostAsync`, lines 26-184). The Phase 4 edits slot into the existing numbered flow:

Existing schema/catalog step to change (lines 95-102):
```csharp
switch (validator.Validate(root))
{
    case SubmissionOutcome.Violation v:
        return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.SchemaViolation, v.Detail.Pointer, v.Detail.Keyword);
    case SubmissionOutcome.NotInCatalog:
        return ProblemResults.Result(StatusCodes.Status400BadRequest, ProblemReasons.NotInCatalog);   // becomes: store refused_policy / not_in_catalog (201), no trace, no further policy
}
```
Stored-refusal and 201 shape to reuse (lines 156-182): `store.InsertRefusedAsync(key.TenantId, proposal, refusalReason, ct)`, `id is null` falls to `DecideExistingAsync()`, and the 201 `ProposalCreatedResponse(id.Value, ProposalStates.RefusedPolicy, refusalReason)`. Keep the `PostgresException` catch (lines 163-172).

Fail-closed signalling to mirror for the directory resolver (lines 138-150 and 188):
```csharp
case TraceVerdict.NotFound: return ProblemResults.Retryable(StatusCodes.Status422UnprocessableEntity, ProblemReasons.TraceNotFound);
default:
    if (verdict is TraceVerdict.Unavailable unavailable) { logger.LogWarning("Trace verification unavailable tenant_id={TenantId} agent_id={AgentId} cause={Cause} exception_type={ExceptionType}", ...); }
    return TraceUnavailable();
private static IResult TraceUnavailable() => ProblemResults.Retryable(StatusCodes.Status503ServiceUnavailable, ProblemReasons.TraceVerificationUnavailable);
```
Add a `DirectoryUnavailable()` sibling with `ProblemReasons.DirectoryUnavailable`, logging cause and exception type only (never `on_behalf_of` or groups).

Order after trace verification (Verified or trace-refused branch): trace refusal still wins and skips policy; otherwise purpose (pure), then entitlement (resolver; 503 stores nothing), then the rate path via `InsertEvaluatedAsync`. Compute `expires_at` with the injected `TimeProvider` (inject as a handler parameter, it is registered in `Program.cs` line 21): `Min(now + TimeSpan.FromSeconds(Math.Min(expiresIn ?? 28800, max)), key.ExpiresAt)`, stored only on accepted proposals. Replay path (`DecideExistingAsync`, lines 65-87) runs before any evaluation, so POL-05 holds structurally; do not add evaluation before it.

`GetAsync` (lines 223-241): pass `r.ExpiresAt` through to `ProposalResponse` (add the field to `src/Olive.Contracts/ProposalDtos.cs`, omit when null), and update `contracts/proposal/v1/proposal.schema.json` + valid/invalid examples.

Comment on line 25 ("Order (Decision section 15.2) ...") should be updated to list the full order.

---

### `src/Olive.Contracts/ProblemReasons.cs` + `src/Olive.Host/Problems/ProblemResults.cs`

**Analog:** the files themselves. Add `public const string DirectoryUnavailable = "directory_unavailable";` after `TraceVerificationUnavailable` (ProblemReasons line 18) and a `Title` case in `ProblemResults.Title` (lines 21-37), otherwise it falls to "An internal error occurred.":

```csharp
ProblemReasons.TraceVerificationUnavailable => "Trace verification is unavailable.",
```
`ProblemResults.Retryable` (lines 59-65) already adds `retryable: true` plus `Retry-After` for 503. `ProblemReasons.NotInCatalog` (line 16) is reused as a refusal reason string only; check `CatalogEndpoints` before retiring it.

---

### `src/Olive.Host/Program.cs` (DI)

**Analog:** itself, line 39:
```csharp
builder.Services.AddSingleton<ITraceResolver>(_ => new CachingTraceResolver(FixtureTraceResolver.CreateDefault()));
```
Add `builder.Services.AddSingleton<IDirectoryGroupResolver>(_ => FixtureDirectoryGroupResolver.CreateDefault());` (no caching, D-07) plus the policy verifier registration. Add `using Olive.Integrations.Entra;` and the project reference from `Olive.Host.csproj` if missing. `StartupGuards.cs` (lines 44-104) scans registered types and decorators for `INonProductionOnly`, so the fixture is automatically barred in production; extend `StartupGuardTests` rather than the guard.

---

### Tests

**Endpoint refusal tests** (`PolicyEndpointTests`, `RateLimitTests`, `ExpiryTests`): analog `tests/Olive.Tests/TraceRefusalEndpointTests.cs` lines 14-43 (copy the `AssertRefusal` helper shape).

```csharp
[Trait("Category", "Integration")]
public sealed class TraceRefusalEndpointTests(PostgresFixture fixture)
{
    private async Task AssertRefusal(string caseName, string reason, string schemaVersion = "ate/0.4")
    {
        await using var db = await fixture.CreateDatabaseAsync();
        await using var factory = new OliveApiFactory(db, traceMode: TraceMode.Explicit);
        using var client = factory.CreateKeyClient(ApiProposalTests.AlphaKey);
        var post = await Post(client, body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        ...
        Assert.Equal("submitted,trace_refused", await Scalar<string>(db, "select string_agg(event_type, ',' order by event_type) from proposal_event where proposal_id = @p", id));
```
Update the expected event strings for policy refusals (`policy_evaluated,policy_refused,submitted`). Test names carry requirement IDs (`POL_01_...`, `API_08_...`).

**Faulting double**: analog `FaultingTraceResolver` and `ResolverFault` enum (`tests/Olive.Tests/FaultingDoubles.cs` lines 8-30): same modes (`Unavailable`, `Hang`, `Throw` with a secret-detail message to prove no leakage, `CallerRejected401/403`, `Malformed`) wrapping a healthy inner resolver. Add `FaultingDirectoryResolver`.

**Recording double**: analog `RecordingTraceResolver` (`tests/Olive.Tests/RecordingPorts.cs` lines 10-23):
```csharp
public sealed class RecordingTraceResolver(ITraceResolver inner, List<string> events, List<OutboundCall> calls) : ITraceResolver, IComponentDecorator
{
    private static readonly OutboundDestination Destination = typeof(ITraceResolver).GetCustomAttribute<OutboundPortAttribute>()!.Destination;
    public object Inner => inner;
    public Task<TraceLookup> ResolveAsync(...) { lock (events) { events.Add("resolve"); } lock (calls) { calls.Add(new OutboundCall(...)); } return inner.ResolveAsync(...); }
}
```
Copy as `RecordingDirectoryResolver` (event marker `"directory"`; `IComponentDecorator` is needed so the production guard can see through it). The API-08 order test peels faults using `OliveApiFactory.Events` (a `List<string>`, line 26) and asserts the call order and the absence of later markers.

**Factory wiring**: `OliveApiFactory.ConfigureWebHost` (lines 61-87) registers the recording doubles in `ConfigureTestServices`. Add the directory resolver the same way, and a high-limit test catalog via the `configureServices` hook (line 86) so the 10/3600 limit does not turn legacy tests into refusals (pitfall 5). `RecordingProposalStore` (wrapping `PostgresProposalStore`, line 85) needs the new store method forwarded.

**Rewrite required**: `ApiIntegrityTests.API_05_unknown_action_type_gives_non_stored_400_not_in_catalog`, `SubmissionValidatorTests` NotInCatalog cases, and the `CatalogEntry` constructions in `ApiProposalTests` and `SchemaValidatorTests`.

**Concurrency test (POL-03)**: no in-repo analog for parallel POSTs against a lock. Use a per-test database from `fixture.CreateDatabaseAsync()` and `Task.WhenAll` over N requests with distinct `run_id`s; keep N below the Npgsql pool (100) or use `TestDatabase.WithMaxPoolSize`.

## Shared Patterns

### Fail-closed split (stored refusal vs 503 retryable)
**Source:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs` lines 138-150, 188; `src/Olive.Application/TraceVerifier.cs` lines 38-76.
**Apply to:** the directory-resolver path. Expected data outcomes (unknown user, empty groups, missing entitlement) = stored 201 refusal. Operational faults (timeout, throw, 401/403, malformed) = 503, `retryable: true`, `Retry-After`, nothing stored, cause only in Warning logs, alarm for Olive's own credential rejection.

### Typed outcome hierarchies, no exceptions for expected outcomes
**Source:** `TraceVerdict` (`src/Olive.Application/TraceVerdict.cs` lines 3-11), `TraceLookup` (`ITraceResolver.cs`), `SubmissionOutcome` (`SubmissionValidator.cs` lines 5-14): `abstract record` with a private ctor or sealed nested records.
**Apply to:** the directory lookup result, policy verdict.

### Value-free logging and static detail
**Source:** `ProposalEndpoints.cs` lines 83-85, 129, 135-136, 170 (ids and truncated hashes or static codes only), `IOperationalAlarm.Raise(code, component)` with no request data.
**Apply to:** all new log lines and the `policy_evaluated` event detail. Never log `on_behalf_of`, group lists, parameters, tokens or the session key. Test with `SentinelScan` (`tests/Olive.Tests/SentinelScan.cs`).

### Tenant transaction and RLS plus application predicate
**Source:** `src/Olive.Persistence/TenantUnitOfWork.cs` lines 11-24 (sets `olive.tenant_id` transaction-locally, then runs `work`, then commits); every `ProposalSql` statement keeps `tenant_id = @tenant_id`.
**Apply to:** the lock, count, insert, CAS and events in `InsertEvaluatedAsync`. Tenant for directory lookup and rate scope is `key.TenantId` from the introspected key only (IDN-04).

### Compare-and-set plus event in one transaction
**Source:** `PostgresProposalStore.cs` lines 83-102 and 145-165 (row-count check, then `InsertEvent`).
**Apply to:** every state transition (POL-06).

### Strict source-controlled config parsing
**Source:** `AgentAllowlist.Parse` (lines 23-52) and `CatalogLoader` (`Strict` options line 15, static `Invalid` message, no values in errors).
**Apply to:** new `entitlements`, `required_entitlement`, `max_expiry_seconds`.

### Embedded fixture resources
**Source:** `Olive.Integrations.Hometown.csproj` line 7 and `FixtureTraceResolver.ReadResource`.
**Apply to:** `contracts/directory/*.json` into `Olive.Integrations.Entra`.

### Contracts additive within v1 with paired examples
**Source:** `contracts/catalog/v1/*`, `contracts/proposal/v1/*` (`*.valid.json`, `*.invalid.json` beside each schema; `ContractSchemaTests`).
**Apply to:** catalog entry, proposal request (`expires_in_seconds`), proposal response (`expires_at`), submit-response (check whether it pins a `refusal_reason` enum; the `badreason` invalid example needs re-checking), directory fixtures.

## No Analog Found

| File / Concern | Role | Data Flow | Reason |
|---|---|---|---|
| Advisory-lock rate count (`pg_advisory_xact_lock`, `hashtextextended`) | repository | transactional | No advisory locks or sliding-window queries exist yet. Use the RESEARCH code example. Verify A1 (`hashtextextended` on postgres:17) and A2 (READ COMMITTED default) with a Testcontainers test. |
| Parallel-request concurrency test | test | concurrent | No existing parallel-POST test; build from `OliveApiFactory` plus `Task.WhenAll`. |
| Sliding-window time test | test | time | `created_at` is DB time and `ManualTimeProvider` cannot advance it. Use a tiny `window_seconds` test catalog or backdate rows through the owner connection. `ManualTimeProvider` is valid only for `expires_at` tests. |

## Metadata

**Analog search scope:** `src/` (all 11 projects), `sql/migrations`, `contracts/`, `catalog/`, `config/`, `tests/Olive.Tests`
**Files read:** about 30
**Pattern extraction date:** 2026-10-22
