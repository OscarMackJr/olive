# Phase 1: Foundation and Walking Skeleton - Research

**Researched:** 2026-10-21
**Domain:** .NET 10 ASP.NET Core host, Npgsql/Dapper on PostgreSQL with forced RLS, role and migration model, startup guards, secret-safe types, architecture tests, GitHub Actions CI
**Confidence:** MEDIUM-HIGH (stack versions verified against the NuGet API today; Postgres/RLS semantics are well established; items tagged [ASSUMED] need a quick spike or confirmation)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Delete `src/Olive/Domain.cs` in Phase 1. Later phases rebuild from the specs; git history keeps it. Known defects are in `.planning/codebase/CONCERNS.md`.
- **D-02:** Retire the 11 scaffold tests in `tests/Olive.Tests/RequirementsTests.cs` with `Domain.cs`. **Keep the naming convention**: tests are named by requirement or test-case ID (e.g. `REQ_ACT_01_…`, `TC_QUE_02_…`). Each phase that owns a requirement adds the real test.
- **D-03:** Replace `db/migrations/V001__action_broker.sql` with `sql/migrations/0001_init.sql`. Tables go in the **public schema of the `olive` database**. 0001 creates only **`proposal` and `proposal_event`**, with ENABLE + FORCE RLS and the D§13 grants. Drop the `action_broker` and `integration_outbox` schemas and delete `db/`. `queue_item` and `queue_item_event` come in 0002 (Phase 5), `execution_ledger` in 0003 (Phase 6); both must follow the Phase 1 RLS and grant pattern and extend its privilege-matrix test.
- **D-04:** Leave `contracts/schemas/*.schema.json` and `contracts/examples/` untouched in Phase 1 (Phase 2 moves them to `contracts/proposal/v1/`).
- **D-05:** `Catalog.Load` and its YAML parser go away with `Domain.cs`. Phase 1 needs only the startup **SHA-256 check on the vendored `contracts/vendored/vocabulary.yaml`** (FND-03, DB-07 guard set). The YamlDotNet catalog is Phase 2 (API-10).
- **D-06:** A Phase 1 proposal is stored in state **`proposed`**. The skeleton never writes `awaiting_approval`.
- **D-07:** Phase 1 accepts the **full v1 field set** and stores it as received: `action_type`, `party_ref`, `parameters` (jsonb), `justification`, `trace_ref`, `run_id`, optional `nono_session_id`. Checks are minimal: body must be a JSON object with a string `run_id`; anything else is a 400 and not stored. Phase 2 adds strict raw-byte validation, duplicate-key rejection and JCS hashes.
- **D-08:** Record the introspected **key's `expires_at` in its own column**, alongside `key_id`, `agent_id`, `on_behalf_of`, `purpose`, `tenant_id`. The proposal's own `expires_at` is Phase 4 (POL-07).
- **D-09:** Successful `POST /v1/proposals` returns **`201 Created`** with `Location: /v1/proposals/{proposal_id}` and body `{proposal_id, state}`. The 200 replay arrives in Phase 2.
- **D-10:** Migration 0001 includes **`UNIQUE (tenant_id, agent_id, run_id)`**. In Phase 1 a duplicate submission gets a plain `409`. Phase 2 replaces this with 200 replay / `409 idempotency_conflict`.
- **D-11:** `GET /v1/proposals/{id}` for an id in another tenant returns **404**, same as a missing id.
- **D-12:** Olive reads the session agent key from **`Authorization: Bearer <key>`**. This is an **assumption** (no spec names the header); record it as an open item for the nono owner. Keep the header name isolated in one place.
- **D-13:** The popeye introspection stub is driven by **schema-checked contract fixtures in `contracts/introspection/`**, shaped exactly like POPEYE_v0.4.2 §2.1. Active keys return `active: true`, `key_id`, `principal_type`, `agent_id`, `on_behalf_of`, `purpose`, `tenant_id`, data-class ceiling, `expires_at`. Expired, revoked, unknown or non-agent keys return **only** `active: false`. Fixtures cover an active `liam` agent in each of two tenants, a `user` key, an `application` key, a non-allowlisted agent, and an inactive key. The stub implements `INonProductionOnly`.
- **D-14:** **No introspection cache in Phase 1.** Every request is introspected; introspection always runs first. Record the POPEYE §2.2 constraint for later (positive cached ≤ 60 s and never past `expires_at`; negatives never cached).
- **D-15:** The agent allowlist (`liam`) lives in a **source-controlled config file**, e.g. `config/agents.yaml`; change-managed like the catalog. Not an env var or appsettings override.
- **D-16:** Local development uses the shared `deploy-postgres-1` (D§13): `olive` database created by `nexus`, roles from the idempotent bootstrap script. **Tests never touch this instance**; they always use Testcontainers, and a guard stops tests from reaching `localhost:5432`.
- **D-17:** **Check the actual Postgres major version of `deploy-postgres-1`** (start Nexus CRM `deploy/` once, `SHOW server_version`). Pin the Testcontainers image to that major version and record it. 15+ required; if older, stop and report.
- **D-18:** CI is **GitHub Actions on `ubuntu-latest`** in `.github/workflows/` for `OscarMackJr/olive`. Restore in locked mode, build, run unit, architecture and Testcontainers tests.
- **D-19:** A developer runs the service with **`dotnet run` plus a gitignored `.env`**, with a committed `.env.example` following D§15.4. The migrator is a separate command and the only reader of `OLIVE_MIGRATOR_URL`. The service refuses to start if `OLIVE_MIGRATOR_URL` is set in its environment.
- **Carried forward:** Projects Domain, Contracts, Application, Persistence, Catalog, Executor.Simulated, Integrations.Popeye, Integrations.Hometown, Integrations.Entra, Host, Migrator (FND-01; empty/minimal allowed). Npgsql + Dapper with explicit SQL, not EF Core; `set_config('olive.tenant_id', $1, true)` inside explicit transactions only; `current_setting('olive.tenant_id')` without `missing_ok`. Hand-written forward-only migration runner with `schema_migrations`, advisory lock, checksum drift failure, `SET ROLE olive_owner`, roles from a separate bootstrap script. xUnit v3 3.2.2 (not 4.x), Testcontainers.PostgreSql 4.16.0 with native assembly fixture; fixtures expose `olive_app`/`olive_reader` connection strings only and assert `rolsuper`/`rolbypassrls` false; two seeded tenants; negative tests use `Max Pool Size=1`. Startup guards (OLIVE_ENVIRONMENT, INonProductionOnly in production). `SessionKey` and `ServiceToken` sealed and self-redacting, no C# `record` for secrets. Central Package Management with lockfiles, BannedApiAnalyzers, NetArchTest (or equivalent).

