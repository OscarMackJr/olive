# Stack Research

**Domain:** Governed AI action broker and human review queue (Olive Phase 1, simulation-only), C# / .NET 10
**Researched:** 2026-10-21
**Confidence:** MEDIUM-HIGH overall. Versions were checked against the live NuGet API on 2026-10-21. Library behaviour was checked against official docs or source where noted.

> Scope: this file does not revisit decisions that `specs/DECISION_olive-poc2-phase1_v0.5.md` already ratifies (Postgres, forced RLS, the role model, JCS, JSON Schema 2020-12, OpenAPI 3.1, Razor Pages, Entra). It only picks the libraries that implement those decisions.

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended | Confidence |
|------------|---------|---------|-----------------|------------|
| .NET SDK / runtime | SDK 10.0.204+ (`global.json` already set, `rollForward: latestFeature`); runtime 10.0.13 (latest servicing, 2026-10-13) | Platform | Already in place. It matches bluto, and the charter (§6.2) recommends it. | HIGH |
| ASP.NET Core (`Microsoft.NET.Sdk.Web`) | 10.0.13 (shared framework, no package) | Minimal APIs for `/v1/*` and Razor Pages for the reviewer UI, in one host | Both ship in the shared framework, so they add no third-party surface and need no JS build. Razor Pages provides antiforgery and cookie auth out of the box. | HIGH |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | 10.0.13 | Reviewer sign-in (Entra ID) | Use the **plain OIDC handler, not Microsoft.Identity.Web**. Entra (`https://login.microsoftonline.com/{tenant}/v2.0`) and a dev IdP then share one code path, with only configuration differing, which is what "Entra behind an interface plus a local stub" needs. Phase 1 acquires no downstream tokens, so Identity.Web's main value (MSAL token acquisition and caching) goes unused. | MEDIUM-HIGH |
| Npgsql | 10.0.3 | PostgreSQL driver | The only serious ADO.NET provider for Postgres. Use `NpgsqlDataSource` as a singleton. | HIGH |
| Dapper | 2.1.89 (Apache-2.0) | Thin row mapping over Npgsql | Every query must be explicit SQL: `set_config('olive.tenant_id', …, true)` per transaction, a tenant predicate on every statement, `INSERT … ON CONFLICT DO NOTHING RETURNING`, and INSERT-only tables. Dapper keeps the SQL visible and reviewable and only removes mapping boilerplate. | HIGH |
| Hand-rolled migration runner (on Npgsql) | n/a (~150 LOC) | Forward-only `sql/migrations/NNNN_*.sql` plus a `schema_migrations` table | Decision §13 specifies "a small runner that records applied versions in a `schema_migrations` table" and follows hometown's convention. DbUp's journal has a different shape (see Alternatives). | HIGH |
| Corvus.Text.Json.Validator | 5.7.9 (Apache-2.0, published 2026-10-17, net10.0 target) | JSON Schema 2020-12 validation of proposal bodies and catalog parameter schemas | Full 2020-12 support. It validates raw UTF-8 bytes or a read-only element and returns a bool plus a results collector, so it has no path to mutate input. Format assertion is configurable. Apache-2.0 with no fee (compare JsonSchema.Net below). | MEDIUM (the V5 runtime validator package is new: about 13K downloads; the Corvus engine itself is mature) |
| RFC 8785 JCS: **in-house implementation** (`Olive.Canonicalization`) | n/a (~250 LOC), ported from or checked against the cyberphone reference (Apache-2.0) | `body_sha256` (§15.3) and `proposal_hash` (§17.2) | No NuGet package is mature enough to sit on the core-value hash path (see "JCS in .NET" below). The algorithm is small and fully specified, and cyberphone publishes conformance vectors, including 100M ES6 number cases. | MEDIUM-HIGH |
| YamlDotNet | 18.1.0 (MIT, 2026-06-26, net10.0 target) | Parse `catalog/*.yaml` and the vendored vocabulary | The de facto .NET YAML library (about 570M downloads). It replaces the hand-rolled line parser. | HIGH |
| xUnit v3 | **3.2.2** (stay on 3.x this milestone; 4.0.2 is current) | Test framework | xunit.v3 4.0 (2026-08-14) introduced extensibility breaking changes, and Testcontainers.XunitV3 4.16.0 still compiles against `xunit.v3.extensibility.core` 3.2.2. Moving from the scaffold's 3.2.0 to 3.2.2 is a patch bump. Revisit 4.x after Testcontainers ships a 4.x-built adapter. | MEDIUM |
| Testcontainers.PostgreSql | 4.16.0 (MIT, 2026-10-08) | Throwaway Postgres per CI run (Decision §13) | The standard approach, and the same pattern bluto uses. Use `new PostgreSqlBuilder("postgres:<major>-alpine")`; the image must be passed explicitly. | HIGH |

