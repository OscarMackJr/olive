# Phase 1: Foundation and Walking Skeleton - Pattern Map

**Mapped:** 2026-10-21
**Files analyzed:** 34 new/modified/deleted file groups
**Analogs found:** 9 / 34 (the repo is a one-file scaffold, so most files are greenfield; D-01 and D-03 retire the main analogs)

Existing code surveyed (all of it): `src/Olive/Domain.cs` (129 lines), `src/Olive/Olive.csproj`, `tests/Olive.Tests/RequirementsTests.cs` (29 lines), `tests/Olive.Tests/Olive.Tests.csproj`, `db/migrations/V001__action_broker.sql` (104 lines), `Directory.Build.props`, `Directory.Build.targets`, `global.json`, `.gitignore`, `contracts/`, `catalog/`.

Important: Domain.cs, RequirementsTests.cs and V001 are being deleted (D-01, D-02, D-03). They are analogs for **style and naming only**, and in two places (V001 RLS function, `ListWithDatabaseRls`) for what NOT to copy.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `.gitattributes` | config | n/a | none (RESEARCH Pitfall 4 has content) | no analog |
| `Directory.Packages.props` (CPM) | config | n/a | `Directory.Build.props` | partial (same root-props role) |
| `Directory.Build.props` / `.targets` (modify) | config | n/a | themselves | exact (extend) |
| `BannedSymbols.txt`, `.editorconfig` | config | n/a | none | no analog |
| `Olive.sln` (modify) | config | n/a | itself | exact (extend) |
| `src/Olive.Domain/*.csproj` + `AssemblyMarker.cs` | model (empty/minimal) | n/a | `src/Olive/Olive.csproj` | exact (csproj shape) |
| `src/Olive.Contracts`, `.Application`, `.Catalog`, `.Executor.Simulated`, `.Integrations.Hometown`, `.Integrations.Entra` | library csproj | n/a | `src/Olive/Olive.csproj` | exact (csproj shape) |
| `src/Olive.Application/` ports (`IKeyIntrospector`, `INonProductionOnly`, `ITenantUnitOfWork`) | service (port) | request-response | `ISimulatedActionExecutor` in Domain.cs L88 | role-match |
| `src/Olive.Domain/SessionKey.cs`, `ServiceToken.cs` | model (secret) | transform | none (inverse of `sealed record` types in Domain.cs L14-19) | no analog |
| `src/Olive.Domain` proposal state constants | model | n/a | `Outcomes` class, Domain.cs L8-12 | exact |
| `src/Olive.Persistence/TenantUnitOfWork.cs` + proposal repository | service | CRUD | none; `ActionBroker.Get` (L128) for the tenant-predicate/404 semantics | partial |
| `src/Olive.Persistence/PostgresUrl.cs` | utility | transform | none | no analog |
| `src/Olive.Persistence` migration runner + embedded SQL list | service | batch / file-I/O | none | no analog |
| `src/Olive.Migrator/Program.cs` | entrypoint | batch | none | no analog |
| `sql/bootstrap/001_roles.sql` | migration (cluster) | batch | none | no analog |
| `sql/migrations/0001_init.sql` | migration | CRUD | `db/migrations/V001__action_broker.sql` | role-match (replaces it) |
| `src/Olive.Integrations.Popeye` stub + `contracts/introspection/*.json` | service + fixtures | request-response | `Catalog.Load` file-driven read (L27-32); `contracts/examples/*.valid/invalid.json` for fixture pairing | partial |
| `config/agents.yaml` + loader in `Olive.Catalog` | config + utility | file-I/O | `catalog/crm.case_note@1.yaml` + `Catalog.Load` | role-match |
| `src/Olive.Host/Program.cs`, auth handler, endpoints, `.env` source, startup guards | controller / middleware | request-response | none | no analog |
| `.env.example` | config | n/a | `.gitignore` entry for `.env` | partial |
| `tests/Olive.Tests/Olive.Tests.csproj` (rewrite) | config | n/a | itself | exact (extend) |
| `tests/Olive.Tests/*Tests.cs` (REQ-named tests) | test | n/a | `RequirementsTests.cs` | exact (naming, style) |
| `tests/Olive.Tests/PostgresFixture.cs`, ablation DB helper, `WebApplicationFactory` builders, log capture | test fixture | file-I/O / CRUD | none; `Root` path helper in RequirementsTests L10 | partial |
| `.github/workflows/ci.yml` | config | batch | none | no analog |
| Deletions: `src/Olive/` (Domain.cs, csproj), `db/`, `RequirementsTests.cs` | n/a | n/a | n/a | n/a |

