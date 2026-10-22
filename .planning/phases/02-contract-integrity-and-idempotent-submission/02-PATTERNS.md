# Phase 2: Contract Integrity and Idempotent Submission - Pattern Map

**Mapped:** 2026-10-22
**Files analyzed:** 27 new/modified
**Analogs found:** 24 / 27

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Olive.Domain/Jcs/Jcs.cs` (+ hash helper) | utility | transform | `src/Olive.Domain/SessionKey.cs` (pure BCL, sealed, leaf) | role-match |
| `src/Olive.Host/Endpoints/IJsonGate.cs` (extracted gate) | utility | transform | `ProposalEndpoints.cs` lines 136-306 (extract in place) | exact (move) |
| `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (modify PostAsync) | controller | request-response | itself | exact |
| `src/Olive.Host/Endpoints/CatalogEndpoints.cs` | controller | request-response | `ProposalEndpoints.GetAsync` + `MapProposalEndpoints` | exact |
| `src/Olive.Catalog/CatalogLoader.cs` (YamlStream to JSON, manifest) | service | file-I/O | `src/Olive.Catalog/AgentAllowlist.cs` | role-match (research says typed Deserialize is insufficient: dup keys) |
| `src/Olive.Catalog/CatalogVetting.cs` (D-02) | utility | transform | `AgentAllowlist.Parse` (throw `InvalidOperationException` on invalid) | partial |
| `src/Olive.Catalog/SchemaValidator.cs` (JsonSchema.Net wrapper) | service | transform | none; see RESEARCH Pattern 4 | no analog |
| `src/Olive.Catalog/Olive.Catalog.csproj` (add pkg and embed catalog and contracts) | config | n/a | itself (line 10 EmbeddedResource) | exact |
| `Directory.Packages.props` | config | n/a | existing YamlDotNet entry | exact |
| `src/*/packages.lock.json` (regenerate) | config | n/a | existing | exact |
| `catalog/catalog.yaml` (manifest) | config | file-I/O | `catalog/crm.case_note@1.yaml`, `config/agents.yaml` | role-match |
| `src/Olive.Application/IProposalStore.cs` (add `FindByRunAsync`, new fields) | port | CRUD | itself | exact |
| `src/Olive.Persistence/ProposalSql.cs` (insert cols, `FindByRun`) | model/sql | CRUD | itself (`Get` and `Insert`) | exact |
| `src/Olive.Persistence/PostgresProposalStore.cs` | service | CRUD | itself (`InsertAsync`, `GetAsync`) | exact |
| `sql/migrations/0004_proposal_body_hash.sql` | migration | CRUD | `0003_proposal_insert_columns.sql` | exact |
| `src/Olive.Contracts/ProblemReasons.cs` | config | n/a | itself | exact |
| `src/Olive.Host/Problems/ProblemResults.cs` (titles; extension members) | utility | request-response | itself | exact |
| `src/Olive.Host/Program.cs` / `StartupGuards.cs` (catalog guard) | config | startup | `AddSingleton<IAgentAllowlist>(...LoadEmbedded())` at Program.cs:34 | exact |
| `contracts/proposal/v1/*.schema.json` + examples | contract | n/a | `contracts/schemas/proposal.schema.json` + `contracts/examples/proposal.*.json` | exact (move/reshape) |
| `contracts/catalog/v1/action-type.schema.json` + examples | contract | n/a | `contracts/schemas/action-type.schema.json` | exact (move) |
| `contracts/queue-item/v?/`, `contracts/execution-record/v?/` (placeholders) | contract | n/a | `contracts/schemas/{queue-item,execution-record}.schema.json` | exact (move) |
| `contracts/jcs/v1/` (README, vectors.json, schema, pairs) | contract/fixture | transform | `contracts/introspection/` (README, index.json, schema) | role-match |
| `tests/Olive.Tests/JcsTests.cs`, `IJsonGateTests.cs` | test | transform | `IntrospectionFixtureTests.cs` (Unit trait, TestPaths) | role-match |
| `tests/Olive.Tests/IdempotencyTests.cs`, `ApiIntegrityTests.cs`, `CatalogTests.cs` | test | request-response | `ApiProposalTests.cs` | exact |
| `tests/Olive.Tests/ContractSchemaTests.cs` + `IntrospectionFixtureTests.cs` (drop `JsonSchemaLite`) | test | transform | `IntrospectionFixtureTests.cs` | exact |
| `tests/Olive.Tests/MigrationRunnerTests.cs`, `PrivilegeMatrixTests.cs`, `ArchitectureTests.cs` (expectations) | test | n/a | themselves | exact |
| `CLAUDE.md`, `README.md`, `docs/STATUS.md`, `AGENTS.md` (contract paths) | docs | n/a | n/a | n/a |
| `.gitattributes` (`contracts/jcs/v1/* -text`) | config | n/a | existing `.gitattributes` | exact |

