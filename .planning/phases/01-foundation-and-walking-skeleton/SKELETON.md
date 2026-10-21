# Walking Skeleton — Olive (Action Broker and Review Queue)

**Phase:** 1
**Generated:** 2026-10-21

## Capability Proven End-to-End

An identified agent (`liam`, via the popeye introspection stub) can `POST /v1/proposals` and `GET /v1/proposals/{id}` through a real ASP.NET Core host. The proposal is written and read back from a real PostgreSQL database, as a least-privilege role, through the migrator-applied schema, with the tenant isolated by an application predicate and by forced row-level security. There is no UI; the HTTP API is the interaction surface.

Plan mapping: scaffold and build controls (01-01), database foundation (01-02), identity building blocks (01-03), tenant unit of work and isolation proof (01-04), **the end-to-end slice (01-05)**, startup guards and secret-leak proof (01-06), CI and developer run path (01-07).

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Framework | C# / .NET 10, ASP.NET Core minimal APIs (`Microsoft.NET.Sdk.Web`) in `Olive.Host`; Razor Pages arrives in Phase 7 | Charter 6.2 recommends .NET and it matches bluto; no UI is needed in Phase 1 |
| Solution layout | 11 projects under `src/`: Domain, Contracts, Application, Persistence, Catalog, Executor.Simulated, Integrations.Popeye, Integrations.Hometown, Integrations.Entra, Host, Migrator; one `tests/Olive.Tests` project. Dependencies point inward (Domain <- Application <- adapters <- Host) | FND-01; enforced by the `FND_01` test against a literal reference graph |
| Data layer | PostgreSQL via Npgsql plus Dapper with explicit SQL, no EF Core. Tenant context is set per transaction with `set_config('olive.tenant_id', $1, true)` in `TenantUnitOfWork`; every statement also carries `tenant_id = @tenant_id` | SQL stays reviewable; pooled-connection tenant leakage is prevented structurally (DB-05) |
| Isolation | Two layers kept: application predicate plus `ENABLE` and `FORCE ROW LEVEL SECURITY` with `current_setting('olive.tenant_id')` (no `missing_ok`) in USING and WITH CHECK | Charter REQ-QUE-02; layer-ablation tests prove each blocks alone (DB-06) |
| Roles | `olive_owner` (NOLOGIN, owns objects), `olive_migrator` (member of owner), `olive_app`, `olive_reader`; none superuser or BYPASSRLS; `proposal_event` INSERT-only for `olive_app` | D§13; the service refuses to start as a privileged role (DB-07) |
| Migrations | Hand-written, forward-only runner in `Olive.Persistence`, invoked by `Olive.Migrator`; `sql/migrations/NNNN_*.sql`, `schema_migrations` with SHA-256, advisory lock, `SET ROLE olive_owner`; roles from `sql/bootstrap/` | D§13 and D-03; only the migrator reads `OLIVE_MIGRATOR_URL` |
| Auth (agents) | Session key read from `Authorization: Bearer <key>` (assumption D-12, isolated in `AgentKeyHeader`), introspected on every request through `IKeyIntrospector`; fixture-driven stub in `contracts/introspection/` marked `INonProductionOnly`; agent allowlist in source-controlled `config/agents.yaml` | D-12 to D-15; no cache in Phase 1 (D-14) |
| Secrets | `SessionKey` and `ServiceToken` are sealed non-record classes that redact themselves; no header logging; developer exception page never enabled | FND-06; sentinel tests |
| Startup guards | `IStartupFilter` runs before Kestrel listens: `OLIVE_ENVIRONMENT` required, no `INonProductionOnly` in production, vendored vocabulary SHA-256, no `OLIVE_MIGRATOR_URL`, DB role and migration checks | FND-03, FND-04, FND-05, DB-07 |
| Contracts | `contracts/vendored/vocabulary.yaml` never edited; problem responses use stable `reason` codes in `Olive.Contracts`; `contracts/schemas` and `contracts/examples` untouched until Phase 2 | AGENTS.md; D-04 |
| Packages and build | Central Package Management, lockfiles, `--locked-mode` in CI, BannedApiAnalyzers (`HttpClient`, `DateTime.UtcNow` banned; RS0030 is an error); no NetArchTest (reflection, csproj and lockfile tests instead) | FND-02, FND-07; no unverified package |
| Tests | xunit.v3 3.2.2 (VSTest), Testcontainers.PostgreSql 4.16.0 with an assembly fixture (image major pinned to `deploy-postgres-1`, expected `postgres:17-alpine`), `WebApplicationFactory`; tests named by requirement ID (`FND_01_...`, `DB_06_...`) | D-02, D-16, D-17 |
| Deployment target | No deployed environment in Phase 1. Documented local full-stack run: shared `deploy-postgres-1` plus `dotnet run --project src/Olive.Host` with a gitignored `.env`; CI on GitHub Actions `ubuntu-latest` | D-16, D-18, D-19 |
| Directory layout | `src/`, `tests/`, `sql/{bootstrap,migrations}`, `config/`, `contracts/{vendored,introspection,schemas,examples,queue}`, `catalog/`, `.github/workflows/` | Source-controlled config and catalog; Phase 2 adds `contracts/proposal/v1/` |