## Pattern Assignments

### Test naming and class style (all `tests/Olive.Tests/*Tests.cs`) (test)

**Analog:** `tests/Olive.Tests/RequirementsTests.cs`

**Naming convention to keep (D-02), lines 18-28:** `[Fact] public void REQ_ACT_02_policy_refusal_happens_before_queueing()`: requirement ID prefix, snake_case, behavior phrase. Phase 1 names: `FND_01_...`, `DB_06_...`, `API_01_...`, `IDN_02_...` (RESEARCH Validation Architecture map uses `FullyQualifiedName~FND_01` filters, so the prefix is load-bearing).

**Class header / constants (lines 1-10):**
```csharp
using System.Text.Json;
namespace Olive.Tests;   // file-scoped namespace, sealed class

public sealed class RequirementsTests
{
    private static readonly Guid Tenant = Guid.Parse("5325ffe9-5727-4d88-9832-aebabb8d0538");
    private static readonly Guid OtherTenant = Guid.Parse("f92c9dba-50f1-4cad-9690-5d79f8ded335");
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
```
Reuse: the two tenant GUIDs (these are the vendored-vocabulary TENANT_ALPHA / TENANT_BETA; RESEARCH Wave 0 says to seed the same two), and the `Root` repo-root helper (needed by FND-03 vocabulary-hash and `.gitattributes` tests, and by fixture tests reading `sql/` and `contracts/`). Caution: `../../../../../` assumes `bin/<cfg>/net10.0/` depth from `tests/Olive.Tests`; keep that depth or re-verify when the test project moves.

**Contract-file test pattern (line 24):** REQ_QUE_01 loads `contracts/examples/queue-item.invalid.json` and the schema via `JsonDocument.Parse(File.ReadAllText(...))`. Same approach for the `contracts/introspection/*` fixture schema check (active:false carries nothing else).

**Style to improve on:** the scaffold compacts bodies onto one line with `;`. CLAUDE.md says "prefer clarity for new code". Use multi-line bodies; keep `[Fact]` inline.

**Test csproj (rewrite), current shape lines 1-9:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable><UseAppHost>true</UseAppHost></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Olive/Olive.csproj" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.0" />
    <PackageReference Include="xunit.v3" Version="3.2.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
  </ItemGroup>
</Project>
```
Changes: drop `Version=` attributes (CPM), xunit.v3 to 3.2.2, replace the single ProjectReference with one per new project, add Testcontainers.PostgreSql 4.16.0, Mvc.Testing, NetArchTest.Rules. Keep `UseAppHost=true` (Directory.Build.targets only forces false when the project did not set `true`). OutputType Exe is required by xunit.v3 (the scaffold relies on the xunit.v3 package implying it; keep that).

---

### `src/Olive.*/Olive.*.csproj` (library projects, FND-01)

**Analog:** `src/Olive/Olive.csproj` (entire file, 4 lines)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
```
Copy for Domain, Contracts, Application, Executor.Simulated, Integrations.*. Move `TargetFramework`/`ImplicitUsings`/`Nullable` into `Directory.Build.props` and leave csproj with only references. Zero package references for Domain/Application/Executor (RESEARCH reference graph; FND-02 package allowlist test reads `packages.lock.json`). `Olive.Host` uses `Microsoft.NET.Sdk.Web`. Add `AssemblyMarker` classes (used by RESEARCH architecture-test sketch `typeof(Olive.Domain.AssemblyMarker).Assembly`).