### Supporting Libraries

| Library | Version | Purpose | When to Use | Confidence |
|---------|---------|---------|-------------|------------|
| Npgsql.DependencyInjection | 10.0.3 | `AddNpgsqlDataSource(...)` registration | In the API host. Register **two** data sources only where needed: the app uses `OLIVE_POSTGRES_URL`, and the migrator uses `OLIVE_MIGRATOR_URL` in the migrate command only. | HIGH |
| Microsoft.OpenApi + Microsoft.OpenApi.YamlReader | 3.11.0 (2026-10-19) | Parse the hand-authored `contracts/proposal/v1/openapi.yaml` in tests | In a contract test that loads the document, fails on diagnostics, and asserts that every mapped route, method and status code exists in the document, and the reverse. The 3.x line supports OAS 3.1 (and 3.2). | MEDIUM |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.13 | `WebApplicationFactory<Program>` in-process API and UI tests | All endpoint tests: TC-ACT-*, TC-QUE-*, §16.8 and §17.4. | HIGH |
| WireMock.Net | 2.19.0 (Apache-2.0, 2026-10-07) | Stand-in HTTP servers for popeye (introspection and tokens) and hometown (trace resolution) | Integration tests that run the real HTTP transport implementations of `IPopeyeIntrospectionClient` and `ITraceResolver` against recorded contract responses, and that assert on received headers (the session key only reaches the popeye mock). | HIGH |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | `FakeTimeProvider` | Expiry (8h default, catalog maximum, `expires_at` cap), token-cache lifetime and rate windows. Inject `TimeProvider` everywhere and never call `DateTime.UtcNow`. | HIGH |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 | Compile-time ban list | Ban `new HttpClient(...)`, `HttpClientHandler`, `SocketsHttpHandler`, `WebRequest`, `DateTime.UtcNow/Now`, and `JsonSerializerDefaults.Web` in the proposal path. This backs the outbound-capture proof and the no-write-credential audit. | HIGH |
| Microsoft.Extensions.Compliance.Redaction | 10.11.0 | Log redaction of tokens and session keys | Optional. Use it if logging structured objects that could carry `Authorization` values. Otherwise rely on never logging headers. | MEDIUM |
| Microsoft.NET.Test.Sdk / xunit.runner.visualstudio | 18.11.0 / 3.1.5 (keep 3.x runner with xunit.v3 3.2.2) | VSTest runner (existing scaffold) | Keep the scaffold's VSTest mode for now. Note that the .NET 10 SDK pushes toward Microsoft Testing Platform. | MEDIUM |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| Central Package Management (`Directory.Packages.props`) plus `packages.lock.json` (`RestorePackagesWithLockFile=true`, `--locked-mode` in CI) | Pinned, auditable dependency graph | Supports the credential and supply-chain audit evidence. The scaffold has no lockfile today. |
| `dotnet list package --vulnerable --include-transitive` | CI gate | Cheap, and gives evidence for the §3 review. |
| `ghcr.io/navikt/mock-oauth2-server:6.1.2` (released 2026-10-18) as a Testcontainers generic container | A real OIDC provider for one end-to-end test of the OIDC handler path | Use only for the test that proves the production OIDC wiring (discovery, code flow, `max_age`, roles claim) works against a standards-compliant IdP. The everyday dev stub is in-process (see Patterns). The image tag was not pulled to verify it (ghcr requires auth). |
| Redocly CLI (`npx @redocly/cli lint`) | Optional OpenAPI 3.1 lint in CI | It is a dev tool, not a runtime dependency. Skip it if Node in CI is unwelcome; the Microsoft.OpenApi test covers parsing. |

