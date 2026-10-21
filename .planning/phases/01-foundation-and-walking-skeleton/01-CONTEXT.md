# Phase 1: Foundation and Walking Skeleton - Context

**Gathered:** 2026-10-21
**Status:** Ready for planning

<domain>
## Phase Boundary

An identified agent (`liam`) can `POST /v1/proposals` and `GET /v1/proposals/{id}` through a real ASP.NET Core host on Postgres. Tenant isolation is enforced in two layers: an application-level tenant predicate and forced RLS.

All of the following are in place before any feature work:
- the restructured solution;
- architecture tests;
- the `.gitattributes` byte-stability rule;
- the startup guards;
- redacting secret types;
- the role and migration model;
- CI.

**In scope:**
- **Requirements:** FND-01..07, DB-01..07, API-01, API-02, IDN-01, IDN-02, IDN-04. See `.planning/ROADMAP.md` Phase 1 success criteria.
- **Introspection:** introspect, then the principal checks.
- **Persistence:** persist, then read back.

**Not in this phase:**
- schema validation, JCS and idempotent replay (Phase 2);
- trace verification and the token issuer (Phase 3);
- policy and the expiry rules (Phase 4);
- the queue (Phase 5 onward).

</domain>

<decisions>
## Implementation Decisions

### Scaffold disposition
- **D-01:** Delete `src/Olive/Domain.cs` in Phase 1. Later phases rebuild their pieces from the specs, not from this code; git history keeps it as a reference. Its known defects are listed in `.planning/codebase/CONCERNS.md`.
- **D-02:** Retire the 11 scaffold tests in `tests/Olive.Tests/RequirementsTests.cs` along with `Domain.cs`. **Keep the naming convention**: tests are named by requirement or test-case ID (e.g. `REQ_ACT_01_…`, `TC_QUE_02_…`). Each phase that owns a requirement adds the real test for it.
- **D-03:** Replace `db/migrations/V001__action_broker.sql` with `sql/migrations/0001_init.sql`.
  - Tables go in the **public schema of the `olive` database**.
  - 0001 creates only **`proposal` and `proposal_event`**, with ENABLE + FORCE RLS and the D§13 grants.
  - Drop the `action_broker` and `integration_outbox` schemas, and delete `db/`.
  - `queue_item` and `queue_item_event` come in 0002 (Phase 5), and `execution_ledger` in 0003 (Phase 6). Both must follow the Phase 1 RLS and grant pattern and extend its privilege-matrix test.
- **D-04:** Leave `contracts/schemas/*.schema.json` and `contracts/examples/` untouched in Phase 1. Phase 2 moves and reshapes them into `contracts/proposal/v1/`.
- **D-05:** `Catalog.Load` and its YAML parser go away with `Domain.cs`. Phase 1 needs only the startup **SHA-256 check on the vendored `contracts/vendored/vocabulary.yaml`** (FND-03, DB-07 guard set). The YamlDotNet catalog is Phase 2 (API-10). *(Follows from D-01.)*

### Walking-skeleton proposal semantics
- **D-06:** A Phase 1 proposal is stored in state **`proposed`**, because no policy has run. Phase 4 adds the transitions out of `proposed`. The skeleton never writes `awaiting_approval`.
- **D-07:** Phase 1 accepts the **full v1 field set** and stores it as received: `action_type`, `party_ref`, `parameters` (jsonb), `justification`, `trace_ref`, `run_id` and the optional `nono_session_id`.
  - Checks are minimal: the body must be a JSON object with a string `run_id`. Anything else is a 400 and is not stored.
  - Phase 2 adds strict raw-byte validation, rejection of duplicate keys, and the JCS hashes.
- **D-08:** Record the introspected **key's `expires_at` in its own column**, alongside `key_id`, `agent_id`, `on_behalf_of`, `purpose` and `tenant_id`. The proposal's own `expires_at`, which is min(now + 8h, key expiry, `expires_in_seconds`), is added in Phase 4 under POL-07.
- **D-09:** A successful `POST /v1/proposals` returns **`201 Created`** with a `Location: /v1/proposals/{proposal_id}` header and the body `{proposal_id, state}`. The 200 for an idempotent replay arrives in Phase 2.
- **D-10:** Migration 0001 includes **`UNIQUE (tenant_id, agent_id, run_id)`**. In Phase 1 a duplicate submission gets a plain `409`, so there are never duplicate rows. Phase 2 replaces this with the D§6 / D§15.2 behaviour: 200 replay for an identical body, `409 idempotency_conflict` for a different one.
- **D-11:** `GET /v1/proposals/{id}` for an id in another tenant returns **404**, the same as a missing id.