### `Directory.Build.props` / `Directory.Build.targets` / `global.json` (config)

**Analog:** the files themselves.

`Directory.Build.props` (lines 1-5):
```xml
<Project>
  <PropertyGroup>
    <UseAppHost>false</UseAppHost>
  </PropertyGroup>
</Project>
```
`Directory.Build.targets` (lines 1-5):
```xml
<Project>
  <PropertyGroup>
    <UseAppHost Condition="'$(UseAppHost)' != 'true'">false</UseAppHost>
  </PropertyGroup>
</Project>
```
Extend, keep the `UseAppHost` behavior intact: add `ManagePackageVersionsCentrally`, `RestorePackagesWithLockFile=true`, `TargetFramework`/`Nullable`/`ImplicitUsings`, BannedApiAnalyzers `PackageReference` + `AdditionalFiles BannedSymbols.txt`, RS0030 error. `global.json` stays: `{ "sdk": { "version": "10.0.204", "rollForward": "latestFeature" } }` (CI uses `global-json-file`).

---

### Proposal state constants (`src/Olive.Domain`) (model)

**Analog:** `Outcomes` in `src/Olive/Domain.cs` lines 8-12
```csharp
public static class Outcomes
{
    public const string Proposed = "proposed", RefusedPolicy = "refused_policy", AwaitingApproval = "awaiting_approval";
    public const string Approved = "approved", Rejected = "rejected", Executed = "executed", ExecutionFailed = "execution_failed", Expired = "expired";
}
```
Copy the snake_case constants; they must equal the `check (state in (...))` list in 0001 (CLAUDE.md: "match DB check constraints"). Phase 1 only writes `Proposed` (D-06). Re-home under `namespace Olive.Domain;` (file-scoped namespace convention).

### Immutable domain data (`src/Olive.Domain`, introspection result in Application) (model)

**Analog:** one-line sealed records, Domain.cs L14-19:
```csharp
public sealed record Principal(string Type, string Id, Principal? OnBehalfOf = null, IReadOnlySet<string>? Grants = null);
```
Use for `IntrospectionResult.Active(KeyId, PrincipalType, AgentId, OnBehalfOf, Purpose, TenantId, DataClassCeiling, ExpiresAt)` (RESEARCH Pattern 8: a record is fine because it holds no secret). **Do NOT use a record for `SessionKey`/`ServiceToken`** (see No Analog).

---

### Ports (`src/Olive.Application`) (service port, request-response)

**Analog:** `ISimulatedActionExecutor`, Domain.cs L88
```csharp
public interface ISimulatedActionExecutor { ExecutionRecord Execute(Proposal proposal, string approver); }
```
Pattern: one-line interface per port, narrow signature. New ports: `IKeyIntrospector.IntrospectAsync(SessionKey, CancellationToken)`, `ITenantUnitOfWork`, marker `INonProductionOnly`. Time convention (Domain.cs L106 `DateTimeOffset now` parameter): services receive time as a parameter or `TimeProvider`; RESEARCH bans `DateTime.UtcNow`/`DateTimeOffset.UtcNow` via BannedSymbols and says inject `TimeProvider`. Note the scaffold violates this in `SimulatedActionExecutor` L96; do not copy it.

---

### `src/Olive.Persistence` proposal repository / `TenantUnitOfWork` (service, CRUD)

**No true analog** (no database code exists). Pattern source: RESEARCH Pattern 1 (lines 212-222) and Pattern 2.

**Semantics to carry over from the scaffold** `ActionBroker.Get`, Domain.cs L128: the application-level tenant predicate and the "cross-tenant == missing" rule (D-11):
```csharp
public Proposal Get(Guid tenantId, Guid proposalId) => _proposals.TryGetValue(proposalId, out var proposal) && proposal.TenantId == tenantId ? proposal : throw new KeyNotFoundException();
```
Port as SQL `select ... from proposal where tenant_id = @tenant_id and proposal_id = @proposal_id`, zero rows -> 404. The same indistinguishability appears in `Update` L85 (`FindIndex(x => x.TenantId == tenantId && x.ItemId == itemId)` then `KeyNotFoundException`). Every SQL constant must contain `tenant_id = @tenant_id` (or `@tenant_id` in INSERT column list); a lint test enforces it (DB-05).

