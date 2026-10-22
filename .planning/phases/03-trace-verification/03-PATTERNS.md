# Phase 3: Trace Verification - Pattern Map

**Mapped:** 2026-10-22
**Files analyzed:** 40 new/modified
**Analogs found:** 38 / 40

Note: CLAUDE.md's stack text is stale; the repo is the eleven-project layout (Domain, Application, Catalog, Contracts, Persistence, Host, Integrations.Popeye/Hometown/Entra, Executor.Simulated, Migrator). Line numbers below are from the files as read on 2026-10-22.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match |
|---|---|---|---|---|
| `src/Olive.Domain/TraceVerificationSources.cs`, `TraceRefusalReasons.cs` | constants | n/a | `src/Olive.Contracts/ProblemReasons.cs`, `src/Olive.Domain/ProposalStates.cs` | exact |
| `src/Olive.Application/ITraceResolver.cs` (+ `TraceFacts`, `TraceLookup`) | port | request-response | `IKeyIntrospector.cs` + `IntrospectionResult.cs` | role-match |
| `src/Olive.Application/ITokenIssuer.cs` (+ `TokenIssueResult`) | port | request-response | `IKeyIntrospector.cs` | role-match |
| `src/Olive.Application/ServiceTokenGrant.cs` | secret-holding type | n/a | `src/Olive.Domain/ServiceToken.cs` / `SessionKey.cs` | exact |
| `src/Olive.Application/ServiceTokenProvider.cs` | service (cache + validation) | request-response | none for cache; types from `IntrospectionResult.cs` | partial |
| `src/Olive.Application/CachingTraceResolver.cs` | decorator | request-response | `IComponentDecorator.cs` + `RecordingKeyIntrospector` (tests) | role-match |
| `src/Olive.Application/TraceVerifier.cs` | service | transform | Pattern 4 in RESEARCH; `SubmissionOutcome` result style | partial |
| `src/Olive.Application/IOperationalAlarm.cs`, `OutboundPortAttribute.cs` | port / attribute | event | `INonProductionOnly.cs` (marker) | partial |
| `src/Olive.Application/IKeyIntrospector.cs` / `IntrospectionUnavailableException.cs` (modify: caller-rejected) | port / exception | request-response | itself | exact |
| `src/Olive.Application/IProposalStore.cs` (modify: `InsertRefusedAsync`, trace fields, `RefusalReason` on records) | port | CRUD | itself | exact |
| `src/Olive.Integrations.Hometown/FixtureTraceResolver.cs`, `TraceResolutionResponse.cs` | stub + strict parser | request-response | `StubKeyIntrospector.cs` | exact |
| `src/Olive.Integrations.Hometown/Olive.Integrations.Hometown.csproj` (modify) | config | n/a | `Olive.Integrations.Popeye.csproj` | exact |
| `src/Olive.Integrations.Popeye/StubTokenIssuer.cs` | stub | request-response | `StubKeyIntrospector.cs` | exact |
| `src/Olive.Host/Alarms/LoggingOperationalAlarm.cs` | adapter | event | `ProblemExceptionHandler.cs` (value-free logging) | role-match |
| `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (modify) | controller | request-response | itself | exact |
| `src/Olive.Host/Problems/ProblemResults.cs`, `ProblemExceptionHandler.cs` (modify) | utility | request-response | themselves | exact |
| `src/Olive.Host/Program.cs` (modify DI) | config | n/a | itself lines 19-41 | exact |
| `src/Olive.Contracts/ProblemReasons.cs`, `ProposalDtos.cs` (modify) | contract | n/a | themselves | exact |
| `src/Olive.Persistence/PostgresProposalStore.cs`, `ProposalSql.cs` (modify) | repository | CRUD | themselves | exact |
| `sql/migrations/0006_trace_verification.sql` | migration | n/a | `0004_proposal_body_hash.sql`, `0003_proposal_insert_columns.sql` | exact |
| `contracts/trace-resolution/*` (schema, valid/invalid, fixtures, `index.json`, README) | contract | n/a | `contracts/introspection/*` | exact |
| `contracts/proposal/v1/proposal-submit-response.*`, `proposal.*` (modify) | contract | n/a | themselves | exact |
| Tests: `TraceFixtureTests`, `TraceVerifierTests`, `ServiceTokenProviderTests`, `TraceEndpointTests`, `ContainmentTests` | test | n/a | `IntrospectionFixtureTests.cs`, `SecretLeakageTests.cs`, `ArchitectureTests.cs` | exact |
| `tests/Olive.Tests/ManualTimeProvider.cs`, `RecordingPorts.cs`, `SentinelScan.cs` | test double | n/a | `TestDoubles.cs` | exact |
| `tests/Olive.Tests/OliveApiFactory.cs` (modify: trace mode) | test harness | n/a | itself | exact |
| `PrivilegeMatrixTests.cs`, `MigrationRunnerTests.cs`, `StartupGuardTests.cs` (modify) | test | n/a | themselves | exact |

## Pattern Assignments

### Domain constants: `TraceVerificationSources.cs`, `TraceRefusalReasons.cs`
**Analog:** `src/Olive.Contracts/ProblemReasons.cs` (lines 4-18): static class of `public const string` snake_case values.
```csharp
public static class ProblemReasons
{
    public const string KeyInactive = "key_inactive";
    public const string IntrospectionUnavailable = "introspection_unavailable";
    ...
}
```
Add `Unresolved="trace_unresolved"`, `TenantMismatch="trace_tenant_mismatch"`, `AgentMismatch="trace_agent_mismatch"`, `ProposalMismatch="trace_proposal_mismatch"`; sources `Fixture="fixture"`, `Hometown="hometown"`. Also add to ProblemReasons: `TraceNotFound="trace_not_found"`, `TraceVerificationUnavailable="trace_verification_unavailable"`.

---

### `ITraceResolver`, `ITokenIssuer`, `TraceLookup`, `TokenIssueResult` (port, request-response)
**Analog:** `src/Olive.Application/IKeyIntrospector.cs` (lines 1-7) and `IntrospectionResult.cs` (lines 3-9).
```csharp
namespace Olive.Application;
public interface IKeyIntrospector { Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct); }

public sealed record ActiveKey(string KeyId, string PrincipalType, string? AgentId, string? OnBehalfOf, string Purpose, Guid TenantId, string DataClassCeiling, DateTimeOffset ExpiresAt);
public sealed record IntrospectionResult(bool Active, ActiveKey? Key)
{
    public static IntrospectionResult Inactive { get; } = new(false, null);
    public static IntrospectionResult From(ActiveKey key) => new(true, key);
}
```
Use file-scoped `namespace Olive.Application;`, one-line comment on the contract. Result hierarchies per RESEARCH Patterns 1-2 (nested sealed records under an abstract record). Records are fine for `TraceFacts`/`TraceLookup` (no secrets).

### `ServiceTokenGrant` (secret holder)
**Analog:** `src/Olive.Domain/ServiceToken.cs` (lines 6-28). Must be a sealed class, never a record; override `ToString` and implement `IFormattable` returning `"[REDACTED]"`; add `[DebuggerDisplay("[REDACTED]")]` and `[JsonConverter(typeof(RedactingJsonConverter<...>))]`.
```csharp
[DebuggerDisplay("[REDACTED]")]
[JsonConverter(typeof(RedactingJsonConverter<ServiceToken>))]
public sealed class ServiceToken : IFormattable
{
    ...
    public override string ToString() => "[REDACTED]";
    public string ToString(string? format, IFormatProvider? formatProvider) => "[REDACTED]";
}
```
`RedactingJsonConverter` lives in `Olive.Domain`; if the grant goes in Application it still can reference Domain. Note SecretTypeTests exists (`tests/Olive.Tests/SecretTypeTests.cs`): extend it to cover the grant.

### `ServiceTokenProvider`, `TraceVerifier`, `CachingTraceResolver` (Application orchestration)
No in-repo analog for caches or the verifier; use RESEARCH Patterns 3 and 4 verbatim. Conventions to copy:
- Primary-constructor services; `TimeProvider time` injected (`AgentKeyAuthenticationHandler` line 16: `TimeProvider time`, use `time.GetUtcNow()`). `DateTime.UtcNow` is banned (BannedSymbols.txt).
- Application is logging-free (no `ILogger`); failures returned as typed results, alarms via `IOperationalAlarm`.
- Decorator must expose inner for the FND-05 guard. **Analog:** `IComponentDecorator.cs` lines 5-8:
```csharp
public interface IComponentDecorator { object Inner { get; } }
```
and `StartupGuards.IsNonProduction` (lines 100-113) walks `Inner`. `CachingTraceResolver : ITraceResolver, IComponentDecorator` with `public object Inner => inner;`.
- Timeout pattern: linked CTS + `CancelAfter`, catch `OperationCanceledException when (!ct.IsCancellationRequested)` (RESEARCH Pitfall 4). Use `new CancellationTokenSource(timeout, timeProvider)`.

### `IntrospectionUnavailableException` caller-rejected (modify)
**Analog:** `src/Olive.Application/IntrospectionUnavailableException.cs` (lines 4-7): sealed class with static `Reason`. It is sealed, so either unseal and add a derived `IntrospectionCallerRejectedException`, or add a bool/enum to the existing type. Handler mapping lives in `ProblemExceptionHandler.cs` lines 12-14:
```csharp
var (status, reason) = exception is IntrospectionUnavailableException
    ? (StatusCodes.Status503ServiceUnavailable, ProblemReasons.IntrospectionUnavailable)
    : (StatusCodes.Status500InternalServerError, ProblemReasons.InternalError);
logger.LogError("Request failed with {ExceptionType}.", exception.GetType().Name);
```
Keep logging of type name only. Raise `IOperationalAlarm` (inject into the handler) for the caller-rejected case with code `introspection_caller_rejected`. The auth handler (`AgentKeyAuthenticationHandler.cs` line 28-29) deliberately does not catch it; leave that.

---

### `src/Olive.Integrations.Hometown/FixtureTraceResolver.cs` + `TraceResolutionResponse.cs` (stub, request-response)
**Analog:** `src/Olive.Integrations.Popeye/StubKeyIntrospector.cs`.

Class shape and embedded-resource loading (lines 14-35):
```csharp
public sealed partial class StubKeyIntrospector(IReadOnlyDictionary<string, string> keyToJson, TimeProvider time) : IKeyIntrospector, INonProductionOnly
{
    private const string Malformed = "malformed_introspection_response";
    [GeneratedRegex("^[0-9a-f]{8}-...$")] private static partial Regex UuidPattern();

    public static StubKeyIntrospector CreateDefault(TimeProvider time)
    {
        var asm = typeof(StubKeyIntrospector).Assembly;
        var index = JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(asm, "introspection/index.json")) ?? throw ...;
        var map = index.ToDictionary(kv => kv.Key, kv => ReadResource(asm, "introspection/" + kv.Value), StringComparer.Ordinal);
        return new StubKeyIntrospector(map, time);
    }
    private static string ReadResource(Assembly asm, string name) { using var s = asm.GetManifestResourceStream(name) ?? throw ...; ... }
```
Strict parsing pattern (lines 44-107, 109-125): `JsonDocument.Parse`, verify each required field kind, `Str`/`OptStr` helpers rejecting wrong kinds or empty strings, `catch (JsonException)` to malformed. For the resolver, malformed becomes `TraceLookup.Malformed` (not an exception), but keep the same helper style; exactly seven fields, unknown property -> malformed; `run_id`, `agent_id`, `proposal_hash` nullable (JSON null allowed), `tenant_id` lowercase UUID, `schema_version` `^ate/[0-9]+\.[0-9]+$`.

csproj embedding (`Olive.Integrations.Popeye.csproj` lines 3-8):
```xml
<ProjectReference Include="..\Olive.Application\Olive.Application.csproj" />
<ProjectReference Include="..\Olive.Domain\Olive.Domain.csproj" />
<EmbeddedResource Include="..\..\contracts\introspection\*.json" LogicalName="introspection/%(Filename)%(Extension)" />
```
Copy to the Hometown csproj with `contracts\trace-resolution\*.json` and `LogicalName="trace-resolution/..."`. The Hometown project currently has only `AssemblyMarker.cs` and a csproj; check it already references Application/Domain (FND-01 test `FND_01_project_references_match_inward_graph` in `ArchitectureTests.cs` line 41 pins the allowed graph).
Embedding the `*.proposal.json` files is optional; the schema must not match them (`index.json` should map trace_ref -> response file only). Implement `INonProductionOnly` (`src/Olive.Application/INonProductionOnly.cs`). Resolver returns the fixture verbatim and does not tenant-scope (D-17); `Found(facts, "fixture")`.

### `src/Olive.Integrations.Popeye/StubTokenIssuer.cs` (stub)
**Analog:** `StubKeyIntrospector.cs` as above. Reuse its embedded fixtures (`introspection/*.json`) to derive tenant/agent/key_id/expiry from the presented `SessionKey` (call `ExposeSecret()` as the stub does at line 39). Token value from `RandomNumberGenerator` (RESEARCH). Return `TokenIssueResult.Refused` for unregistered tenant/agent (A2). `INonProductionOnly`. Place `[OutboundPort(OutboundDestination.Popeye)]` on the interface, not the stub.

### `src/Olive.Host/Alarms/LoggingOperationalAlarm.cs`
**Analog:** `ProblemExceptionHandler.cs` lines 8-18 (primary constructor `ILogger<T>`, static message template, ids only, never exception text). Write `logger.LogCritical("Operational alarm {AlarmCode} component={Component}", code, component)`. Category `Olive.Alarm` per RESEARCH Pitfall 10 (use `ILoggerFactory.CreateLogger("Olive.Alarm")`, mirroring `ProposalEndpoints.LogCategory` line 16).

---

### `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (modify `PostAsync`, `GetAsync`)
**Analog:** itself. Insertion point: after the `validator.Validate(root)` switch (lines 95-102), before field extraction (line 104). Hoist `Jcs.ProposalHash(root)` (currently inline at line 115).

Existing shape to preserve:
```csharp
var replay = await DecideExistingAsync();           // lines 89-93: replay/409 never reach the resolver
...
switch (validator.Validate(root)) { case SubmissionOutcome.Violation v: return ProblemResults.Result(400, ...); case SubmissionOutcome.NotInCatalog: ... }
// <-- new trace step here
var proposal = new NewProposal(key.AgentId, key.KeyId, key.OnBehalfOf, key.Purpose, key.ExpiresAt, nonoSessionId, runId, actionType, partyRef, parametersJson, justification, traceRef, Encoding.UTF8.GetString(canonical), bodySha, Jcs.ProposalHash(root));
id = await store.InsertAsync(key.TenantId, proposal, ct);   // wrapped in try/catch PostgresException (lines 117-130)
if (id is null) { return await DecideExistingAsync() ?? ProblemResults.Result(409, IdempotencyConflict); }   // lost race, lines 132-136
return Results.Created($"/v1/proposals/{id}", new ProposalCreatedResponse(id.Value, ProposalStates.Proposed));
```
Changes:
- Signature adds `TraceVerifier verifier, IOperationalAlarm?` as needed (parameters are DI-injected, see line 26).
- Session key: `AgentKeyHeader.TryRead(http.Request.Headers[AgentKeyHeader.Name].ToString())` (same call as `AgentKeyAuthenticationHandler.cs` line 22).
- Verdict switch maps to: NotFound -> 422 `trace_not_found` with `retryable:true`; Unavailable -> 503 + `Retry-After: 5` + `retryable:true`; Refused -> `store.InsertRefusedAsync` -> `Results.Created(..., new ProposalCreatedResponse(id, "refused_policy", reason))`; Verified -> existing insert with trace fields.
- The lost-race branch and the PostgresException catch must wrap both Insert paths.
- Replay (line 80) must return `existing.RefusalReason` in the body: `Results.Ok(new ProposalCreatedResponse(existing.ProposalId, existing.State, existing.RefusalReason))`.
- Logging style (lines 84-85): `logger.LogWarning("... tenant_id={TenantId} agent_id={AgentId} ...", ...)`: ids and `Short(hash)` only. Add Trace-level lines per RESEARCH Pitfall 7, never trace_ref values/tokens.
- `GetAsync` (lines 177-195): positional `ProposalResponse` construction; extend with `r.RefusalReason, r.TraceVerification, r.TraceSchemaVersion`.

### `ProposalDtos.cs` (modify)
**Analog:** itself. Add optional fields with `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` plus `JsonPropertyName("refusal_reason")`. `ProposalCreatedResponse(Guid ProposalId, string State, string? RefusalReason = null)`: the API_01 test asserts the exact property set `{proposal_id, state}` for accepted responses, so null must be omitted. Note the file currently has no `JsonIgnore` usages; `using System.Text.Json.Serialization;` is already present (line 2).

### `ProblemResults.cs` (modify)
**Analog:** itself. Add reasons to the `Title` switch (lines 21-35, static text only) and a new overload alongside lines 45-54 for retryable:
```csharp
public static IResult Result(int status, string reason) =>
    Results.Json(Create(status, reason), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", status);
```
New overload sets `problem.Extensions["retryable"] = true;` (same pattern as `Extensions["pointer"]` line 48). For 503 add `Retry-After` header via a small `IResult` wrapper or `http.Response.Headers.RetryAfter = "5"` before returning (precedent: `http.Response.Headers.Location = ...` line 79 and `Response.Headers.WWWAuthenticate` in auth handler line 41).

---

### `src/Olive.Persistence/PostgresProposalStore.cs` + `ProposalSql.cs` (add `InsertRefusedAsync`, trace columns)
**Analog:** itself.

Insert + event within one tenant unit of work (lines 32-72) and CAS transition (lines 100-121, `ProposalSql.UpdateState`):
```csharp
public Task<Guid?> InsertAsync(Guid tenantId, NewProposal p, CancellationToken ct) =>
    uow.ExecuteAsync<Guid?>(tenantId, async (conn, tx) =>
    {
        var id = Guid.NewGuid();
        var inserted = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(ProposalSql.Insert, new { tenant_id = tenantId, proposal_id = id, ..., proposal_hash = p.ProposalHash }, tx, cancellationToken: ct));
        if (inserted is null) { return null; }
        await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new { tenant_id = tenantId, event_id = Guid.NewGuid(), proposal_id = id, event_type = "submitted", from_state = (string?)null, to_state = "proposed", actor = p.AgentId }, tx, cancellationToken: ct));
        return id;
    }, ct);
```
`InsertRefusedAsync`: same body, then execute a new `ProposalSql.RefuseWithReason` (`update proposal set state = 'refused_policy', refusal_reason = @reason where tenant_id = @tenant_id and proposal_id = @proposal_id and state = 'proposed'`, same shape as `UpdateState` line 22-23), assert one row, then `InsertEvent` with `event_type = "trace_refused"`, `from_state="proposed"`, `to_state="refused_policy"` (event detail static). Do not put `state` in the INSERT (olive_app has no INSERT privilege on it, 0003).

SQL constants (`ProposalSql.cs` lines 6-9): extend the insert column list and parameters with `trace_verification, trace_schema_version`; every statement keeps `tenant_id = @tenant_id`. `Get` (line 15-17) and `FindByRun` (line 19-20) add `refusal_reason` (+ trace columns for Get). Update `Row`/`RunRow` classes (lines 10-28, 83-90) with nullable properties (Dapper `MatchNamesWithUnderscores = true`, line 8) and the record constructors in `ProposalRecord`/`ProposalRunRecord` (`IProposalStore.cs` lines 19-23). `NewProposal` gains `TraceVerification`, `TraceSchemaVersion` as optional trailing parameters (existing style: defaults at the end, line 19).

Test double to update in lock-step: `RecordingProposalStore` (`tests/Olive.Tests/TestDoubles.cs` lines 26-51): add `InsertRefusedAsync` forwarding with `events.Add("insert")` style (suggest `"insert_refused"`).

### `sql/migrations/0006_trace_verification.sql`
**Analogs:** `0004_proposal_body_hash.sql` (add columns + format checks + additive INSERT grant) and `0003`/`0005` header comment style.
```sql
-- API-06 / D-06 / D-08: ...
-- 0001-0003 are checksum-pinned and unchanged.
alter table proposal add column canonical_body text, add column body_sha256 text, add column proposal_hash text;
alter table proposal add constraint proposal_body_sha256_format check (body_sha256 is null or body_sha256 ~ '^[0-9a-f]{64}$');
...
-- Additive: 0003 already revoked table-wide INSERT; the column UPDATE grant from 0002 (state only) is untouched.
grant insert (canonical_body, body_sha256, proposal_hash) on proposal to olive_app;
```
Use the SQL sketch in RESEARCH Pitfall 2 (columns nullable; CHECKs; `grant insert (trace_verification, trace_schema_version)`; `grant update (refusal_reason)`). Existing `state` CHECK allows `refused_policy` already (0001 line 6). Migration files must be contiguous `NNNN_snake_case.sql` (ArchitectureTests `DB_02_...` lines 168-212); check whether `MigrationCatalog` embeds `sql/migrations/*.sql` by glob (`src/Olive.Persistence/MigrationCatalog.cs`).

### `PrivilegeMatrixTests` / `MigrationRunnerTests` (modify)
**Analog:** `PrivilegeMatrixTests.cs`:
- Line 133-134: `InsertableColumns` array: append `"trace_verification", "trace_schema_version"`.
- Lines 57-74 `DB_04_app_update_is_limited_to_state_column` asserts `col == "state"`; change to `col is "state" or "refusal_reason"` (and rename).
- Add a test following lines 76-89 that `update proposal set trace_verification = 'x'` still yields `42501`, plus direct-SQL CHECK rejection test (owner connection) for invalid `trace_verification`.
- `MigrationRunnerTests.cs`: update expected migration count/list (read file when planning; not read here).

### Contracts: `contracts/trace-resolution/`
**Analog:** `contracts/introspection/` (schema, `index.json`, one JSON per case, README; embedded into the stub assembly).
Schema style (`introspection-response.schema.json`): draft 2020-12, `$id`, `title`, `required`, `additionalProperties: false`, lowercase-UUID regex for ids:
```json
"tenant_id": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$" },
"expires_at": { "type": "string", "format": "date-time" }
```
Fixture style (`liam-alpha.json`): flat object using tenant `5325ffe9-5727-4d88-9832-aebabb8d0538` (Alpha). Nullable fields in trace schema: `"type": ["string","null"]`. Add `<case>.proposal.json` beside each response (not schema-validated as a response; keep out of `index.json`). Because Hometown embedding uses a `*.json` glob, name the schema/proposal files so the glob does not break the loader (embed only `index.json` + response files, or have the loader read only index entries, as `CreateDefault` does).
Paired valid/invalid examples: `trace-resolution-response.valid.json` / `.invalid.json` (convention in `contracts/proposal/v1/`). Add to `ContractSchemaTests`.

Fixture tests analog: `IntrospectionFixtureTests.cs` (lines 8-25): `TestPaths.Root`, `SchemaValidator.Compile(File.ReadAllText(...))`, `Assert.Null(Validator.Validate(Load(file)))`. Add `TRC_01_*` tests including `fixture.proposal_hash == Jcs.ProposalHash(proposal)` per pair.

### Contracts: `proposal-submit-response.schema.json`, `proposal.schema.json`
**Analog:** `proposal-submit-response.schema.json` (lines 6-11): `additionalProperties:false`, `required: [proposal_id, state]`. Add optional `refusal_reason` (`pattern ^[a-z][a-z_]{0,63}$`); for `proposal.schema.json` also `trace_verification` (enum fixture/hometown) and `trace_schema_version`. Update `.valid.json`/`.invalid.json` pairs.

---

### Tests

**`ManualTimeProvider`, recording ports, faulting doubles** - **Analog:** `tests/Olive.Tests/TestDoubles.cs`: primary-constructor sealed classes, `lock (events) { events.Add("name"); }`, `Interlocked.Increment` counters:
```csharp
public sealed class RecordingKeyIntrospector(IKeyIntrospector inner, List<string> events) : IKeyIntrospector
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public async Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct)
    { Interlocked.Increment(ref _calls); lock (events) { events.Add("introspect"); } return await inner.IntrospectAsync(key, ct); }
}
public sealed class ThrowingKeyIntrospector : IKeyIntrospector { ... throw new IntrospectionUnavailableException("test_unavailable"); }
```
New recorders should also implement `IComponentDecorator` (RESEARCH recording decorator example) so the StartupGuard sees through them. Event names for order evidence: `"token"`, `"resolve"`, `"insert"` alongside existing `"introspect"`, `"FindByRun"`, `"insert"` (`Events` list on the factory).

**`OliveApiFactory` trace mode** - **Analog:** itself (lines 15-55). Constructor already takes `configureServices` and `extraConfig`; `ConfigureTestServices` (line 43) registers `RecordingKeyIntrospector` via `services.AddSingleton<IKeyIntrospector>(Introspector)` and a scoped recording store. Do the same: register resolver/issuer/alarm as recording singletons in this block, with a default auto-fixture resolver (test project only) that computes `Jcs.ProposalHash` per submission. The test factory uses `TimeProvider.System` (line 47); IDN-06 expiry tests can unit-test `ServiceTokenProvider` with `ManualTimeProvider` instead. `environment: "ci"` means INonProductionOnly components are allowed; production test uses `environment: "nonprod"`-style variants as in `StartupGuardTests`.

**Startup guard test** - **Analog:** `StartupGuardTests.cs` (`TryStartAsync`, lines 32-44; FND-05 cases below line 80, not fully read) - add production + `FixtureTraceResolver` and + `StubTokenIssuer`, including wrapped in `CachingTraceResolver`.

**Secret sentinel test** - **Analog:** `SecretLeakageTests.cs` (lines 18-24 `EverythingAsync` = body + headers + `factory.Logs.Text`; lines 83-90 structured-logging redaction test). Extend into reusable `SentinelScan`; add row scan via `select row_to_json(t)::text from proposal t`.

**Architecture test for `[OutboundPort]`** - **Analog:** `ArchitectureTests.cs` (`FND_02_domain_application_executor_do_not_reference_http_assemblies` lines 65-85, uses `typeof(Olive.Application.AssemblyMarker).Assembly`; `[Fact]` + `[Trait("Category","Architecture")]`). Add reflection test per RESEARCH. Also update `FND_01_project_references_match_inward_graph` if Hometown gains references.

Naming: `TRC_0x_...`, `IDN_0x_...` (e.g. `IDN_01_all_introspection_fixtures_validate_against_schema`), `[Trait("Category", "Unit")]` or `"Integration"` with `PostgresFixture fixture` constructor injection (`StartupGuardTests` line 11), `await using var db = await fixture.CreateDatabaseAsync(); await using var factory = new OliveApiFactory(db, ...)`, `TestContext.Current.CancellationToken`.

## Shared Patterns

### Secret-holding types
**Source:** `src/Olive.Domain/ServiceToken.cs`, `SessionKey.cs`. **Apply to:** `ServiceTokenGrant`, any outcome type carrying a token (never a record).

### Non-production bar
**Source:** `src/Olive.Application/INonProductionOnly.cs`, `IComponentDecorator.cs`, `StartupGuards.CheckNoNonProductionComponents` (lines 45-95) and `IsNonProduction` (lines 100-113). **Apply to:** `FixtureTraceResolver`, `StubTokenIssuer` (INonProductionOnly); `CachingTraceResolver` and every recording decorator (IComponentDecorator). Registration with factory lambdas is also walked by the guard.

### Value-free logging and static problem text
**Source:** `ProposalEndpoints.cs` lines 83-85, 128; `ProblemExceptionHandler.cs` line 15; `ProblemResults.Title` lines 21-35. **Apply to:** all Host code in this phase: ids, 12-char hash prefixes, reason codes only; never trace_ref, tokens, parameters, exception messages.

### Tenant predicate plus RLS
**Source:** `ProposalSql.cs` (every statement has `tenant_id = @tenant_id`), `TenantUnitOfWork` via `uow.ExecuteAsync(tenantId, ...)`. **Apply to:** new refusal SQL.

### Time
**Source:** `AgentKeyAuthenticationHandler.cs` (`TimeProvider time`, `time.GetUtcNow()`); `StubKeyIntrospector` ctor takes `TimeProvider`. **Apply to:** `ServiceTokenProvider`, `CachingTraceResolver` (if TTL), `StubTokenIssuer`, timeout CTS. `DateTime.UtcNow` is a banned symbol.

### Typed outcome instead of exceptions
**Source:** `SubmissionOutcome.Violation/NotInCatalog` consumed in `ProposalEndpoints.cs` lines 96-102 (pattern `switch` with `case X v:`). **Apply to:** `TraceVerdict`, `TraceLookup`, `TokenIssueResult`.

### Requirement IDs in names
**Apply to:** every test `TRC_0x_`, `IDN_0x_`; reference IDs in migration header comments as 0003/0004 do.

### Allowlist and banned API constraints
No new NuGet packages (`FND_02_direct_packages_match_allowlist_in_lockfiles`), no HttpClient or sockets (`REQ_ACT_04_...`, `BannedSymbols.txt`). The Hometown/Popeye projects must not gain package references.

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `ServiceTokenProvider` / `CachingTraceResolver` cache internals | service | in-memory cache | No caches exist yet (D-14 had no introspection cache); use RESEARCH Pattern 3 with bounded `ConcurrentDictionary` |
| `TraceVerifier` | service | transform | No verifier exists; use RESEARCH Pattern 4 |
| `IOperationalAlarm` + `LoggingOperationalAlarm` | port/adapter | event | No alarm concept exists; closest style is `ProblemExceptionHandler` logging |
| `OutboundPortAttribute` reflection test | architecture test | n/a | Existing architecture tests inspect assembly references, not interface attributes; copy structure of `ArchitectureTests.cs` lines 65-85 |
| `ManualTimeProvider` | test double | n/a | No TimeProvider fake exists; hand-write (no new package) |

## Metadata

**Analog search scope:** `src/**`, `tests/Olive.Tests/**`, `sql/migrations/**`, `contracts/**`
**Files read:** ProposalEndpoints, PostgresProposalStore, ProposalSql, StubKeyIntrospector, IProposalStore, IComponentDecorator, IKeyIntrospector, IntrospectionUnavailableException, IntrospectionResult, ServiceToken, SessionKey, ProblemResults, ProblemExceptionHandler, ProblemReasons, ProposalDtos, Program, StartupGuards, StartupGuardFilter, AgentKeyAuthenticationHandler, OliveApiFactory, TestDoubles, IntrospectionFixtureTests, SecretLeakageTests (part), StartupGuardTests (part), PrivilegeMatrixTests (part), ArchitectureTests (grep), migrations 0003-0005, introspection schema and fixture, proposal-submit-response schema, Popeye csproj
**Not read (read when planning):** `MigrationRunnerTests.cs`, `MigrationCatalog.cs`, `Olive.Integrations.Hometown.csproj`, `proposal.schema.json`, `ContractSchemaTests.cs`, `SecretTypeTests.cs`, `ApiProposalTests.cs` (API_01 exact property assertion)
**Pattern extraction date:** 2026-10-22