### Introspection stub and agent key
- **D-12:** Olive reads the session agent key from **`Authorization: Bearer <key>`**. This is an **assumption**: the nono specs say the proxy injects the key, and Liam sends no `Authorization` header, but no spec names the header. Record it as an open item to confirm with the nono owner. Keep the header name isolated in one place so changing it is trivial.
- **D-13:** The popeye introspection stub is driven by **schema-checked contract fixtures in `contracts/introspection/`**, shaped exactly like the POPEYE_v0.4.2 §2.1 response:
  - Active keys return `active: true`, `key_id`, `principal_type`, `agent_id`, `on_behalf_of`, `purpose`, `tenant_id`, the data-class ceiling and `expires_at`.
  - Expired, revoked, unknown or non-agent keys return **only** `active: false`.
  - The fixtures cover an active `liam` agent in each of two tenants, a `user` key, an `application` key, a non-allowlisted agent, and an inactive key.
  - The stub implements `INonProductionOnly`.
- **D-14:** **No introspection cache in Phase 1.** Every request is introspected, and introspection always runs first. Record the POPEYE §2.2 constraint for when a cache is added: a positive result may be cached for at most 60 s and never past `expires_at`; negative results are never cached.
- **D-15:** The agent allowlist (`liam`) lives in a **source-controlled config file**, for example `config/agents.yaml`. Changes to it are change-managed like the catalog. It is not an environment variable or an appsettings override.

### Dev environment and CI
- **D-16:** Local development uses the **shared `deploy-postgres-1`** instance, as ratified in D§13. That means an `olive` database created by `nexus`, with roles created by the idempotent bootstrap script. **Tests never touch this instance.** They always use Testcontainers, and a guard stops tests from reaching `localhost:5432`.
- **D-17:** **Check the actual Postgres major version of `deploy-postgres-1`** by starting the Nexus CRM `deploy/` stack once and running `SHOW server_version`. Pin the Testcontainers image to that major version, and record the value. Postgres 15 or later is required. If it is older, stop and report.
- **D-18:** CI is **GitHub Actions on `ubuntu-latest`**, defined in `.github/workflows/` for the `OscarMackJr/olive` remote. It restores in locked mode, builds, and runs the unit, architecture and Testcontainers tests.
- **D-19:** A developer runs the service with **`dotnet run` plus a gitignored `.env`**, with a committed `.env.example` that follows the D§15.4 variables. The migrator is a separate command and is the only reader of `OLIVE_MIGRATOR_URL`. The service refuses to start if `OLIVE_MIGRATOR_URL` is set in its environment.

### Carried forward (already decided: do not re-ask)
- **Projects:** Domain, Contracts, Application, Persistence, Catalog, Executor.Simulated, Integrations.Popeye, Integrations.Hometown, Integrations.Entra, Host, Migrator (FND-01). Phase 1 may create empty or minimal projects for the ones it doesn't yet use.
- **Database access:** Npgsql + Dapper with explicit SQL, not EF Core.
  - `set_config('olive.tenant_id', $1, true)` inside explicit transactions only.
  - `current_setting('olive.tenant_id')` without `missing_ok` in the policies.
- **Migration runner:** hand-written and forward-only.
  - Uses `schema_migrations` and an advisory lock, and fails on checksum drift.
  - Runs with `SET ROLE olive_owner`, and roles come from a separate bootstrap script.
- **Tests:**
  - xUnit v3 3.2.2 (not 4.x) and Testcontainers.PostgreSql 4.16.0 with a native assembly fixture.
  - Fixtures expose `olive_app` and `olive_reader` connection strings only, and assert that `rolsuper` and `rolbypassrls` are false.
  - Two tenants are seeded, and the negative tests use `Max Pool Size=1`.
- **Startup guards:** the service refuses to start if `OLIVE_ENVIRONMENT` is unset, or if any `INonProductionOnly` implementation is registered in `production`. That covers all stubs, per Q17.
- **Secret types:** `SessionKey` and `ServiceToken` are sealed and redact themselves. Use no C# `record` for any type that holds a secret.
- **Supporting tools:** Central Package Management with lockfiles, BannedApiAnalyzers, and NetArchTest (or an equivalent).

### Claude's Discretion
- How each project is internally organised, and which projects are created empty versus populated in Phase 1.
- The exact column names and types in 0001, as long as they satisfy D§13 and the decisions above.
- The problem-details response shape for 400, 401, 403, 404 and 409. Keep reason codes stable, because Phase 9's OpenAPI documents them.
- The format of the startup-guard failure messages, as long as they never include a secret value.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Ratified decisions and charter
- `specs/DECISION_olive-poc2-phase1_v0.5.md`:
  - §1–§2: simulation-only.
  - §4: the stop-if-absent rule.
  - §6: endpoints.
  - §7: introspection claims and principal checks.
  - §10, §13: Postgres, roles, RLS, immutability and migrations.
  - §15.2: order of operations.
  - §15.4: connection variables.
  - §16.5: `OLIVE_ENVIRONMENT`.