## Pattern Assignments

### `src/Olive.Host/Endpoints/ProposalEndpoints.cs` (controller, request-response) - modify

**Analog:** itself (351 lines). Keep the order: auth handler, principal checks (lines 28-37), content type, bounded read (44-48), parse, then new steps.

**Principal checks to keep unchanged** (lines 28-42):
```csharp
var key = http.User.ToActiveKey();
if (key.PrincipalType != PrincipalTypes.Agent)
    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.PrincipalNotPermitted);
if (key.AgentId is null || key.OnBehalfOf is null || !allowlist.IsAllowed(key.AgentId))
    return ProblemResults.Result(StatusCodes.Status403Forbidden, ProblemReasons.AgentNotAllowlisted);
if (!http.Request.HasJsonContentType()) return Invalid();
```

**Parse change** (lines 50-58). Add `AllowDuplicateProperties = false` and catch BOTH exception types (lone-surrogate property name throws `InvalidOperationException`; RESEARCH Pitfall 1):
```csharp
try { doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth, AllowDuplicateProperties = false }); }
catch (Exception e) when (e is JsonException or InvalidOperationException) { return Invalid(); }
```

**Gate scope change.** Phase 1 walks only `parameters` (`TryParameters` lines 308-330, `AllStringsValid` 275-306). Phase 2 must call the walk on `root` (whole body), add the lossy-number check inside `IsNumberInRange` (lines 237-273), and drop the `TryOptionalString` chain (lines 65-70) in favour of envelope schema validation (after idempotency). Keep `TryRunId` (136-151) as the pre-idempotency run_id check.

**Race and conflict pattern** (replace lines 94-97). Existing store already returns `null` on conflict (IProposalStore.cs line 5). New shape per RESEARCH Pattern 3:
```csharp
var existing = await store.FindByRunAsync(key.TenantId, key.AgentId, runId, ct);  // before schema/catalog
if (existing is not null) return existing.BodySha256 == bodySha ? Replay(existing) : Conflict(...);
...
var id = await store.InsertAsync(key.TenantId, proposal, ct);
if (id is null) { /* re-lookup, same compare: 200 or 409 */ }
```

**Existing DB data-fault mapping to keep** (lines 83-92) with its static-log pattern:
```csharp
catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.NumericValueOutOfRange or ...)
{
    loggerFactory.CreateLogger(LogCategory).LogWarning("Proposal insert refused by database data check {SqlState}", e.SqlState);
    return Invalid();
}
```
Use the same `LogCategory` + static-template `LogWarning` for the 409 line (ids and truncated hashes only, never body or key).

**Success responses** (line 99): `Results.Created($"/v1/proposals/{id}", new ProposalCreatedResponse(id.Value, ProposalStates.Proposed))`. Replay: `Results.Json(new ProposalCreatedResponse(existing.ProposalId, existing.State), statusCode: 200)` (optionally add `Location` header).

**Tenant rule:** tenant from the introspected key only (comment line 75); lookup passes `key.TenantId`.

---

### `src/Olive.Host/Endpoints/CatalogEndpoints.cs` (controller, request-response)

**Analog:** `ProposalEndpoints.cs` lines 18-23 and 332-350.

**Route registration pattern:**
```csharp
public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
{
    app.MapGet("/v1/proposals/{id:guid}", GetAsync).RequireAuthorization();
    return app;
}
```
Copy as `MapCatalogEndpoints`, `app.MapGet("/v1/catalog/{version}", ...).RequireAuthorization()`, NO principal-type check (D-11). Take `version` as string; unparseable or unknown gives 404 problem.