### Claude's Discretion
- How each project is internally organised, and which projects are empty versus populated in Phase 1.
- Exact column names and types in 0001, as long as they satisfy D§13 and the decisions above.
- Problem-details response shape for 400, 401, 403, 404, 409. Keep reason codes stable (Phase 9's OpenAPI documents them).
- Format of startup-guard failure messages, as long as they never include a secret value.

### Deferred Ideas (OUT OF SCOPE)
- Introspection cache (60 s, never past `expires_at`, positives only): with the live popeye transport (v2, LIVE-02).
- Confirm the agent-key header name (D-12) with the nono owner; log in STATE.md as an open coordination item.
- Q14 conflict (UPN vs Entra `oid`+`tid`): needs a ruling before Phase 5/7.
- Q4, narrowed: how Olive resolves `on_behalf_of` to directory groups without a write-capable credential (Phase 4).
- Schema validation, JCS, idempotent replay (Phase 2); trace verification and token issuer (Phase 3); policy and expiry (Phase 4); queue (Phase 5+).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| FND-01 | 11-project solution, inward dependencies | Solution layout and reference graph below |
| FND-02 | Architecture tests + BannedApiAnalyzers | NetArchTest rules, package-allowlist test via `packages.lock.json`, `BannedSymbols.txt` (RS0030 set to error) |
| FND-03 | `.gitattributes` byte stability; vocabulary SHA-256 at startup and CI | `.gitattributes` content, hash guard, CI step; hash verified equal today |
| FND-04 | `OLIVE_ENVIRONMENT` required, no default | Startup guard pattern |
| FND-05 | No `INonProductionOnly` in production | Descriptor scan + resolved-instance check |
| FND-06 | Self-redacting secret types | Sealed class with redacting `ToString`/`IFormattable`/JSON converter; sentinel test |
| FND-07 | Ubuntu CI, locked packages | CPM + lockfiles + `--locked-mode`; workflow sketch |
| DB-01 | Roles via idempotent bootstrap | Bootstrap SQL pattern |
| DB-02 | Forward-only runner | Runner design (advisory lock, checksum, SET ROLE) |
| DB-03 | tenant_id + ENABLE/FORCE RLS, policy without `missing_ok` | 0001 SQL pattern; pattern-guard catalog test (table-driven for later tables) |
| DB-04 | INSERT-only event tables | Grants + privilege-matrix test |
| DB-05 | Tenant unit of work + application predicate | `TenantUnitOfWork` pattern; SQL-text lint test |
| DB-06 | Layer-ablation negative tests, Max Pool Size=1 | Ablation DB strategy, unset-tenant behaviour |
| DB-07 | Refuse superuser/BYPASSRLS/owner role; migration mismatch | Role-introspection queries |
| API-01 | POST /v1/proposals | Endpoint pattern, columns |
| API-02 | GET /v1/proposals/{id} tenant-scoped | Same |
| IDN-01 | Introspect first via port with stub | Auth handler + port + fixtures; POPEYE_v0.4.2 §2 read and confirmed present |
| IDN-02 | agent-only, allowlisted `liam`; 403 principal_not_permitted | Endpoint-level principal checks |
| IDN-04 | Record key_id, on_behalf_of, purpose, tenant_id, expires_at; advisory nono_session_id | Column set |
</phase_requirements>

## Summary

Phase 1 turns a one-file in-memory scaffold into an 11-project solution with a real ASP.NET Core host, two Postgres access roles, forced RLS, a hand-written migrator, startup guards and CI. The stack is already decided (CONTEXT carried-forward list); research confirms every pinned version still exists on NuGet today (Npgsql 10.0.3, Dapper 2.1.89, xunit.v3 3.2.2 line still on 3.x while 4.0.2 is current, Testcontainers.PostgreSql 4.16.0, BannedApiAnalyzers 5.6.0, Mvc.Testing 10.0.13) and that all D§4 prerequisite specs are present: POPEYE_v0.4.2 and v0.4.3, NONO_v0.4.2/v0.4.3, CROSS_COMPONENT_EXECUTION_SPEC.md, PROGRAM_SHARED_VOCABULARY_v0.4, LIAM_DESIGN_SPEC_v0.4.1, and both copies of the two DECISION files are byte-identical. The vendored `vocabulary.yaml` SHA-256 (`60a44f00…e8e7`) matches VENDORED.md, the upstream spec header and the working copy (LF in index and worktree).

The three things most likely to make this phase silently pass while being wrong are: (1) RLS tests that run as a superuser or owner, (2) pooled connections leaking or hiding the tenant setting, and (3) guards that can be bypassed by DI factory registrations or by ASP.NET's auto-enabled developer exception page leaking the `Authorization` header. Each has a concrete mitigation below. Two scope tensions need a planner decision: DB-03/DB-04 text names five tables but D-03 limits 0001 to two (follow D-03; make the pattern tests table-driven), and D-07 "full v1 field set stored as received" with only `run_id` mandatory implies most proposal columns are nullable in 0001.

**Primary recommendation:** Build bottom-up in this order: `.gitattributes` and CPM/lockfiles/analyzers, then solution restructure, then bootstrap+migrator+0001 with the Testcontainers fixture and the RLS/privilege tests, then startup guards and secret types, then introspection stub plus the two endpoints, then CI. Do the RLS and privilege tests before the endpoints exist, because they define the pattern Phases 5 and 6 copy.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Agent-key authentication (introspect, 401) | API / Backend (Host auth handler) | Integrations.Popeye (port impl) | Introspection is a server-side call; must run before any other logic |
| Principal checks (agent, allowlist) | API / Backend (Application) | — | Business rule, D§7 |
| Tenant isolation layer 1 (predicate) | API / Backend (Persistence SQL) | — | Every statement carries `tenant_id = @tenant_id` |
| Tenant isolation layer 2 (forced RLS) | Database / Storage | — | Enforced by policy for non-owner, non-bypass role |
| Role/privilege model, immutability | Database / Storage | Migrator | Grants make INSERT-only structural |
| Migration application | Migrator (separate command) | Database | Only reader of `OLIVE_MIGRATOR_URL` |
| Startup guards | API / Backend (Host) | Database (role introspection) | Fail closed before serving |
| Secret redaction | Domain/Application types | Host logging config | Type-level guarantee plus no header logging |
| Vendored file integrity | Host startup + CI | Git (`.gitattributes`) | Byte stability protects the hash |
| Browser / CDN tiers | n/a | n/a | No UI or static assets in Phase 1 |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| .NET SDK / `net10.0` | global.json 10.0.204 (`latestFeature`); dev box has 10.0.400 | Platform | Already set [VERIFIED: `dotnet --version` = 10.0.400, global.json read] |
| ASP.NET Core (`Microsoft.NET.Sdk.Web`) | shared framework 10.0.x | Minimal APIs host | In framework, no third-party surface [CITED: .planning/research/STACK.md] |
| Npgsql | 10.0.3 | Postgres driver, `NpgsqlDataSource` | Only serious ADO.NET provider [VERIFIED: NuGet API 2026-10-21] |
| Npgsql.DependencyInjection | 10.0.3 | `AddNpgsqlDataSource` | Host registration [VERIFIED: NuGet API] |
| Dapper | 2.1.89 | Row mapping over explicit SQL | Keeps SQL visible, no change tracking [VERIFIED: NuGet API] |
| YamlDotNet | 18.1.0 | Parse `config/agents.yaml` (Phase 1), catalog (Phase 2) | De facto .NET YAML library [VERIFIED: NuGet API]. Phase 1 use is only the allowlist; keep it in `Olive.Catalog` or Host, never Domain/Application |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 | Compile-time API ban | [VERIFIED: NuGet API] |

### Supporting (tests)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| xunit.v3 | 3.2.2 | Test framework (stay on 3.x; 4.0.2 exists) | All tests [VERIFIED: 4.0.2 is current on NuGet; 3.2.2 pin is a locked decision] |
| xunit.runner.visualstudio | 3.1.5 | VSTest runner | Keep VSTest mode |
| Microsoft.NET.Test.Sdk | 18.x (STACK.md says 18.11.0; scaffold uses 18.0.0) | VSTest host | Bump to a verified current 18.x when writing `Directory.Packages.props` |
| Testcontainers.PostgreSql | 4.16.0 | Throwaway Postgres | `new PostgreSqlBuilder("postgres:17-alpine")` (image arg is required in 4.x) |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.13 | `WebApplicationFactory<Program>` | Endpoint and startup-guard tests |
| NetArchTest.Rules | 1.3.2 | Layer/dependency rules | FND-02 [ASSUMED: package legitimacy not machine-verified; see audit] |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | `FakeTimeProvider` | Optional in Phase 1; inject `TimeProvider` from the start |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| NetArchTest.Rules 1.3.2 (last release old, Cecil-based) | TngTech.ArchUnitNET.xUnitV3 0.13.4 | ArchUnitNET is more active; either meets FND-02. CONTEXT says "NetArchTest (or an equivalent)". Recommend NetArchTest for the simple namespace/assembly dependency rules and a hand-written `packages.lock.json` reader for the package allowlist (neither tool does the latter) |
| Hand-written runner | DbUp | Rejected in CONTEXT (journal shape, no checksum) |

**Installation:** use `Directory.Packages.props` (CPM) with `ManagePackageVersionsCentrally=true`; `PackageReference` entries carry no version.

```bash
dotnet restore            # first run writes packages.lock.json per project
dotnet restore --locked-mode   # CI
```

**Version verification:** NuGet flat-container API queried 2026-10-21 for Npgsql, Npgsql.DependencyInjection, Dapper, xunit.v3, Testcontainers.PostgreSql, BannedApiAnalyzers, Mvc.Testing, YamlDotNet, NetArchTest.Rules, ArchUnitNET.xUnitV3. Latest stable values: 10.0.3, 10.0.3, 2.1.89, 4.0.2 (pin 3.2.2), 4.16.0, 5.6.0, 10.0.13, 18.1.0, 1.3.2, 0.13.4.

## Package Legitimacy Audit

slopcheck (installed from PyPI) only checks PyPI and could not reach the registry (TLS error); it is not applicable to NuGet. Registry existence and latest versions were confirmed through the NuGet API only, and per the provenance rule that does not confer VERIFIED status for packages not found in official docs. Packages that come from the prior STACK.md research or are first-party Microsoft/.NET Foundation/xunit/Testcontainers/Npgsql are treated as approved; the remaining ones are gated.

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| Npgsql, Npgsql.DependencyInjection | NuGet | many yrs | very high | github.com/npgsql/npgsql | n/a (PyPI-only tool) | Approved (ecosystem standard; cited in STACK.md) |
| Dapper | NuGet | many yrs | very high | github.com/DapperLib/Dapper | n/a | Approved |
| xunit.v3, xunit.runner.visualstudio | NuGet | established | high | github.com/xunit | n/a | Approved |
| Testcontainers.PostgreSql | NuGet | established | high | github.com/testcontainers | n/a | Approved |
| Microsoft.CodeAnalysis.BannedApiAnalyzers, Microsoft.AspNetCore.Mvc.Testing, Microsoft.NET.Test.Sdk | NuGet | Microsoft | very high | github.com/dotnet | n/a | Approved |
| YamlDotNet | NuGet | many yrs | very high | github.com/aaubry/YamlDotNet | n/a | Approved |
| NetArchTest.Rules | NuGet | ~5 yrs | moderate | github.com/BenMorris/NetArchTest | n/a | [ASSUMED] planner adds a `checkpoint:human-verify` before first install (or use ArchUnitNET) |

**Packages removed due to [SLOP]:** none. **Packages flagged [SUS]:** none. NuGet has no install-time `postinstall` scripts for these packages (analyzers ship `.props/.targets` only; review `build/` assets when adding BannedApiAnalyzers).

## Architecture Patterns

### System Architecture Diagram

```
 Liam (via nono proxy, injects Authorization: Bearer <session key>)
        |
        v
 [Kestrel] --body size limit--> [ExceptionHandler, ProblemDetails (never DeveloperExceptionPage)]
        |
        v
 [ApiKey AuthenticationHandler]  --reads header (one class owns header name)--> SessionKey (redacting)
        |  IKeyIntrospector.Introspect(SessionKey)   <-- port (Application)
        |        |-- Stub (INonProductionOnly): contracts/introspection/*.json fixtures
        |        '-- (v2) live popeye
        |  inactive/malformed ----> 401 problem (key_inactive), nothing else runs
        v
 ClaimsPrincipal{key_id, agent_id, principal_type, on_behalf_of, purpose, tenant_id, expires_at}
        v
 POST /v1/proposals                                GET /v1/proposals/{id}
   |  principal_type==agent? else 403 principal_not_permitted      |
   |  agent_id in config/agents.yaml? else 403                      |
   |  body is JSON object w/ string run_id? else 400 (not stored)   |
   v                                                                v
 [TenantUnitOfWork(tenant_id from introspection)]  <-- the only DB entry point
   BEGIN; select set_config('olive.tenant_id', @t, true);
   INSERT proposal ... ON CONFLICT (tenant_id,agent_id,run_id) DO NOTHING RETURNING   (conflict -> 409)
   INSERT proposal_event('submitted')      SELECT ... WHERE tenant_id=@t AND proposal_id=@id
   COMMIT                                   (0 rows -> 404)
        v
 PostgreSQL 'olive' db: role olive_app (NOSUPERUSER, NOBYPASSRLS, not owner)
   tables owned by olive_owner, ENABLE+FORCE RLS, policy tenant_id = current_setting('olive.tenant_id')::uuid

 Startup (before Run): OLIVE_ENVIRONMENT set? -> OLIVE_MIGRATOR_URL absent? -> no INonProductionOnly in production?
   -> vocabulary.yaml SHA-256 == expected? -> DB role not super/bypassrls/owner? -> schema_migrations == embedded list?
 Migrator (separate exe): OLIVE_MIGRATOR_URL as olive_migrator -> advisory lock -> SET ROLE olive_owner -> apply sql/migrations/NNNN_*.sql
```

### Recommended Project Structure
```
Olive.sln
global.json  Directory.Build.props  Directory.Build.targets  Directory.Packages.props
.gitattributes  .editorconfig(optional: RS0030 severity)  .env.example
config/agents.yaml
contracts/{vendored,schemas,examples,queue,introspection}/
sql/bootstrap/001_roles.sql          # cluster-level, idempotent, run by nexus / test superuser
sql/migrations/0001_init.sql
src/Olive.Domain            (no package refs)
src/Olive.Contracts         (DTOs/problem codes; no package refs)
src/Olive.Application       (ports: IKeyIntrospector, INonProductionOnly, ITenantUnitOfWork; no package refs)
src/Olive.Persistence       (Npgsql, Dapper; embeds sql/migrations/*.sql; PostgresUrl converter)
src/Olive.Catalog           (YamlDotNet; AgentAllowlist loader in Phase 1)
src/Olive.Executor.Simulated(empty)
src/Olive.Integrations.Popeye / .Hometown / .Entra   (Popeye: introspection stub + fixtures; others empty)
src/Olive.Host              (Microsoft.NET.Sdk.Web; Npgsql.DependencyInjection)
src/Olive.Migrator          (console; Npgsql via Persistence)
tests/Olive.Tests           (single project; categories via [Trait("Category","Unit|Architecture|Integration")])
.github/workflows/ci.yml
```

Reference graph (inward): Domain <- Application <- {Persistence, Catalog, Executor.Simulated, Integrations.*} <- Host; Migrator -> Persistence. Contracts has no inbound requirement on Domain. Host is the only composition root. Use `ProjectReference` only in these directions; an architecture test enumerates each csproj's `ProjectReference` set and compares to a literal allowlist.

### Pattern 1: Tenant unit of work (DB-05)
**What:** The only way to get a connection. Opens a connection, begins an explicit transaction, issues `set_config(..., true)` with the tenant, hands the transaction to repositories. Every SQL constant also contains `tenant_id = @tenant_id`.
```csharp
// Source: Npgsql/PostgreSQL docs for set_config(is_local => true); STACK.md decision
await using var conn = await dataSource.OpenConnectionAsync(ct);
await using var tx = await conn.BeginTransactionAsync(ct);
await conn.ExecuteAsync("select set_config('olive.tenant_id', @t, true)", new { t = tenantId.ToString("D") }, tx);
// ... repository calls using (conn, tx) ...
await tx.CommitAsync(ct);
```
`SET LOCAL` cannot take bind parameters; `set_config(name, value, true)` is the parameterizable equivalent with identical transaction scope. Never use session-level `SET`.

### Pattern 2: 0001 SQL shape (sketch; planner finalizes columns)
```sql
-- 0001_init.sql (applied as olive_owner)
create table proposal (
  tenant_id        uuid        not null,
  proposal_id      uuid        not null,
  state            text        not null default 'proposed'
    check (state in ('proposed','refused_policy','awaiting_approval','approved','rejected','executed','execution_failed','expired')),
  agent_id         text        not null,
  key_id           text        not null,
  on_behalf_of     text        not null,
  purpose          text        not null,
  key_expires_at   timestamptz not null,
  nono_session_id  text        null,            -- advisory only
  run_id           text        not null,
  action_type      text null, party_ref text null, parameters jsonb null,
  justification    text null, trace_ref text null,   -- nullable: D-07 mandates only run_id
  created_at       timestamptz not null default now(),
  primary key (tenant_id, proposal_id),
  unique (tenant_id, agent_id, run_id)
);
create table proposal_event (
  tenant_id uuid not null, event_id uuid not null, proposal_id uuid not null,
  event_type text not null, from_state text null, to_state text not null,
  actor text not null, detail jsonb null, created_at timestamptz not null default now(),
  primary key (tenant_id, event_id),
  foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id)  -- composite: FK checks bypass RLS
);
alter table proposal       enable row level security;  alter table proposal       force row level security;
alter table proposal_event enable row level security;  alter table proposal_event force row level security;
create policy tenant_isolation on proposal       using (tenant_id = current_setting('olive.tenant_id')::uuid) with check (tenant_id = current_setting('olive.tenant_id')::uuid);
create policy tenant_isolation on proposal_event using (tenant_id = current_setting('olive.tenant_id')::uuid) with check (tenant_id = current_setting('olive.tenant_id')::uuid);
revoke all on proposal, proposal_event from public;
grant select, insert, update on proposal       to olive_app;
grant select, insert         on proposal_event to olive_app;   -- INSERT-only: no UPDATE/DELETE
grant select on proposal, proposal_event to olive_reader;
```
Notes: no DELETE grant anywhere; uniqueness always includes `tenant_id` (so no cross-tenant existence leak through unique violations); composite FK keeps child rows in the parent's tenant because referential-integrity checks bypass RLS.

### Pattern 3: Bootstrap (cluster-level, idempotent, DB-01)
Run as `nexus` locally or as the container superuser in tests. Contains no passwords (passwords supplied separately: psql variables locally, test constants in the fixture).
```sql
do $$ begin
  if not exists (select 1 from pg_roles where rolname='olive_owner')    then create role olive_owner    nologin; end if;
  if not exists (select 1 from pg_roles where rolname='olive_migrator') then create role olive_migrator login;   end if;
  if not exists (select 1 from pg_roles where rolname='olive_app')      then create role olive_app      login;   end if;
  if not exists (select 1 from pg_roles where rolname='olive_reader')   then create role olive_reader   login;   end if;
end $$;
alter role olive_owner    nosuperuser nobypassrls nocreaterole nocreatedb;
alter role olive_migrator nosuperuser nobypassrls nocreaterole nocreatedb;
alter role olive_app      nosuperuser nobypassrls nocreaterole nocreatedb;
alter role olive_reader   nosuperuser nobypassrls nocreaterole nocreatedb;
grant olive_owner to olive_migrator;           -- plain GRANT works on PG 15, 16, 17
-- CREATE DATABASE cannot run in a DO block: done by a separate admin step:
--   create database olive owner olive_owner;  revoke all on database olive from public;
--   grant connect on database olive to olive_migrator, olive_app, olive_reader;
```
Re-running `ALTER ROLE ... NOBYPASSRLS` each time makes the script self-healing. PG 15+ revokes CREATE on schema `public` from PUBLIC; the database owner (`olive_owner`) owns `public` implicitly via `pg_database_owner`, so migrations work and `olive_app` cannot create objects [ASSUMED: standard PG15+ behaviour, confirm in the fixture with a test that `olive_app` gets 42501 on `create table`].

### Pattern 4: Migration runner (DB-02)
Connect as `olive_migrator` from `OLIVE_MIGRATOR_URL` (read only by `Olive.Migrator`). Per run: take `pg_advisory_lock(<constant>)`; `SET ROLE olive_owner`; `create table if not exists schema_migrations(version text primary key, filename text not null, sha256 text not null, applied_at timestamptz not null default now())`; grant SELECT on it to `olive_app` and `olive_reader` (the service's startup guard reads it). For each embedded file in lexical order: if recorded, compare SHA-256 of the raw bytes and abort on mismatch; else run it in its own transaction and insert the row. Embed the SQL into `Olive.Persistence` via `<EmbeddedResource Include="..\..\sql\migrations\*.sql" />` so the Host's version guard and the Migrator share one list; `.gitattributes` forces LF so the checksum is identical on Windows and Ubuntu.

### Pattern 5: Startup guards (FND-04/05, DB-07, D-19)
Run in `Program.cs` before `app.Run()`; throw a `StartupGuardException` with a static message naming the rule (never a secret or connection string). Guards:
1. `OLIVE_ENVIRONMENT` in {local, ci, nonprod, production}; unset or other value fails. Read from `IConfiguration` (env vars; plus the `.env` provider below), not from `ASPNETCORE_ENVIRONMENT`.
2. `OLIVE_MIGRATOR_URL` absent from environment and configuration.
3. If `production`: no `INonProductionOnly`. Scan `IServiceCollection` for `ImplementationType`, `ImplementationInstance?.GetType()` and keyed variants; for **factory registrations the implementation type is not visible**, so also, after `Build()`, resolve every registered port type (`IEnumerable<T>`) and test `is INonProductionOnly`. An architecture test also asserts every type in the stub namespaces implements the marker.
4. Vendored vocabulary hash: SHA-256 of the raw file bytes (copied to output as `Content`, not embedded, so runtime tampering is caught) compared case-insensitively to a constant; a CI test asserts the constant equals VENDORED.md and the on-disk file.
5. DB role (as the service's own connection, no tenant set):
```sql
select r.rolsuper, r.rolbypassrls,
       pg_has_role(current_user, 'olive_owner', 'MEMBER') as is_owner_member,
       exists (select 1 from pg_class c where c.relowner = r.oid and c.relnamespace = 'public'::regnamespace) as owns_objects
from pg_roles r where r.rolname = current_user;
```
6. Migrations: set of `schema_migrations` versions and checksums equals the embedded list exactly (behind or ahead both refuse).

### Pattern 6: Local `.env` (D-19)
`dotnet run` does not read `.env`. Add a ~30-line `DotEnvConfigurationSource` in Host that, if `.env` exists in the content root, adds its `KEY=VALUE` lines as a configuration source **below** real environment variables. No third-party package. Do not apply `appsettings.*.json` overrides for any `OLIVE_*` guard variable.

### Pattern 7: Secret types (FND-06)
```csharp
public sealed class SessionKey                     // not a record
{
    private readonly string _value;
    public SessionKey(string value) => _value = value;
    public string ExposeSecret() => _value;        // arch test: callable only from Integrations.Popeye
    public override string ToString() => "[REDACTED]";
}
```
Also implement `IFormattable` (redacted), mark `[DebuggerDisplay("[REDACTED]")]`, attach a `JsonConverter` that writes `"[REDACTED]"`, and exclude it from any DTO. Same for `ServiceToken`. Sentinel test: build a unique random sentinel, wrap it, and assert absence in `ToString`, `string.Format`/interpolation, `JsonSerializer.Serialize`, an in-memory `ILogger` capturing at Trace, an exception message from the auth handler path, and the problem-details response body.

### Pattern 8: Introspection port and stub
`IKeyIntrospector.IntrospectAsync(SessionKey, ct)` returns `Inactive` or `Active(KeyId, PrincipalType, AgentId, OnBehalfOf, Purpose, TenantId, DataClassCeiling, ExpiresAt)` (a record is fine here; it holds no secret). The stub maps fake keys (clearly prefixed, e.g. `fixture-key-liam-alpha`) to response JSON via an index in `contracts/introspection/`; unknown keys return `{active:false}`. Validate each fixture against a JSON schema in a test with a `oneOf`: either `{active:false}` with `additionalProperties:false`, or the full active shape. **POPEYE_v0.4.3 §2 requires `key_id` to be a lowercase canonical UUID** (regex `^[0-9a-f]{8}-…$`, same as `tenant_id_format`), so fixture `key_id`s must be UUIDs and the stub treats a malformed active response as fail-closed (503 `introspection_unavailable`). `principal_type` outside `{user, application, agent}` is also fail-closed.
Auth handler: authenticate = read header, introspect; inactive -> `AuthenticateResult.Fail` -> challenge writes a 401 problem with reason `key_inactive` and no hint whether the key ever existed. Principal checks (403) live in the endpoint (or a policy), after authentication.

### Anti-Patterns to Avoid
- **Tests as `postgres`/owner:** RLS passes vacuously. Fixture exposes only `olive_app`/`olive_reader` strings and asserts `rolsuper=false`, `rolbypassrls=false`, and not-owner.
- **`SET` (session) for the tenant:** leaks across pooled connections.
- **`UseDeveloperExceptionPage` / Development environment:** `WebApplication.CreateBuilder` auto-adds the developer exception page in Development, which prints request headers including `Authorization`. Create the builder with `EnvironmentName = Environments.Production` (and never read `ASPNETCORE_ENVIRONMENT` for logic) and use `UseExceptionHandler` + `AddProblemDetails` [ASSUMED: behaviour known from ASP.NET docs; the sentinel test is the proof].
- **Binding the body to a model in Phase 1:** read the raw JSON (`JsonDocument` with limited depth) so Phase 2's raw-byte validation is not blocked by a bound DTO.
- **`record` for secrets;** logging headers; `Include Error Detail=true` on production connection strings (error detail echoes row values).
- **Hashing files with CRLF:** see `.gitattributes`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Postgres driver, pooling | custom socket/pool | Npgsql `NpgsqlDataSource` | Pool reset semantics matter for tenant leakage |
| Row mapping | ad-hoc `reader.GetX` everywhere | Dapper with explicit SQL | Less boilerplate, SQL stays visible |
| Arch/dependency rules | reflection loops for assembly refs | NetArchTest (types) + small `packages.lock.json` reader (packages) | Cecil sees real type dependencies |
| Test Postgres | local installs, shared DB | Testcontainers.PostgreSql | Isolation, same as bluto |
| Problem responses | custom error JSON | `Results.Problem`/`AddProblemDetails` with a `reason` extension | Standard `application/problem+json` |
| Dep lock/pinning | hand-edited versions | CPM + `RestorePackagesWithLockFile` + `--locked-mode` | Supply-chain evidence |
| Env/secret masking | regex scrubbing of logs | Types that cannot render (sealed, redacting) | Prevention beats scrubbing |

Hand-roll (decided): the migration runner (~150 LOC), the Postgres URI converter, the tiny `.env` source, the `packages.lock.json` allowlist test.

**Key insight:** In this phase the cheap, high-value controls are structural (grants, FORCE RLS, sealed types, build-time bans), not procedural, because every later phase inherits them.

## Runtime State Inventory

Phase replaces the scaffold (rename/replace of `action_broker` schemas and in-memory domain).

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None. The scaffold has no Npgsql dependency and `V001__action_broker.sql` was never applied by any code; `deploy-postgres-1` exists but is stopped, and no `olive` database is recorded | Code edit only (delete `db/`, add `sql/migrations/0001_init.sql`). When first starting the shared instance, confirm no `action_broker`/`integration_outbox` schema was hand-applied (`\dn`) |
| Live service config | None. No external service references the scaffold names | None |
| OS-registered state | None | None |
| Secrets/env vars | None existing; `OLIVE_*` variables are new (`.env.example`) | Create `.env.example` |
| Build artifacts | Stale `bin/`/`obj/` of `src/Olive` and `tests/Olive.Tests`; `Olive.sln` references the old two projects; test csproj pins xunit.v3 3.2.0 | Rebuild solution; update `Olive.sln`; bump to 3.2.2 |

## Common Pitfalls

### Pitfall 1: Unset-tenant test passes for the wrong reason
**What goes wrong:** With `Max Pool Size=1`, a connection that earlier ran `set_config(..., true)` keeps a defined-but-empty placeholder GUC after commit, so `current_setting('olive.tenant_id')` returns `''` and `''::uuid` raises `22P02`; on a brand-new session it raises `42704` (unrecognized parameter). Npgsql's reset-on-close may clear it.
**How to avoid:** Assert "raises a `PostgresException`" (accept 22P02 or 42704) in **both** situations: fresh connection and after a prior tenant transaction on the same pooled physical connection. Never `missing_ok`.
**Warning signs:** A test that asserts an empty result instead of an exception.

### Pitfall 2: "Removing" RLS in a negative test
**What goes wrong:** `olive_app` cannot disable RLS, so teams skip the "RLS removed" half of DB-06.
**How to avoid:** Create an ablation database cloned from the migrated template (`create database olive_ablation_x template olive_template owner olive_owner`, run as superuser/admin connection inside the fixture) and, as `olive_owner`, `alter table ... disable row level security` there only; run the app-predicate queries as `olive_app` and assert cross-tenant read **and** write are blocked by the predicate. For "predicate removed": run the equivalent unfiltered SQL (`select … where proposal_id=@id`) as `olive_app` with the tenant set to A and assert tenant B rows are invisible and an `INSERT`/`UPDATE` of a B row fails (`42501`/0 rows). Run ablation collections non-parallel. Add a lint test that every SQL constant in Persistence contains `tenant_id = @tenant_id` (or `@tenant_id` in an INSERT column list) so the predicate cannot silently vanish from production code.

### Pitfall 3: Guard bypass through factory registrations
See Pattern 5 item 3. Resolve-and-check after build, plus the namespace architecture test.

### Pitfall 4: Line endings change the hashes
`git config core.autocrlf` is **true** on this Windows machine. Without `.gitattributes`, a checkout may convert `vocabulary.yaml` and `sql/migrations/*.sql` to CRLF and break both the vocabulary SHA-256 and migration checksums. Add `.gitattributes` **first**:
```
* text=auto
*.sql   text eol=lf
*.yaml  text eol=lf
*.yml   text eol=lf
*.json  text eol=lf
*.md    text eol=lf
contracts/vendored/** -text
contracts/** text eol=lf
```
`contracts/vendored/**` is marked `-text` (binary-like, never converted) to be strictest; it is already LF in index and worktree (`git ls-files --eol` shows i/lf w/lf). Verify with `git ls-files --eol` after adding the file; do a `git add --renormalize .` check shows no diff for vendored files.

### Pitfall 5: Npgsql timestamp and jsonb handling with Dapper
Npgsql writes `timestamptz` only for UTC `DateTime`/zero-offset `DateTimeOffset` (non-zero offset throws): always `ToUniversalTime()`. Reading `timestamptz` yields `DateTime` (Kind=Utc); map rows to `DateTime` or read `DateTimeOffset` explicitly [ASSUMED: Npgsql 6+ behaviour, covered by a round-trip test]. For jsonb pass text and cast in SQL (`@parameters::jsonb`) and read with `parameters::text`; avoids custom Dapper type handlers.

### Pitfall 6: Connection URI not supported by Npgsql
The planned `OLIVE_POSTGRES_URL=postgresql://…` is not a form `NpgsqlConnectionStringBuilder` parses; no URI handling was found in Npgsql's builder source [VERIFIED: grep of NpgsqlConnectionStringBuilder.cs on main returned no `postgresql://`; official docs search found nothing]. Spike result: write `PostgresUrl.ToNpgsql(string)` using `System.Uri` (scheme `postgresql`/`postgres`, `Uri.UnescapeDataString` on user/password, default port 5432, database from path, `sslmode` query -> `SslMode`), emit `Host/Port/Database/Username/Password/Pooling/Maximum Pool Size/Application Name`, never `Include Error Detail`. Unit-test with an `@` and `%` in the password, missing port, and bad scheme. Failure messages must not echo the URL.

### Pitfall 7: Lockfile portability between Windows and Ubuntu
`RestorePackagesWithLockFile` files generated on Windows (SDK 10.0.400) can differ from Ubuntu restore in rare cases (RID-specific assets), and CI uses SDK per `global.json` (10.0.204 floor). Mitigation: run `dotnet restore --locked-mode` on Ubuntu CI from the first commit; if drift appears, regenerate from a Linux run. [ASSUMED: risk, not observed]
**Also:** `Directory.Build.targets` forces `UseAppHost` false except where set true; keep that for the xunit.v3 test exe.

### Pitfall 8: BannedApiAnalyzers severity
RS0030 is a warning by default; set `dotnet_diagnostic.RS0030.severity = error` in `.editorconfig` (or `<WarningsAsErrors>$(WarningsAsErrors);RS0030</WarningsAsErrors>`) and add `BannedSymbols.txt` as `<AdditionalFiles>`. Ban: `T:System.Net.Http.HttpClient`, `HttpClientHandler`, `SocketsHttpHandler`, `T:System.Net.WebRequest`, `P:System.DateTime.UtcNow`, `P:System.DateTime.Now`, `P:System.DateTimeOffset.UtcNow` (inject `TimeProvider`). Domain/Application/Executor additionally get an architecture test with `NotHaveDependencyOn("System.Net.Http")`. [ASSUMED: default severity from memory; set explicitly so it does not matter.]

### Pitfall 9: Docker and Testcontainers environments
Dev box: Docker Desktop is running (v29.7.2); `postgres:16-alpine` and `postgres:17-alpine` images are cached locally. Ubuntu runners have Docker preinstalled. If a Windows run lacks Ryuk access, set `TESTCONTAINERS_RYUK_DISABLED=true` locally only. Tests must refuse to run if any resolved connection targets port 5432 on localhost (D-16).

### Pitfall 10: Request body abuse
Set `Kestrel` `MaxRequestBodySize` (e.g. 1 MiB) and `JsonDocumentOptions { MaxDepth = 64 }`; reject non-`application/json`. Phase 2 tightens, but unbounded bodies on day one are an avoidable DoS.

## Code Examples

### Architecture test sketch (FND-02)
```csharp
// NetArchTest.Rules
[Fact] public void FND_02_domain_application_executor_do_not_depend_on_http()
{
    foreach (var asm in new[]{ typeof(Olive.Domain.AssemblyMarker).Assembly, typeof(Olive.Application.AssemblyMarker).Assembly, typeof(Olive.Executor.Simulated.AssemblyMarker).Assembly })
        Assert.True(Types.InAssembly(asm).ShouldNot().HaveDependencyOn("System.Net.Http").GetResult().IsSuccessful, asm.GetName().Name);
}
// package allowlist: read src/<proj>/packages.lock.json, "dependencies" -> net10.0 -> entries with "type":"Direct";
// compare to Dictionary<string,string[]> allowlist; Domain/Application/Executor = empty.
```

### CI workflow sketch (FND-07, D-18)
```yaml
name: ci
on: [push, pull_request]
jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: echo "60a44f00db0daafcad3a4e11fe8fcbb2821e1ca7879302bae413b212a0c1e8e7  contracts/vendored/vocabulary.yaml" | sha256sum -c -
      - run: dotnet restore --locked-mode
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release
      - run: dotnet list package --vulnerable --include-transitive
```
Action major versions are [ASSUMED] current; confirm when writing the file. Fail the job if `--vulnerable` output contains "has the following vulnerable" (grep), since the command itself exits 0.

### Endpoint outline
```csharp
app.MapPost("/v1/proposals", async (HttpRequest req, ClaimsPrincipal user, ProposalService svc, CancellationToken ct) => { ... })
   .RequireAuthorization();            // handler already ran introspection
// 201 + Location: /v1/proposals/{id}; body {"proposal_id": "...", "state": "proposed"}
```
Problem reason codes (stable): `key_inactive` (401), `principal_not_permitted` (403 for user/application), `agent_not_allowlisted` (403, planner's choice; D§7 gives no code), `invalid_request` (400), `proposal_not_found` (404), `duplicate_run` (409, Phase 2 replaces with `idempotency_conflict`), `introspection_unavailable` (503).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `new PostgreSqlBuilder()` parameterless | `new PostgreSqlBuilder("postgres:17-alpine")` | Testcontainers 4.x | Image must be passed explicitly |
| xunit.v3 3.x | 4.0 (2026-08) with extensibility breaks | 2026-08-14 | Stay on 3.2.2 per CONTEXT |
| `.sln` | `.slnx` default in .NET 10 `dotnet new sln` | .NET 10 | Keep existing `Olive.sln` (supported) [ASSUMED: unchanged support] |
| VSTest vs Microsoft Testing Platform | .NET 10 SDK pushes MTP | .NET 10 | Keep VSTest (`Microsoft.NET.Test.Sdk`) per STACK.md |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `Authorization: Bearer` is the header nono's proxy injects (D-12, already flagged) | Pattern 8 | Header isolated in one class; low change cost |
| A2 | `deploy-postgres-1` is PG 17: derived from `Nexus-CRM/deploy/docker-compose.yml` (`postgres:17-alpine`) and `docker inspect` (config image `postgres:17-alpine`); container is stopped so `SHOW server_version` was not run | Pitfall 9, D-17 | If an older data volume were used the container would not start on 17; confirm with `SHOW server_version` on first start. Recommend pin `postgres:17-alpine` |
| A3 | RS0030 default severity is warning | Pitfall 8 | None if severity is set explicitly |
| A4 | Dapper/Npgsql DateTime/DateTimeOffset handling as described | Pitfall 5 | Round-trip test catches it |
| A5 | Forcing `EnvironmentName=Production` removes the auto developer exception page | Anti-patterns | Sentinel test covers; fallback is explicit pipeline ordering |
| A6 | Lockfiles stable across Windows/Ubuntu | Pitfall 7 | CI locked restore fails; regenerate on Linux |
| A7 | GitHub action majors `checkout@v4`, `setup-dotnet@v4` are current | CI sketch | Trivial |
| A8 | NetArchTest.Rules 1.3.2 legitimacy/compat with net10 | Standard Stack | Use ArchUnitNET instead |
| A9 | Plain `GRANT olive_owner TO olive_migrator` allows `SET ROLE` on PG 15-17 | Pattern 3 | Fixture test fails immediately; then use `WITH SET TRUE` on 16+ |
| A10 | PG15+ default leaves `public` CREATE to the db owner only | Pattern 3 | Test with `olive_app` create-table attempt |

## Open Questions (RESOLVED)

1. **DB-03/DB-04 text names five tables, D-03 and ROADMAP notes limit 0001 to two.**
   - **Resolution:** Resolved in 01-02 (table-driven RLS and privilege tests) and 01-07 (REQUIREMENTS.md marks DB-03/DB-04 partial: pattern established, tables completed in Phases 5/6).
   - Recommendation: follow D-03. Make the RLS-pattern catalog test and privilege-matrix test **table-driven** (a list of RLS tables and a list of INSERT-only tables) so 0002/0003 only append names. Planner should record in VERIFICATION that DB-03/04 are fully satisfied only after Phases 5-6; Phase 1 satisfies the pattern.
2. **Nullable columns for un-validated fields (D-07).** Recommendation: `run_id` NOT NULL; `action_type`, `party_ref`, `parameters`, `justification`, `trace_ref` nullable in 0001 with a handler check that, if present, they have the right JSON type (else 400). Phase 2 tightens with a new migration. Planner may instead require all of them (stricter than D-07) — a decision to confirm.
   - **Resolution:** Resolved in 01-04/01-05 (nullable columns in 0001 plus handler JSON type check, else 400).
3. **Migration numbering.** CONTEXT labels 0002 = queue (Phase 5), 0003 = ledger (Phase 6), but Phases 2-4 may need columns (`body_sha256`, `proposal_hash`, `trace_verification`, `expires_at`, policy fields). Recommend treating the numbers as nominal (sequential by arrival) and, optionally, adding nullable `body_sha256 text` to 0001 now. Needs a planner call.
   - **Resolution:** Resolved in 01-07 (migrations numbered sequentially in order of arrival; 0002/0003 labels are nominal).
4. **GET authorization scope.** Recommendation: any active introspected key may GET proposals in its own tenant (principal checks apply only to POST per D§7). Confirm whether non-agent keys should be allowed.
   - **Resolution:** Resolved in 01-05 (any active introspected key may GET proposals in its own tenant).
5. **Non-allowlisted agent response code** (reason string): D§7 names the allowlist but not the code; recommend 403 `agent_not_allowlisted`.
   - **Resolution:** Resolved in 01-05 (403 agent_not_allowlisted).
6. **Q4 for the stub shape:** POPEYE_v0.4.2 §2.5 confirms introspection returns no entitlements, so the Phase 1 fixture shape (no entitlement claim) is correct; the Phase 4 resolution mechanism stays open (deferred).
   - **Resolution:** Resolved in 01-03 (fixture shape carries no entitlement claim; Phase 4 mechanism stays deferred).
7. **`.env` loader vs `user-secrets`:** recommended tiny loader; trivially swappable.
   - **Resolution:** Resolved in 01-06 (tiny .env loader).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build | yes | 10.0.400 (global.json allows via latestFeature) | — |
| Docker Desktop | Testcontainers | yes | 29.7.2 | — |
| postgres:17-alpine, 16-alpine images | Testcontainers | yes (cached) | 17, 16 | pull |
| `deploy-postgres-1` container | local dev run (D-16/17) | exists, stopped | image postgres:17-alpine | start via `C:\Users\OMack\nexus\Nexus-CRM\deploy` compose |
| NuGet network | restore | yes | — | — |
| GitHub remote + Actions | CI | remote configured (`OscarMackJr/olive`); `.github/` absent | — | — |
| Python slopcheck | package audit | installed, PyPI-only, registry TLS failed | — | NuGet API check performed |
| Specs (D§4 stop rule) | stub shape | present: POPEYE_v0.4.2/4.3, NONO_v0.4.2/4.3, CROSS_COMPONENT_EXECUTION_SPEC.md, vocabulary v0.4, LIAM_DESIGN_SPEC_v0.4.1; both DECISION_agent-key-service-scope / desktop-agent-nono-integration copies hash-identical | — | — |

**Missing dependencies with no fallback:** none. Stop rule is satisfied (record the exact CROSS_COMPONENT filename `CROSS_COMPONENT_EXECUTION_SPEC.md` at run start).

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 on VSTest (`Microsoft.NET.Test.Sdk` 18.x), `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, Testcontainers.PostgreSql 4.16.0 |
| Config file | none yet; Wave 0 creates `tests/Olive.Tests/Olive.Tests.csproj` (OutputType Exe, UseAppHost true), assembly fixture `PostgresFixture` |
| Quick run command | `dotnet test --no-build --filter "Category!=Integration"` |
| Full suite command | `dotnet test -c Release` (needs Docker) |

### Phase Requirements to Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| FND-01 | ProjectReference graph equals allowed inward graph | architecture | `dotnet test --filter "FullyQualifiedName~FND_01"` | Wave 0 |
| FND-02 | No System.Net.Http in Domain/Application/Executor; package allowlist; `new HttpClient` fails compile | architecture | `--filter "FullyQualifiedName~FND_02"` | Wave 0 |
| FND-03 | vocabulary SHA-256 equals constant and VENDORED.md; `.gitattributes` rules present | unit | `--filter "FullyQualifiedName~FND_03"` | Wave 0 |
| FND-04 | Host start fails with OLIVE_ENVIRONMENT unset/invalid | integration (WebApplicationFactory) | `--filter "FullyQualifiedName~FND_04"` | Wave 0 |
| FND-05 | production + stub (instance and factory registration) fails | integration | `--filter "FullyQualifiedName~FND_05"` | Wave 0 |
| FND-06 | Sentinel never in ToString/format/JSON/log/exception/response | unit + integration | `--filter "FullyQualifiedName~FND_06"` | Wave 0 |
| FND-07 | CI green on Ubuntu with locked restore | CI | GitHub Actions run | Wave 0 |
| DB-01 | Roles exist with correct attributes after bootstrap; bootstrap re-runnable | integration | `--filter "FullyQualifiedName~DB_01"` | Wave 0 |
| DB-02 | Applies 0001 as olive_owner, records row; changed file fails; concurrent runs serialize | integration | `--filter "FullyQualifiedName~DB_02"` | Wave 0 |
| DB-03 | Catalog scan: every public table (except schema_migrations) has tenant_id, relrowsecurity, relforcerowsecurity, policy using `current_setting('olive.tenant_id')` w/o missing_ok in qual and with_check | integration | `--filter "FullyQualifiedName~DB_03"` | Wave 0 |
| DB-04 | Privilege matrix: olive_app UPDATE/DELETE on proposal_event denied (attempt gives 42501; `has_table_privilege` false); olive_reader SELECT only | integration | `--filter "FullyQualifiedName~DB_04"` | Wave 0 |
| DB-05 | Unit of work sets tenant per tx; SQL-constant lint for tenant predicate | unit + integration | `--filter "FullyQualifiedName~DB_05"` | Wave 0 |
| DB-06 | Ablation tests (predicate removed / RLS disabled) as olive_app, 2 tenants, Max Pool Size=1; unset tenant raises (fresh and reused connection) | integration | `--filter "FullyQualifiedName~DB_06"` | Wave 0 |
| DB-07 | Guard refuses superuser, BYPASSRLS, owner-member, version mismatch, checksum mismatch | integration | `--filter "FullyQualifiedName~DB_07"` | Wave 0 |
| API-01 | POST returns 201, Location, `{proposal_id,state:'proposed'}`; 400 for non-object / missing run_id; 409 on duplicate | integration | `--filter "FullyQualifiedName~API_01"` | Wave 0 |
| API-02 | GET same tenant 200; other tenant and missing id 404 | integration | `--filter "FullyQualifiedName~API_02"` | Wave 0 |
| IDN-01 | Inactive/unknown key 401 before any DB/body processing (no DB row, no body read assertion via stub call order) | integration | `--filter "FullyQualifiedName~IDN_01"` | Wave 0 |
| IDN-02 | user/application key 403 principal_not_permitted; non-allowlisted agent 403 | integration | `--filter "FullyQualifiedName~IDN_02"` | Wave 0 |
| IDN-04 | Stored row has key_id, on_behalf_of, purpose, tenant_id, key_expires_at, nono_session_id | integration | `--filter "FullyQualifiedName~IDN_04"` | Wave 0 |

Also: fixture JSON schema checks for `contracts/introspection/*` (active:false carries nothing else; key_id lowercase UUID), `PostgresUrl` converter tests, and a guard that tests never use localhost:5432.

### Sampling Rate
- **Per task commit:** `dotnet test --no-build --filter "Category!=Integration"` (unit + architecture, under 30 s)
- **Per wave merge:** full suite with Docker
- **Phase gate:** full suite green on Ubuntu CI before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/Olive.Tests/Olive.Tests.csproj` rewritten (xunit.v3 3.2.2, CPM, project refs)
- [ ] `PostgresFixture` (assembly fixture): start container, bootstrap as superuser, create DB and template, run real migrator, expose `olive_app`/`olive_reader` strings, assert role flags, seed two tenants (use TENANT_ALPHA `5325ffe9-5727-4d88-9832-aebabb8d0538`, TENANT_BETA `f92c9dba-50f1-4cad-9690-5d79f8ded335` from the vendored vocabulary)
- [ ] Ablation-DB helper (clone from template)
- [ ] `WebApplicationFactory` builders with configuration overrides for guard tests
- [ ] In-memory log capture and sentinel helper
- [ ] Framework install via CPM; remove `RequirementsTests.cs`

## Security Domain

### Applicable ASVS Categories (level 1)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | Session key introspected on every request; inactive reveals nothing (POPEYE §2.2); fail closed |
| V3 Session Management | no | Stateless API; reviewer sessions are Phase 7 |
| V4 Access Control | yes | Principal-type and allowlist checks; tenant predicate + forced RLS; 404 for cross-tenant |
| V5 Input Validation | yes | JSON object + string `run_id`, max body size, max depth, parameterized SQL only |
| V6 Cryptography | yes | SHA-256 from `System.Security.Cryptography`; no custom crypto |
| V7 Logging/Error Handling | yes | Never log headers/keys; static guard messages; ProblemDetails without exception text |
| V14 Configuration | yes | Startup guards, no defaults for environment, no migrator URL in service, locked packages |

### Known Threat Patterns

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Cross-tenant read/write | Information disclosure / Tampering | App predicate + forced RLS, non-owner non-bypass role, tested with each layer removed |
| RLS bypass via superuser/owner/BYPASSRLS | Elevation of privilege | Startup guard + fixture assertions |
| Tenant leak via pooled connection | Information disclosure | `set_config(..., true)` in explicit tx only, `Max Pool Size=1` tests |
| Session key / token in logs, exceptions, error pages | Information disclosure | Sealed redacting types, no header logging, no developer exception page, sentinel test |
| Stub identity provider in production | Spoofing | `INonProductionOnly` guard (descriptor + resolved-instance) |
| Key-existence probing | Information disclosure | Uniform 401 `key_inactive`; stub returns only `active:false` |
| SQL injection | Tampering | Parameters only; tenant GUC through `set_config` bind parameter |
| Event history rewrite | Tampering / Repudiation | INSERT-only grants on `proposal_event`; composite FK |
| Oversized/deep JSON | Denial of service | Kestrel body limit, `MaxDepth` |
| Postgres error detail leaking row values | Information disclosure | No `Include Error Detail`; map `PostgresException` to generic problem |

## Sources

### Primary (HIGH confidence)
- `specs/DECISION_olive-poc2-phase1_v0.5.md` §4, §6, §7, §13, §15.2-15.4 (read in full for those sections)
- `~/fiskroad/specs/POPEYE_v0.4.2.md` §2.1-2.5 and `POPEYE_v0.4.3.md` §2 (introspection shape; key_id lowercase UUID)
- `~/fiskroad/specs/NONO_v0.4.2.md` (injection on `olive`; header name not specified)
- `.planning/CONTEXT, REQUIREMENTS, ROADMAP, STATE`, `.planning/research/STACK.md`
- NuGet flat-container API (versions, 2026-10-21)
- Local checks: `git ls-files --eol`, sha256 of vocabulary, `Nexus-CRM/deploy/docker-compose.yml`, `docker inspect deploy-postgres-1`, `docker images`

### Secondary (MEDIUM confidence)
- npgsql/npgsql `NpgsqlConnectionStringBuilder.cs` source grep (no URI handling found)
- PostgreSQL behaviour of RLS/FORCE, `set_config`, `pg_roles` (standard documented semantics, applied from knowledge; fixture tests are the proof)

### Tertiary (LOW confidence)
- Items tagged [ASSUMED] in the Assumptions Log

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH for versions (registry-checked today), MEDIUM for NetArchTest
- Architecture: MEDIUM-HIGH; patterns are standard, with two scope questions for the planner
- Pitfalls: MEDIUM-HIGH; each has a test that proves or disproves it on first run

**Research date:** 2026-10-21
**Valid until:** 2026-11-20 (xunit 4.x/Testcontainers movement is the fast-changing part)