**Core pattern to write (RESEARCH Pattern 1):**
```csharp
await using var conn = await dataSource.OpenConnectionAsync(ct);
await using var tx = await conn.BeginTransactionAsync(ct);
await conn.ExecuteAsync("select set_config('olive.tenant_id', @t, true)", new { t = tenantId.ToString("D") }, tx);
// ... repository calls using (conn, tx) ...
await tx.CommitAsync(ct);
```
Insert: `INSERT ... ON CONFLICT (tenant_id, agent_id, run_id) DO NOTHING RETURNING` (zero rows -> 409), then `INSERT proposal_event('submitted')`. jsonb: pass text and cast `@parameters::jsonb`, read `parameters::text` (Pitfall 5). UTC-only timestamps.

**Error handling:** scaffold convention is exceptions with short messages (`InvalidOperationException`, `KeyNotFoundException`) for invariants and returned values for expected refusals. In Host, map `PostgresException` to a generic problem; never `Include Error Detail`.

---

### `sql/migrations/0001_init.sql` (migration, CRUD)

**Analog:** `db/migrations/V001__action_broker.sql` (being replaced; copy structure, not content)

**Copy: table/constraint style (lines 4-18):** lowercase SQL, composite PK with `tenant_id` first, state `check (state in (...))`:
```sql
create table if not exists action_broker.proposals (
    tenant_id uuid not null,
    proposal_id uuid not null,
    ...
    primary key (tenant_id, proposal_id),
    check (state in ('proposed', 'refused_policy', 'awaiting_approval', 'approved', 'rejected', 'executed', 'execution_failed', 'expired'))
);
```
**Copy: composite FKs including tenant_id (line 36):** `foreign key (tenant_id, proposal_id) references action_broker.proposals (tenant_id, proposal_id)` (use for `proposal_event`).

**Copy: ENABLE + FORCE pairs (lines 87-88):**
```sql
alter table action_broker.proposals enable row level security;
alter table action_broker.proposals force row level security;
```
**Copy: USING + WITH CHECK policy form (line 98), but change the predicate.**

**DO NOT copy (defect, line 83-85):**
```sql
create or replace function action_broker.current_tenant_id() returns uuid language sql stable as $$
    select nullif(current_setting('olive.tenant_id', true), '')::uuid
$$;
```
This uses `missing_ok = true` and `nullif` so an unset tenant silently yields NULL (fail-quiet). D-carry-forward and DB-03 require `current_setting('olive.tenant_id')` without `missing_ok` directly in the policy (RESEARCH Pattern 2 lines 254-255) so unset raises 22P02/42704.

**Also do not copy:** `create schema action_broker`/`integration_outbox` (D-03 uses public schema), `create table if not exists` (runner owns idempotency via `schema_migrations`), `queue_items`/`executions`/`idempotency_records`/`outbox_facts` tables (later phases), the `-- SERIALIZABLE` trailing comment as a substitute for tests.

**New in 0001:** `unique (tenant_id, agent_id, run_id)` (D-10), `key_expires_at` column (D-08), `nono_session_id` nullable, grants block (`revoke all ... from public`, `grant select, insert, update on proposal to olive_app`, `grant select, insert on proposal_event to olive_app`, `grant select ... to olive_reader`). Full sketch: RESEARCH Pattern 2. Resolve RESEARCH Open Questions 2-3 (nullable columns; optional `body_sha256`) in the plan.

---

### `config/agents.yaml` + allowlist loader (config / file-I/O)

**Analog:** `catalog/crm.case_note@1.yaml` and `Catalog.Load`/`Parse` (Domain.cs L27-43)