- `specs/OLIVE_CHARTER_v0.4.md`:
  - §2: boundary.
  - §3.3: the write credential (none exists in Phase 1).
  - §4: REQ-QUE-02, two-layer isolation.
- `AGENTS.md`: scaffold constraints (no connector, SDK, HTTP client or credential), the vendored vocabulary is never edited, tenant predicates are kept alongside RLS.

### External specs (D§4 stop rule: confirm present at run start)
- `~/fiskroad/specs/POPEYE_v0.4.2.md`:
  - §2.1: the introspection response shape the stub must match.
  - §2.2: caching ceiling, inactive keys reveal nothing.
  - §2.5: introspection returns no entitlements; Olive resolves `on_behalf_of` to groups.
- `~/fiskroad/specs/NONO_v0.4.2.md`: injection of the agent key on `olive`. `NONO_v0.4.3.md` §5: `on_behalf_of` is a UPN.
- `~/liam/specs/LIAM_DESIGN_SPEC_v0.4.1.md`: Liam sends no `Authorization` header (around lines 368 and 541; REQ-LIAM-04).
- `~/fiskroad/specs/PROGRAM_SHARED_VOCABULARY_v0.4.md` and `contracts/vendored/vocabulary.yaml`: `principal_type ∈ {user, application, agent}`, purposes, tenant UUIDs.

### Project planning
- `.planning/REQUIREMENTS.md`: the Phase 1 requirement texts, rulings and working defaults.
- `.planning/research/SUMMARY.md`, `PITFALLS.md` and `ARCHITECTURE.md`: RLS superuser bypass, pooled settings with GUCs, owner versus migrator, secret leakage via `record.ToString()`, `.gitattributes` and CRLF, the marker-based startup guard.
- `.planning/codebase/CONCERNS.md`: the scaffold defects that are being retired.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`contracts/vendored/vocabulary.yaml`** and **`contracts/vendored/VENDORED.md`**: the vendored vocabulary and its recorded SHA-256. This is the input to the startup hash guard. The working copy currently matches; protect it with `.gitattributes` before anything else.
- **`catalog/crm.case_note@1.yaml`**: stays as is; it is consumed from Phase 2.
- **`Directory.Build.props` / `Directory.Build.targets` / `global.json`**: .NET SDK 10.0.204 with `rollForward: latestFeature`, and `UseAppHost=false`. Extend them for CPM, analyzers and lockfiles.
- **`.gitignore`**: already excludes `.env`, `appsettings.*.json`, `*.jsonl`, `bin/`, `obj/`.

### Established Patterns
- Tests are named by requirement ID; keep this.
- There is no web host, DI, ORM or third-party package today, so Phase 1 introduces all of them under CPM and lockfiles.

### Integration Points
- The shared Postgres `deploy-postgres-1` on Docker network `deploy_default`, published on `localhost:5432` by Nexus CRM `deploy/`. Its admin and owner is `nexus`.
- GitHub remote `https://github.com/OscarMackJr/olive.git`, for CI.

</code_context>

<specifics>
## Specific Ideas

- **Scaffold tables are not ported.** The existing V001 tables `action_broker.proposals` and `queue_items` must not survive in any form. The IR-1 queue model replaces the scaffold's queue states in Phase 5.
- **Inactive keys reveal nothing.** The stub's `active: false` responses must carry no other field (POPEYE §2.2). Olive's 401 must not reveal whether a key once existed.

</specifics>

<deferred>
## Deferred Ideas

- **Introspection cache** (60 s, never past `expires_at`, positives only): add it with the live popeye transport (v2, LIVE-02).
- **Confirm the agent-key header name** (D-12) with the nono owner. Log it in STATE.md as an open coordination item.
- **Q14 conflict.** NONO_v0.4.3 §5 says `on_behalf_of` is a UPN, but the Q14 working default says to map by Entra `oid`+`tid` and "never UPN". The self-approval mapping needs a ruling before Phase 5, and before Phase 7 for the real IdP.
- **Q4, narrowed.** POPEYE_v0.4.2 §2.5 says introspection returns no entitlements and that Olive resolves `on_behalf_of` to directory groups. Phase 4 must decide how Olive does that resolution without a write-capable credential (REQ-ACT-04). The options are a Graph read credential, a source-controlled mapping for the POC, or something else.

</deferred>

---

*Phase: 01-foundation-and-walking-skeleton*
*Context gathered: 2026-10-21*