**404 pattern** (lines 335-339):
```csharp
if (r is null)
    return ProblemResults.Result(StatusCodes.Status404NotFound, ProblemReasons.ProposalNotFound);
```
Use `ProblemReasons.CatalogVersionNotFound`. Return JSON from stored `JsonElement` so numbers and booleans pass through unchanged (as `GetAsync` does with `pd.RootElement.Clone()`, lines 341-346).

---

### `src/Olive.Catalog/CatalogLoader.cs` and `CatalogVetting.cs` (service, file-I/O)

**Analog:** `src/Olive.Catalog/AgentAllowlist.cs`

**Imports and invalid-message pattern** (lines 1-10): `using YamlDotNet.Serialization;` plus a single static `Invalid` message, fail closed.

**Fail-closed catch pattern** (lines 25-40):
```csharp
try { doc = new DeserializerBuilder()...Deserialize<AllowlistDocument?>(yaml); }
catch (YamlDotNet.Core.YamlException) { throw new InvalidOperationException(Invalid); }
if (doc?.Agents is not { Count: > 0 } agents) throw new InvalidOperationException(Invalid);
```

**Embedded resource load pattern** (lines 54-60):
```csharp
using var s = typeof(AgentAllowlist).Assembly.GetManifestResourceStream("config/agents.yaml")
    ?? throw new InvalidOperationException(Invalid);
using var r = new StreamReader(s);
return Parse(r.ReadToEnd());
```
Provide `Parse(string)` (testable) plus `LoadEmbedded()`. Deviation required by RESEARCH Pattern 5: do NOT rely on typed `Deserialize<T>` (silently accepts duplicate YAML keys and types all scalars as string). Use `YamlStream.Load` (throws on duplicate keys), convert to `JsonNode` with YAML core-schema scalar typing, then strict-bind with `UnmappedMemberHandling = Disallow`. Keep the "no `IgnoreUnmatchedProperties`" rule and never reintroduce a line-based parser. No `AddAtRuntime`-style API (test API_10).

Also apply: duplicate ids rejected via `HashSet.Add` (line 45 idiom), id regex via `[GeneratedRegex]` (lines 20-21; catalog id is `^[a-z0-9_.]+@[0-9]+$`), purposes checked against `contracts/vendored/vocabulary.yaml` through `src/Olive.Application/VendoredContracts.cs`.

---

### `src/Olive.Catalog/Olive.Catalog.csproj` and `Directory.Packages.props` (config)

**Analog:** itself.
```xml
<ItemGroup><PackageReference Include="YamlDotNet" /></ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="..\..\config\*.yaml" LogicalName="config/%(Filename)%(Extension)" />
</ItemGroup>
```
Add `<PackageReference Include="JsonSchema.Net" />` (version only in `Directory.Packages.props`; 8.0.5 MIT per RESEARCH, user decision on licence pending) and a second `EmbeddedResource` for `..\..\catalog\*.yaml` (`LogicalName="catalog/%(Filename)%(Extension)"`) and the contract schemas. Update `ArchitectureTests.PackageAllowlist["Olive.Catalog"]` (tests/Olive.Tests/ArchitectureTests.cs line 32) to `["JsonSchema.Net", "YamlDotNet"]`. Regenerate lockfiles (`dotnet restore --force-evaluate`).

---

### `sql/migrations/0004_proposal_body_hash.sql` (migration)

**Analog:** `sql/migrations/0003_proposal_insert_columns.sql` and `0002_proposal_column_grants.sql`

**Pattern** (0003 lines 1-5): header comment citing requirement and "earlier migrations are checksum-pinned and unchanged", then the grant. Because 0003 already revoked table-wide INSERT, 0004 is purely additive:
```sql
-- 0001-0003 are checksum-pinned and unchanged.
alter table proposal add column canonical_body text, add column body_sha256 text, add column proposal_hash text;
-- format checks (RESEARCH Pitfall 7/Open Q5: nullable + check, a NULL legacy hash never matches)
grant insert (canonical_body, body_sha256, proposal_hash) on proposal to olive_app;
```
Do not touch the column UPDATE grant (state only, 0002). Update the same-task expectations: `MigrationRunnerTests.cs` line 27 (`["0001","0002","0003"]`, add 0004 and a checksum/filename assertion modeled on lines 35-37) and `PrivilegeMatrixTests.cs` line 10 insert-column list.