Source-controlled YAML read at startup from a directory; fail with short invariant messages:
```csharp
public static Catalog Load(string catalogDirectory, string vocabularyPath) { ... File.ReadAllLines(path) ... }
if (!Regex.IsMatch(id, "^[a-z0-9_.]+@[0-9]+$")) throw new InvalidOperationException("Invalid action_id.");
```
Keep the idea (file path passed in, validated, `InvalidOperationException` on bad content, no runtime mutation, change-managed like the catalog). **Do not copy the hand-rolled line parser** (D-05 deletes it); RESEARCH uses YamlDotNet 18.1.0, confined to `Olive.Catalog` or Host, never Domain/Application. Fail closed: missing file or empty allowlist should refuse to start. Planner: read `catalog/crm.case_note@1.yaml` before writing the YAML for consistent key naming (snake_case keys per CLAUDE.md).

### Vendored vocabulary SHA-256 guard (FND-03)

**Analog:** the scaffold read `contracts/vendored/vocabulary.yaml` as lines (Domain.cs L29). The Phase 1 guard reads **raw bytes** instead: `SHA256.HashData(File.ReadAllBytes(path))` (Domain.cs already imports `System.Security.Cryptography`, L1, but never uses it). Expected value `60a44f00db0daafcad3a4e11fe8fcbb2821e1ca7879302bae413b212a0c1e8e7` (RESEARCH CI sketch; also in `contracts/vendored/VENDORED.md`). The test (FND_03) asserts constant == VENDORED.md == on-disk file. Copy as `Content` to output, not embedded.

---

### `contracts/introspection/*.json` fixtures + Popeye stub (service, request-response)

**Analog (fixture pairing):** `contracts/examples/` convention, files `<name>.valid.json` / `<name>.invalid.json` (CLAUDE.md: add both when adding a schema), tested via `JsonDocument.Parse(File.ReadAllText(...))` as in RequirementsTests L24. Add `contracts/schemas/introspection-response.schema.json` (draft 2020-12, `oneOf` of `{active:false}` with `additionalProperties:false` or full active shape; `key_id` lowercase UUID per POPEYE_v0.4.3 §2) plus fixtures: liam-alpha, liam-beta, user, application, non-allowlisted agent, inactive. JSON keys snake_case. Note D-04 leaves `contracts/schemas` and `contracts/examples` untouched; add the new schema under `contracts/introspection/` to stay consistent with D-13.

**Analog (stub shape):** none for HTTP-ish stubs. Pattern 8 in RESEARCH. The stub implements `INonProductionOnly`; unknown keys return inactive; malformed active -> fail closed.

---

### Secret types: `SessionKey`, `ServiceToken` (model, transform)

**No analog.** The scaffold uses sealed records everywhere (L14-19), and that is exactly what the secret types must NOT do (`record.ToString()` prints members). Retain only "sealed by default" from CLAUDE.md. Pattern source: RESEARCH Pattern 7 (lines 304-314): sealed class, `ToString() => "[REDACTED]"`, `IFormattable`, `[DebuggerDisplay]`, redacting `JsonConverter`, `ExposeSecret()` callable only from Integrations.Popeye (arch test). Sentinel test list is in RESEARCH Pattern 7.

---

### `src/Olive.Host` (Program.cs, auth handler, endpoints, guards, `.env` source) (controller / middleware, request-response)

**No analog** (no web host, DI or HTTP in the repo). Pattern sources in RESEARCH:
- Pipeline and ordering: System Architecture Diagram (L152-186); Builder created with `EnvironmentName = Production`, `UseExceptionHandler` + `AddProblemDetails` (Anti-Patterns).
- Auth handler / port / 401 `key_inactive`: Pattern 8.
- Endpoint outline and stable reason codes: "Endpoint outline" (L436-442): 201 + `Location`, 400 `invalid_request`, 401 `key_inactive`, 403 `principal_not_permitted` / `agent_not_allowlisted`, 404 `proposal_not_found`, 409 `duplicate_run`, 503 `introspection_unavailable`.
- Startup guards 1-6: Pattern 5. `.env` source: Pattern 6. Body handling: raw `JsonDocument` with `MaxDepth=64`, Kestrel `MaxRequestBodySize` ~1 MiB (Pitfall 10).
- Header name isolated in one class (D-12).