## Installation

```bash
# Olive.Api (Microsoft.NET.Sdk.Web)
dotnet add src/Olive.Api package Microsoft.AspNetCore.Authentication.OpenIdConnect --version 10.0.13
dotnet add src/Olive.Api package Npgsql.DependencyInjection --version 10.0.3

# Olive.Persistence
dotnet add src/Olive.Persistence package Npgsql --version 10.0.3
dotnet add src/Olive.Persistence package Dapper --version 2.1.89

# Olive.Domain / contracts / catalog
dotnet add src/Olive.Domain package Corvus.Text.Json.Validator --version 5.7.9
dotnet add src/Olive.Domain package YamlDotNet --version 18.1.0

# All src projects (analyzer)
dotnet add <proj> package Microsoft.CodeAnalysis.BannedApiAnalyzers --version 5.6.0

# Tests
dotnet add tests/Olive.Tests package xunit.v3 --version 3.2.2
dotnet add tests/Olive.Tests package Testcontainers.PostgreSql --version 4.16.0
dotnet add tests/Olive.Tests package Microsoft.AspNetCore.Mvc.Testing --version 10.0.13
dotnet add tests/Olive.Tests package WireMock.Net --version 2.19.0
dotnet add tests/Olive.Tests package Microsoft.Extensions.TimeProvider.Testing --version 10.11.0
dotnet add tests/Olive.Tests package Microsoft.OpenApi.YamlReader --version 3.11.0
```

(Use `Directory.Packages.props` with CPM instead of per-project versions.)

## Decision Details per Question

### 1. Web host, UI and OIDC

- **Minimal APIs** for `/v1/proposals` and `/v1/catalog/{version}`, and **Razor Pages** for list, claim, decide and audit, all in one `Olive.Api` host. Keep them on separate authentication schemes: the API uses a custom popeye-introspection `AuthenticationHandler` (bearer session key, introspected), and the UI uses cookie plus OIDC. Separate schemes per path enforce Decision §7's "caller classes are separate paths" at the framework level.
- **Plain `AddOpenIdConnect`**, behind `IReviewerIdentity` / `IReviewerAuthorization` interfaces:
  - For "role checked at decision time" (Decision §8), the decide POST triggers a fresh challenge with `OpenIdConnectChallengeProperties.MaxAge = TimeSpan.Zero` (or a short window). Authorization then reads a `roles` claim from an **Entra App Role** that is assigned to the `role:case_supervisor` group, not a `groups` claim. App roles avoid the groups-overage problem and need **no Microsoft Graph credential**, which keeps the credential audit at zero extra secrets. **Flag for phase research** (LOW-MEDIUM confidence): confirm with the Entra admin that an app role assignment is acceptable as "membership checked at decision time".
  - The OIDC client itself needs a client secret or certificate for the code flow. It is a sign-in credential, not write-capable, but list it in the credential audit.
- **Local dev IdP stub:** an in-process `DevReviewer` authentication scheme. It is a signed-cookie sign-in page that lets you choose a reviewer identity and roles from a fixture file, and it is registered only when `OLIVE_ENVIRONMENT != production`. It **refuses to start** in production, mirroring §16.5's fixture-resolver rule. One CI test runs the real OIDC handler against `mock-oauth2-server` so the production path is not only exercised in Entra.
- **Why not Microsoft.Identity.Web 4.16.1:** it is Entra-specific (it pulls MSAL, token cache and certificate packages), it makes the dev-IdP path a different code path, and its benefits (downstream API calls, Graph) are out of scope in Phase 1. Reconsider in Phase 2 if Graph group checks become mandatory.

### 2. PostgreSQL access: Npgsql + Dapper, not EF Core