---

### `src/Olive.Persistence/ProposalSql.cs` and `PostgresProposalStore.cs` and `IProposalStore.cs` (CRUD)

**Analog:** themselves.

**Insert SQL pattern** (ProposalSql.cs lines 6-9): extend the column list and values with `canonical_body, body_sha256, proposal_hash` (`@canonical_body, @body_sha256, @proposal_hash`), keep `on conflict on constraint proposal_run_unique do nothing returning proposal_id`.

**Lookup SQL pattern** (Get, lines 16-18) keeps the tenant predicate:
```csharp
public const string FindByRun =
    "select proposal_id, state, body_sha256 from proposal where tenant_id = @tenant_id and agent_id = @agent_id and run_id = @run_id";
```

**Store pattern** (PostgresProposalStore.cs lines 32-77): `uow.ExecuteAsync<T>(tenantId, async (conn, tx) => { ... conn.ExecuteScalarAsync<..>(new CommandDefinition(Sql, new { snake_case = ... }, tx, cancellationToken: ct)) ...}, ct)`. Add `FindByRunAsync` the same way, and add `string CanonicalBody, string BodySha256, string ProposalHash` to `NewProposal` (IProposalStore.cs line 15). Row class uses `MatchNamesWithUnderscores` (line 9).

---

### `src/Olive.Contracts/ProblemReasons.cs` and `ProblemResults.cs`

**Analog:** themselves. Add constants `IdempotencyConflict`, `SchemaViolation`, `CatalogVersionNotFound`, `NotInCatalog` (interim); delete `DuplicateRun` (line 11) and its title (ProblemResults.cs line 33). Add titles in the static `Title` switch (lines 25-36). For `schema_violation`, add `pointer` and `keyword` via `problem.Extensions[...]` like `problem.Extensions["reason"] = reason;` (line 19); static strings only, cap pointer length, never the value. Update Phase 1 tests referencing `DuplicateRun` (`API_01_duplicate_run_gives_409...`).

---

### `src/Olive.Host/Program.cs` / `StartupGuards.cs` (startup fail-closed)

**Analog:** `Program.cs` line 34: `builder.Services.AddSingleton<IAgentAllowlist>(_ => AgentAllowlist.LoadEmbedded());` plus `StartupGuardException` for manifest/disk mismatch (RESEARCH Pattern 5). Register the loaded catalog singleton the same way, and also force eager load in the startup guard so failure occurs before traffic. Add `app.MapCatalogEndpoints()` next to `MapProposalEndpoints`.

---

### `contracts/**` moves (contract)

**Analogs:** `contracts/schemas/*.schema.json`, `contracts/examples/*.{valid,invalid}.json` (use `git mv` to preserve history). Target: `contracts/proposal/v1/`, `contracts/catalog/v1/`, queue-item and execution-record versioned placeholder folders (label as placeholders). New `proposal-request.schema.json` (7 fields, `additionalProperties:false`, no null types, required: run_id, action_type, party_ref, parameters, justification, trace_ref per RESEARCH recommendation pending user confirmation), `proposal.schema.json`, submit-response schema; each with valid and invalid examples. Do not touch `contracts/vendored/`.

### `contracts/jcs/v1/` (fixtures)

**Analog:** `contracts/introspection/` (README.md with provenance and "deliberate divergence" section, `index.json` mapping, `introspection-response.schema.json` validating the fixtures). Mirror: `README.md` (Apache-2.0 cyberphone attribution), `vectors.json` plus `vectors.schema.json`, `<name>.input.json` and `<name>.canonical.json` pairs. Add `contracts/jcs/v1/* -text` to `.gitattributes`. Use the two-verdict manifest (`canonicalize` vs `gate`) from RESEARCH Pitfall 2.

---

### Tests