**Conventions to apply from the scaffold:** reference requirement IDs in messages/comments (`"REQ-ACT-06: action types are source-controlled."`, Domain.cs L26); policy/guard detail text must be static and never contain parameter or secret values (CLAUDE.md; test `policy_decision_detail_never_contains_parameter_value`, RequirementsTests L28, is the precedent for the guard-message-no-secret test); snake_case JSON properties (`proposal_id`, `state`); 404 for cross-tenant (`KeyNotFoundException` semantics).

---

### Migrator (`src/Olive.Migrator`) and runner (batch)

**No analog.** Pattern 4 (RESEARCH L283-284). Only reader of `OLIVE_MIGRATOR_URL`. Embed `sql/migrations/*.sql` into Olive.Persistence. SHA-256 over raw bytes (relies on `.gitattributes` LF). Advisory lock, `SET ROLE olive_owner`, per-file transaction.

### `sql/bootstrap/001_roles.sql`

**No analog.** RESEARCH Pattern 3 (L263-281). Idempotent `do $$ ... if not exists (select 1 from pg_roles ...)`; re-assert `NOSUPERUSER NOBYPASSRLS` each run.

### `PostgresUrl` converter

**No analog.** Pitfall 6 (L387-388). Unit test with `@`/`%` in password, missing port, bad scheme; error messages must not echo the URL (matches the static-message convention).

---

### `tests/Olive.Tests/PostgresFixture.cs` and helpers (test fixture)

**No analog** for Testcontainers/xUnit v3 assembly fixtures. Pattern source: RESEARCH "Wave 0 Gaps" (L534-540) and Pitfalls 1, 2, 9; Standard Stack row for `new PostgreSqlBuilder("postgres:17-alpine")`. Hard rules: expose only `olive_app` / `olive_reader` strings; assert `rolsuper=false`, `rolbypassrls=false`, not owner; seed the same two tenants as RequirementsTests L8-9; negative tests `Max Pool Size=1`; guard against `localhost:5432`.

### `.github/workflows/ci.yml`

**No analog** (no `.github/`). RESEARCH CI sketch L417-434. Includes the vocabulary `sha256sum -c` step, `dotnet restore --locked-mode`, and a grep on `dotnet list package --vulnerable` output.

### `.env.example` / `.gitignore`

**Analog:** `.gitignore` already has `.env`, `appsettings.*.json`, `*.jsonl`, `bin/`, `obj/`, `TestResults/`. Do not add an `.env.example` ignore rule. Variables follow Decision §15.4 (`OLIVE_POSTGRES_URL`, `OLIVE_POSTGRES_HOST/PORT/DB`, `OLIVE_ENVIRONMENT`; `OLIVE_MIGRATOR_URL` documented as migrator-only and commented out). Note the `.env.example` must not contain the migrator URL as an active value because the service refuses to start if it is set (D-19).

---

## Shared Patterns

### Tenant isolation, two layers (all Persistence SQL and tests)
**Source:** `ActionBroker.Get` (Domain.cs L128) for the app predicate; V001 L87-102 for ENABLE+FORCE+policy structure (with the `missing_ok` defect removed).
**Apply to:** every repository statement, 0001 and later migrations, every negative test.
Rule: keep `tenant_id = @tenant_id` in SQL even though RLS exists (AGENTS.md / CLAUDE.md anti-pattern "Dropping tenant predicates").

### Error handling by exception type vs returned value
**Source:** CLAUDE.md "Error Handling"; Domain.cs L25, L26, L81, L85, L125, L128.
**Apply to:** Application and Persistence layers.
```csharp
?? throw new InvalidOperationException("Unknown source-controlled action type.");
throw new KeyNotFoundException();               // missing and cross-tenant deliberately identical
throw new ArgumentException("A decision needs reviewer, outcome and rationale.");
```
Host translates these to problem+json (`KeyNotFoundException` -> 404, etc.). Guard failures use a `StartupGuardException` with static text naming the rule and never any value.