## Stack Touched in Phase 1

- [x] Project scaffold (solution, CPM, lockfiles, analyzers, test runner) — plan 01-01
- [x] Routing — `POST /v1/proposals`, `GET /v1/proposals/{id}` — plan 01-05
- [x] Database — one real write (`proposal` plus `submitted` event) and one real read under forced RLS — plans 01-02, 01-04, 01-05
- [x] UI interaction — maps to the HTTP API: an agent request wired through auth, principal checks and persistence — plan 01-05
- [x] Deployment — documented local full-stack run command plus Ubuntu CI — plans 01-06, 01-07

## Out of Scope (Deferred to Later Slices)

- Strict raw-byte body validation, duplicate-key rejection, JCS hashes, idempotent 200 replay and `idempotency_conflict` (Phase 2); the Phase 1 duplicate `(tenant, agent, run_id)` is a plain 409.
- Catalog endpoint and YamlDotNet catalog loading (Phase 2); the catalog entry `catalog/crm.case_note@1.yaml` is not consumed yet.
- Trace verification, token issuer and service-token cache (Phase 3).
- Policy, purpose and entitlement checks, expiry rules and lifecycle transitions out of `proposed` (Phase 4). The skeleton never writes `awaiting_approval`.
- Queue tables and kinds (`queue_item`, `queue_item_event`, Phase 5), `execution_ledger` and simulated execution (Phase 6), reviewer UI and Entra OIDC (Phase 7), identity_review kind (Phase 8), OpenAPI and evidence pack (Phase 9).
- Introspection cache, live popeye/hometown transports, any real write-capable credential or HTTP client.

## Subsequent Slice Plan

Each later phase adds one vertical slice on top of this skeleton without altering its architectural decisions:

- Phase 2: a resubmitted body converges on one proposal (JCS `body_sha256`, strict body validation) and the catalog is served from YAML.
- Phase 3: only a proposal whose trace resolves to the same tenant, agent, run and hash proceeds (fixture resolver, popeye token stub).
- Phase 4: policy refuses out-of-purpose proposals before any human sees them; lifecycle transitions and expiry.
- Phase 5: a reviewer claims and approves or rejects a queued proposal (migration 0002 follows the 0001 RLS and grant pattern).
- Phase 6: an approved proposal executes exactly once in simulation (migration 0003, `execution_ledger` INSERT-only).
- Phases 7 and 8 (parallel): the reviewer web UI with OIDC, and the identity_review queue kind.
- Phase 9: contract and evidence pack for the joint review.