**Fixture/unit analog:** `tests/Olive.Tests/IntrospectionFixtureTests.cs`
```csharp
private static string Dir => Path.Combine(TestPaths.Root, "contracts", "introspection");
[Fact] [Trait("Category", "Unit")]
public void IDN_01_all_introspection_fixtures_validate_against_schema() { ... JsonSchemaLite.Validate(Schema, Load(file)) ... }
```
Swap `JsonSchemaLite` for the real validator (delete `tests/Olive.Tests/JsonSchemaLite.cs` once unused). Name new tests `API_0x_...`, `Category=Unit` for JCS/gate/catalog vetting.

**Integration analog:** `tests/Olive.Tests/ApiProposalTests.cs` lines 10-60: `[Trait("Category","Integration")]`, ctor `(PostgresFixture fixture)`, `await using var db = await fixture.CreateDatabaseAsync(); await using var factory = new OliveApiFactory(db); using var alpha = factory.CreateKeyClient(AlphaKey);`, `internal const string FullBody` (line 18, reuse for same-body replay), helpers `Json(...)`, `CountRowsAsync(db, tenant)` (no-new-row assertions), `ReaderScalarAsync` (read stored `canonical_body` and recompute SHA-256). Log assertions use `factory.Logs` / `InMemoryLogCapture`. For the race test use `Task.WhenAll` over N posts of the same body and assert one 201, rest 200, and `CountRowsAsync == 1`. Phase 1 partial-body success tests (about 37 literals) must be updated if the envelope requires all six fields.

**Lone surrogate test caution:** build with escaped backslash text, not a literal U+D800 (RESEARCH Pitfall 9).

## Shared Patterns

### Problem responses
**Source:** `src/Olive.Host/Problems/ProblemResults.cs` lines 7-22, 46-48
**Apply to:** every non-2xx in ProposalEndpoints and CatalogEndpoints
```csharp
return ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.IdempotencyConflict);
```
Static titles only; extension members carry no request values.

### Tenant predicate plus RLS
**Source:** `src/Olive.Persistence/ProposalSql.cs` (comment line 3, every statement has `tenant_id = @tenant_id`) and `TenantUnitOfWork.ExecuteAsync(tenantId, ...)`
**Apply to:** `FindByRun`, any new SQL.

### Fail closed at startup
**Source:** `AgentAllowlist.cs` throws `InvalidOperationException(Invalid)` with a static message; Host wraps with `StartupGuardException`.
**Apply to:** catalog loader, D-02 vetting, manifest/disk mismatch.

### No outbound network
**Source:** `BannedSymbols.txt`, `ArchitectureTests` FND-02 banned API tests.
**Apply to:** schema validator (no `$ref` fetch hook; vetting rejects remote refs).

### Logging hygiene
**Source:** ProposalEndpoints.cs line 90 (static template, SQL state only).
**Apply to:** 409 conflict log (ids and truncated hashes only).

### Style
`.editorconfig`, file-scoped namespaces, `sealed`, 4-space indent, `public static class XxxEndpoints` with `MapXxxEndpoints` extension, time via `TimeProvider`.

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/Olive.Catalog/SchemaValidator.cs` | service | transform | No schema-validation library in the codebase; only the test-only `JsonSchemaLite.cs`. Use RESEARCH Pattern 4 (`EvaluationOptions { RequireFormatValidation = true }`, first-error pointer and keyword). |
| `src/Olive.Domain/Jcs/Jcs.cs` | utility | transform | No canonicalizer exists. Use RESEARCH Pattern 2 and the RFC 8785 and cyberphone vectors; lossy-number rule per Pattern 1 (assumption A1). |
| YAML to JSON converter (`YamlStream` to `JsonNode`) | utility | transform | `AgentAllowlist` uses typed deserialization only; RESEARCH Pattern 5 documents why it cannot be copied as is. |

## Open Items for the Planner (from RESEARCH)

- JsonSchema.Net 8.0.5 (MIT) versus 9.x (OSMF EULA): needs a user decision.
- Envelope required-field set (recommend all six except `nono_session_id`) forces a test-fixture update task.
- Cyberphone `values.json` conflicts with the lossy-number gate, so use the two-verdict vector manifest.
- 0004 columns nullable plus check versus NOT NULL.

## Metadata

**Analog search scope:** `src/*`, `sql/migrations`, `contracts/`, `catalog/`, `tests/Olive.Tests`
**Files scanned:** about 90 (tracked tree listing; 12 read in full or in part)
**Pattern extraction date:** 2026-10-22