### Static messages, no secret/parameter echo
**Source:** `policy_decision_detail_never_contains_parameter_value` (RequirementsTests L28) and static `Detail` strings in `PolicyEngine.Evaluate` (Domain.cs L52-60).
**Apply to:** problem details, guard messages, PostgresUrl errors, auth-handler failures.

### Naming
**Source:** CLAUDE.md Naming Patterns. PascalCase types, `_camelCase` private fields, snake_case state/reason strings and JSON properties, `UPPER-000` rule IDs, migrations `NNNN_snake_case.sql` (new; the `V###__` form is retired with `db/`), file-scoped `namespace`, sealed by default, 4-space indent, Allman braces, no XML doc comments, sparse boundary comments.

### Requirement traceability
**Source:** RequirementsTests naming; CLAUDE.md "Requirements Traceability".
**Apply to:** every new test (`FND_01_`, `DB_03_`, `API_01_`, `IDN_01_` prefixes) and requirement IDs inside guard/exception messages where relevant.

### Banned APIs
**Source:** test `REQ_ACT_04_no_external_write_connector_exists` (RequirementsTests L21) asserts source lacks "HttpClient".
```csharp
var source = File.ReadAllText(Path.Combine(Root, "src", "Olive", "Domain.cs")); Assert.DoesNotContain("HttpClient", source);
```
This test hard-codes `src/Olive/Domain.cs`, which is deleted. Replace with FND-02: BannedSymbols.txt (compile-time) plus NetArchTest `NotHaveDependencyOn("System.Net.Http")` on Domain/Application/Executor. Keep the `REQ_ACT_04_` test name (AGENTS.md references it) pointing at the new check, or record its retirement in the plan.

## No Analog Found

| File | Role | Data Flow | Reason / Pattern Source |
|---|---|---|---|
| `.gitattributes` | config | n/a | RESEARCH Pitfall 4 (L370-382). Do first |
| `src/Olive.Host/*` (Program, auth handler, endpoints, ProblemDetails, guards, `.env` source) | controller/middleware | request-response | RESEARCH Patterns 5, 6, 8; diagram; Endpoint outline |
| `Olive.Persistence` TenantUnitOfWork, Dapper repo | service | CRUD | RESEARCH Pattern 1, 2, Pitfall 5 |
| Migration runner + `Olive.Migrator` | service/entrypoint | batch | RESEARCH Pattern 4 |
| `sql/bootstrap/001_roles.sql` | migration | batch | RESEARCH Pattern 3 |
| `PostgresUrl` | utility | transform | RESEARCH Pitfall 6 |
| `SessionKey`, `ServiceToken` | model | transform | RESEARCH Pattern 7 |
| Introspection stub (HTTP-shaped port) | service | request-response | RESEARCH Pattern 8, POPEYE_v0.4.2 §2.1 |
| Testcontainers `PostgresFixture`, ablation DB, `WebApplicationFactory` | test fixture | CRUD | RESEARCH Wave 0 Gaps, Pitfalls 1, 2, 9 |
| `Directory.Packages.props`, `BannedSymbols.txt`, `.editorconfig` | config | n/a | RESEARCH Standard Stack, Pitfall 8 |
| `.github/workflows/ci.yml` | config | batch | RESEARCH CI sketch (D-18) |
| `.env.example` | config | n/a | Decision §15.4, D-19 |

## Metadata

**Analog search scope:** whole repo (`src/`, `tests/`, `db/`, `contracts/`, `catalog/`, root props/config files). `catalog/crm.case_note@1.yaml` and `contracts/schemas/*.json` were listed but not opened (untouched in Phase 1 per D-04/D-05; the planner should open the catalog YAML when writing `config/agents.yaml`).
**Files scanned:** 12 read in full or listed
**Pattern extraction date:** 2026-10-21