- Use **Npgsql 10.0.3 + Dapper 2.1.89**. Every unit of work is an explicit transaction that starts with `SELECT set_config('olive.tenant_id', @tenant, true)`. `SET LOCAL` cannot take bind parameters, and `set_config(..., is_local => true)` is the parameterizable equivalent with identical transaction scope. Never use session-level `SET`: pooled connections would leak tenants.
- Implement idempotency (§15.2) as `INSERT INTO proposal … ON CONFLICT (tenant_id, agent_id, run_id) DO NOTHING RETURNING proposal_id`. Zero rows returned means the caller lost the race, so it re-reads and compares `body_sha256`. As a backstop, catch `PostgresException` with `SqlState == PostgresErrorCodes.UniqueViolation` ("23505") anywhere else.
- **Why not EF Core (Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3):**
  - Change tracking issues UPDATEs implicitly, which conflicts with INSERT-only grants on `execution_ledger` and `proposal_event`.
  - Injecting `set_config` per transaction needs interceptors.
  - EF's migrations conflict with the ratified plain-SQL migrations.
  - "Tenant predicate in every query" is hidden in global query filters, which the §13 negative test then has to disable through EF APIs rather than by visibly removing a `WHERE`.
- **Connection URL:** `OLIVE_POSTGRES_URL` is a `postgresql://` URI (§15.4). Npgsql connection strings are `key=value`. URI support was **not verified** for Npgsql 10 (LOW confidence), so write a small tested URI→`NpgsqlConnectionStringBuilder` converter rather than assume it.

### 3. Migration runner: hand-rolled

- About 150 LOC on Npgsql:
  - take `pg_advisory_lock(<const>)`;
  - `CREATE TABLE IF NOT EXISTS schema_migrations(version text primary key, filename text not null, sha256 text not null, applied_at timestamptz not null default now())`;
  - apply each unapplied `sql/migrations/NNNN_*.sql` in its own transaction, in lexical order;
  - **fail if an already-applied file's sha256 changed** (this enforces forward-only);
  - run as `olive_migrator` only, from `OLIVE_MIGRATOR_URL`.
- **Why not DbUp (dbup-postgresql 7.0.1, MIT):**
  - It journals to its own `schemaversions(schemaversionsid, scriptname, applied)` shape, not the ratified `schema_migrations`.
  - It has no checksum drift detection.
  - Its value (multi-database support, script providers) is unused here.
  - It is a reasonable fallback if the team prefers a library, provided it is configured with `JournalToPostgresTable("public","schema_migrations")` and the column mismatch is accepted.
- **Why not Evolve 3.2.0 / FluentMigrator 8.0.1 / grate 2.1.6:** Evolve is effectively in maintenance (its last stable release is old and only alphas are moving), and the other two bring their own conventions and DSLs on top of plain SQL.
- Role creation (`CREATE ROLE olive_owner/olive_migrator/olive_app/olive_reader`) is **cluster-level** and needs `CREATEROLE` or superuser. Put it in a separate bootstrap script that `nexus` (or the Testcontainers superuser) runs once, outside the runner. Migrations then run as `olive_migrator`.

### 4. Testcontainers + xUnit v3

- Use **Testcontainers.PostgreSql 4.16.0** with xUnit v3's native `[assembly: AssemblyFixture(typeof(PostgresFixture))]`, so one container per test run. **Skip `Testcontainers.XunitV3`:** the native fixture is enough, and it removes the xunit 3.x/4.x coupling described above.
- The fixture:
  1. starts `postgres:<same major as deploy-postgres-1>-alpine` (check with `docker exec deploy-postgres-1 postgres --version`; it was not running during research);
  2. runs the bootstrap script as the container superuser;
  3. runs the real migration runner as `olive_migrator`;
  4. hands tests **`olive_app` and `olive_reader` connection strings, never the superuser one.** Superusers and `BYPASSRLS` roles bypass RLS even with `FORCE`, so an RLS test running as `postgres` passes vacuously.
- Isolate tests with a fresh `tenant_id` (UUID) per test rather than truncation. That fits the RLS model, and it means INSERT-only tables never need cleanup grants. Avoid Respawn (7.0.0): it needs DELETE or TRUNCATE privileges that `olive_app` must not have.

### 5. RFC 8785 JCS in .NET: hand-roll, test against the reference vectors

What exists on NuGet (checked 2026-10-21):

| Package | Version | Downloads | Assessment |
|---|---|---|---|
| `jsoncanonicalizer` | 1.0.0 (2024-08) | ~195K | Unattributed repackaging (author field "jsoncanonicalizer", no project URL) of cyberphone's C# code plus an `es6numberserializer` dependency. Unknown provenance, so **do not use on a security hash path.** |
| `Jcs.Net` | 0.1.1 (2026-08) | ~4K | Single author, two months old, 0.x. |
| `MackySoft.Json.Canonicalization` | 0.1.0 | ~6.6K | 0.x, no project URL. |
| `Stratumn.CanonicalJson` | 0.0.8 | ~540K | Pre-dates RFC 8785 and is not JCS. |

**Recommendation:** implement `JcsCanonicalizer` in-house, ported from or checked against the Apache-2.0 reference in `github.com/cyberphone/json-canonicalization/dotnet` (keep the NOTICE attribution if code is copied). The hard parts and how to do them:

- **Numbers (ES `Number.prototype.toString`):**
  - Parse the token with `double.Parse(..., CultureInfo.InvariantCulture)`. .NET Core 3.0+ parsing is IEEE-correct.
  - Get the shortest round-trip digits from `d.ToString("R", InvariantCulture)` (shortest round-trippable since .NET Core 3.0), then re-layout per ECMA-262 Number::toString. Plain notation applies when −6 < n ≤ 21. Otherwise use `d.ddde±x`, with a lowercase `e`, an explicit `+`, and no exponent padding. `-0` becomes `0`.
  - .NET's own formatting **cannot be used directly**: `1e16.ToString()` gives `1E+16`, where ES gives `10000000000000000`.
  - Test against cyberphone's `es6testfile100m.txt.gz`: a 1M-line slice in CI, and the full file in a nightly or manual job.
- **Strings:** escape only `"`, `\` and U+0000–U+001F (`\b \t \n \f \r`, otherwise `\u00xx` lowercase), and emit everything else as raw UTF-8. **Do not use `Utf8JsonWriter`/`JsonSerializer`**, even with `UnsafeRelaxedJsonEscaping`: they escape non-ASCII and HTML-sensitive characters, which silently breaks hash equality with hometown.
- **Key order:** sort by UTF-16 code units, which is `string.CompareOrdinal`.
- **Input constraints (I-JSON):**
  - Reject duplicate keys. .NET 10 adds `JsonDocumentOptions.AllowDuplicateProperties = false`, which throws on duplicates. Its default is believed to be `true`, so set it explicitly (MEDIUM confidence).
  - Reject lone surrogates.
  - The top level must be an object.
- **Large integers:** they lose precision exactly as in JavaScript (`9007199254740993` becomes `…992`). This is correct per RFC 8785 and matches hometown (TypeScript). Document it in COMPATIBILITY.md, and consider having the v1 schema avoid numbers outside ±2^53.
- **Cross-component fixture:** commit a shared JCS vector set (cyberphone `testdata/input` and `output` plus Olive-specific proposal vectors) under `contracts/`. Ask hometown to run the same vectors through its TS canonicalizer, since §17.2 check 5 depends on the two producing byte-identical output.

### 6. JSON Schema 2020-12, strictly non-mutating

- **Recommended: Corvus.Text.Json.Validator 5.7.9.**
  - Load with `JsonSchema.FromFile(path, options)`. Disable network and file resolution (`allowFileSystemAndHttpResolution: false`) and pre-register sibling schemas via `additionalSchemaFiles`, so `$ref` resolution is hermetic.
  - Validate the **raw request bytes** (`schema.Validate(ReadOnlyMemory<byte>, collector)`) using `JsonSchemaResultsLevel.Detailed` for `400` error bodies.
  - Decide format assertion explicitly. It defaults to asserting, which is stricter than the spec's annotation-only default; keep it on and document it.
- **Non-mutation is guaranteed structurally, not by trusting a library flag.** Validate and canonicalize the *same immutable bytes* the client sent. The typed record used by the domain is deserialized from those bytes afterwards, purely for convenience, and is never re-serialized for hashing.
- **JsonSchema.Net 9.4.1:** technically excellent, about 53M downloads, and the most battle-tested 2020-12 implementation. However, since 2025 its binaries carry the **Open Source Maintenance Fee** agreement: revenue-generating users with at least US$10K annual revenue must pay for binary use. The source stays MIT. For a corporate POC this is a procurement question, not a technical one, so use Corvus unless the org already sponsors json-everything.
- **NJsonSchema 11.6.1: do not use.** It is built around Newtonsoft.Json and draft-04/OpenAPI code generation, its 2020-12 keyword coverage is incomplete, and its JSON.NET reader would coerce types during parsing.
- **Coercion trap:** never bind the proposal body to a typed parameter in a minimal API. `JsonSerializerDefaults.Web` sets `NumberHandling = AllowReadingFromString`, which turns `"5"` into `5`, the exact coercion §17.2(4) forbids. Accept `HttpRequest`, read the bytes, validate, then canonicalize. Deserialize with explicit options (`AllowDuplicateProperties = false`, `UnmappedMemberHandling = Disallow`, `NumberHandling = Strict`).

### 7. YAML catalog: YamlDotNet 18.1.0

- Use the **RepresentationModel (`YamlStream`)**, not object deserialization. Convert each document to `JsonNode` with explicit YAML 1.2 core-schema scalar rules:
  - only plain (unquoted) scalars may become numbers, booleans or null;
  - `1.0`, `no` and `on` follow YAML 1.2, not 1.1.
- Then validate the resulting JSON against `contracts/schemas/action-type.schema.json` using the same Corvus validator, and serve the same JSON from `GET /v1/catalog/{version}`. The embedded `parameters` JSON Schema is needed as JSON for validation anyway.
- If typed deserialization is used for any file, set `.WithDuplicateKeyChecking()` and `.WithEnforceRequiredMembers()`, and do **not** call `IgnoreUnmatchedProperties()`. All three exist on `DeserializerBuilder` in current source.
- `contracts/vendored/vocabulary.yaml` is read-only input. Verify its hash against `VENDORED.md` at startup and never write it.
- Alternative: `Corvus.Text.Json.Yaml` 5.7.9 (YAML 1.2 to JSON, same vendor) would remove the hand-written converter, but it is very new (about 5K downloads). YamlDotNet is the safer choice for a startup-critical path.

### 8. OpenAPI 3.1: hand-authored, served statically, verified by test

- **Source of truth:** `contracts/proposal/v1/openapi.yaml`, `$ref`-ing the JSON Schemas (Decision §12). Serve the file and its schemas as static content (for example `GET /v1/openapi.yaml`). **Do not** use `Microsoft.AspNetCore.OpenApi` 10.0.13 or Swashbuckle 10.3.1 to *publish* the contract: a generated document drifts toward whatever the C# types are, which is the inverse of the ratified model.
- Prevent drift with a contract test using **Microsoft.OpenApi 3.11.0 + YamlReader**. Load the document, assert zero diagnostics, then cross-check it against the app's `EndpointDataSource` (paths, methods, status codes) and the error-code enum (`idempotency_conflict`, `principal_not_permitted`, `trace_not_found`, …).
- Optionally, generate with `Microsoft.AspNetCore.OpenApi` (OAS 3.1 by default in .NET 10) **inside a test only** and diff the path and operation sets. It is useful but not required.

### 9. Outbound HTTP capture (session key reaches only popeye)

Use three layers, because a single `DelegatingHandler` only sees factory-created clients:

1. **Process-wide listener (the proof):** in the test, subscribe to `DiagnosticListener.AllListeners` for `HttpHandlerDiagnosticListener`, event `System.Net.Http.HttpRequestOut.Start`. Its payload carries the `HttpRequestMessage` for **every** `HttpClient` request in the process, whether factory-created or not. Record the URI and headers, and assert that the session key value appears only on requests whose host is the configured popeye host. This is BCL, so it needs no package. MEDIUM confidence on the exact event name and payload shape; verify in the phase.
2. **WireMock.Net 2.19.0 servers** as popeye and hometown stand-ins, so the *real* HTTP transport implementations are exercised. With in-process stubs only, there are zero outbound calls, and the capture would prove nothing. Assert on `server.LogEntries` headers as a second witness.
3. **`ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<OutboundAuditHandler>())`** in production code, which logs host and method only, never headers. Use **BannedApiAnalyzers** to forbid `new HttpClient`/handlers, so every client goes through `IHttpClientFactory`.

Also capture Npgsql traffic separately: the session key must never be a SQL parameter. Assert this by scanning stored rows for the key value in the same test.

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| Plain OpenIdConnect handler | Microsoft.Identity.Web 4.16.1 | Phase 2, if Olive must call Graph (group membership checks) or other downstream APIs with MSAL token caching |
| Npgsql + Dapper | Npgsql only (no Dapper) | If the team wants zero third-party data dependencies. The cost is about 20 lines of mapping per query. |
| Npgsql + Dapper | EF Core + Npgsql provider 10.0.3 | Not for this domain (RLS, INSERT-only and explicit predicates) |
| Hand-rolled runner | DbUp (dbup-postgresql 7.0.1) | If a reviewer insists on a library. Configure it to journal into `schema_migrations` and accept its column shape. |
| Corvus.Text.Json.Validator | JsonSchema.Net 9.4.1 | If the organization pays the OSMF or sponsors json-everything. Technically it is the reference-quality choice. |
| Corvus.Text.Json.Validator (V5) | Corvus.Json.Validator 5.7.9 (V4 engine, ~48K downloads) | If the V5 runtime validator shows a bug. It has the same vendor and license and the older API. |
| In-house JCS | cyberphone C# reference vendored verbatim | Acceptable: vendor the source (Apache-2.0, with attribution) instead of porting, but still own the tests |
| YamlDotNet | Corvus.Text.Json.Yaml 5.7.9 | If the hand-written YAML→JSON scalar mapping proves fiddly |
| In-process DevReviewer scheme | Keycloak / Duende IdentityServer 8.0.10 / OpenIddict 7.7.2 as local IdP | Not worth it. Duende needs a license for commercial use, and Keycloak is heavy. mock-oauth2-server covers the real-handler test. |
| xunit.v3 3.2.2 | xunit.v3 4.0.2 | Once Testcontainers (if used) and runner packages ship 4.x-compatible builds, and the team migrates to MTP v2 |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| Typed minimal-API body binding for `POST /v1/proposals` | Web JSON defaults coerce `"5"`→`5` and allow duplicate keys, which breaks §17.2(4) non-mutation and the hash | Read raw bytes from `HttpRequest`, then validate, then canonicalize |
| `Utf8JsonWriter` / `JsonSerializer` to produce canonical bytes | Its escaping and number formatting differ from ES `JSON.stringify` | In-house JCS emitter |
| `jsoncanonicalizer` 1.0.0 NuGet | Anonymous repackaging with unknown provenance, on the core-value hash path | In-house or vendored-with-attribution reference code |
| NJsonSchema | Newtonsoft-based, partial 2020-12 coverage, coercing reader | Corvus.Text.Json.Validator |
| EF Core for Olive tables | Implicit UPDATEs, hidden tenant filters, competing migrations | Npgsql + Dapper |
| Session-level `SET olive.tenant_id` | Leaks across pooled connections | `set_config('olive.tenant_id', @t, true)` inside each transaction |
| Running RLS tests as the Testcontainers `postgres` superuser | Superusers bypass RLS even with FORCE, so tests pass vacuously | `olive_app` / `olive_reader` logins created by the bootstrap |
| Respawn / TRUNCATE-based test cleanup | Needs privileges `olive_app` must not have, and fights INSERT-only tables | Fresh tenant UUID per test |
| Swashbuckle / generated OpenAPI as the published contract | Inverts the ratified "hand-authored contract is truth" model | Static `openapi.yaml` plus a drift test |
| Microsoft.Identity.Web in Phase 1 | Separate Entra-only code path, with MSAL and token-cache surface left unused | Plain OIDC handler |
| Any HTTP client SDK for nexus_crm, or any write-capable credential | Prohibited by AGENTS.md and charter §3.3 | Simulated JSONL executor only |

## Stack Patterns by Variant

**If `OLIVE_ENVIRONMENT=production`:**
- Refuse to start if the DevReviewer scheme, the fixture trace resolver or the popeye stub is registered.
- Because §16.5 bars fixtures in production, and the same guard should cover every stub, one startup check applies to all of them.

**If running in a container on `deploy_default`:**
- Use the host `deploy-postgres-1`, never `localhost` (§15.4).

**If unit tests (no Docker):**
- Use in-memory stores behind the same repository interfaces. Unit tests only, per §10; no RLS claims are made from these tests.

## Version Compatibility

| Package A | Compatible With | Notes |
|-----------|-----------------|-------|
| Testcontainers.XunitV3 4.16.0 | xunit.v3.extensibility.core >= 3.2.2 | Built against 3.x. xunit 4.0 changed extensibility APIs, so avoid the pairing with xunit.v3 4.x, or avoid XunitV3 entirely (recommended). |
| xunit.v3 4.0.2 | MTP v2 by default | The .NET 10 SDK warns that VSTest via MTP is unsupported. Migrate only deliberately. |
| dbup-postgresql 7.0.1 | Npgsql >= 10.0.1 (net8.0 target) | Only relevant if DbUp is chosen |
| Corvus.Text.Json.Validator 5.7.9 | net10.0 / net9.0 / netstandard2.x | The V5 engine dropped net8.0. That is fine on net10.0. |
| Microsoft.OpenApi.YamlReader 3.11.0 | Microsoft.OpenApi 3.11.0, SharpYaml 2.1.5 | Test-only. It brings SharpYaml transitively, which is separate from YamlDotNet and harmless. |
| Npgsql 10.0.3 | .NET 10, Microsoft.Extensions.Logging.Abstractions 10.0.0 | |
| ASP.NET Core 10.0.13 packages | SDK 10.0.204+ | Keep all `Microsoft.AspNetCore.*` packages on the same 10.0.x patch |

## Sources

- NuGet v3 API (`api.nuget.org/v3-flatcontainer`, `azuresearch-usnc.nuget.org/query`, registration and catalog entries): all version numbers, publish dates, dependency groups and download counts, queried 2026-10-21. HIGH
- https://www.nuget.org/packages/JsonSchema.Net/9.4.1/license: OSMF terms (US$10K revenue threshold, MIT source). HIGH
- NuGet license pages: Corvus (Apache-2.0), YamlDotNet (MIT), WireMock.Net (Apache-2.0), dbup-postgresql (MIT), Dapper (Apache-2.0), Testcontainers.XunitV3 (MIT). HIGH
- https://github.com/corvus-dotnet/Corvus.JsonSchema and `docs/Validator.md`: 2020-12 support, the V5 runtime validator API (`FromFile`, `Validate(bytes, collector)`, `allowFileSystemAndHttpResolution`, `alwaysAssertFormat` default true). MEDIUM-HIGH
- https://github.com/cyberphone/json-canonicalization: Apache-2.0 license, `dotnet/` reference implementation, `testdata/`, and the ES6 100M number test file. HIGH
- https://xunit.net/releases/v3/4.0.0: 4.0.0 released 2026-08-14, MTP v2 default, extensibility breaking changes. HIGH
- https://dotnet.testcontainers.org/modules/postgres/: `new PostgreSqlBuilder("postgres:15.1")` explicit-image builder. HIGH
- YamlDotNet `DeserializerBuilder.cs` (master): `WithDuplicateKeyChecking`, `WithEnforceRequiredMembers`, `IgnoreUnmatchedProperties`. HIGH
- https://github.com/microsoft/OpenAPI.NET: the 3.x line, with YAML reader as a separate package and OAS 3.2 support. MEDIUM
- https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowduplicateproperties and https://duendesoftware.com/blog/20260430-harden-your-dotnet-json-deserialization: the .NET 10 `AllowDuplicateProperties` / `Strict` preset. MEDIUM
- GitHub releases API: aspnetcore v10.0.13 (2026-10-13), testcontainers-dotnet 4.16.0, navikt/mock-oauth2-server 6.1.2 (2026-10-18), DbUp 6.1.1. HIGH
- Training data only (LOW, verify in phase): Npgsql URI connection-string support, the `HttpHandlerDiagnosticListener` event payload shape, Entra App Roles plus `max_age` as satisfying "checked at decision time", and `JsonSerializerDefaults.Web` `NumberHandling` (well known, but re-check).

---
*Stack research for: governed AI action broker and review queue (Olive Phase 1)*
*Researched: 2026-10-21*
