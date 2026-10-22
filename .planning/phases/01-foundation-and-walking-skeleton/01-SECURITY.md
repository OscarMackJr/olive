---
phase: 01
slug: foundation-and-walking-skeleton
status: verified
threats_open: 0
asvs_level: 1
created: 2026-10-22
---

# Phase 01 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

The register comes from the `<threat_model>` blocks of plans 01-01 to 01-13 (68 threats: 58 mitigate, 10 accept). gsd-security-auditor checked every `mitigate` row against the code and its tests on 2026-10-22, with file:line evidence. `dotnet test Olive.sln` passed 314/314 on main, Testcontainers and gate-script tests included.

---

## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| developer machine / CI -> repo | Line-ending conversion or tampering can change hashed files |
| NuGet registry -> build | Third-party packages enter the supply chain |
| source -> compiled Domain/Application | An outbound HTTP type would break the no-write-credential boundary |
| service process -> Postgres | The running role's privileges decide whether RLS applies |
| migrator -> Postgres | A privileged connection that must never reach the service |
| test suite -> shared dev Postgres | A test touching deploy-postgres-1 could damage other apps' data |
| caller -> Olive (Authorization header) | The session key crosses here and must never be echoed |
| Olive -> popeye (stubbed) | Only the introspection port may see the raw key |
| config/agents.yaml -> allowlist | Source-controlled trust list, not overridable at runtime |
| Host -> Persistence | tenant_id originates from the introspected key and is the only tenant source |
| olive_app -> Postgres | RLS applies only to a non-owner, non-bypass role with the GUC set |
| pooled connection reuse | A tenant setting must never survive past its transaction |
| client/proxy -> Kestrel | Untrusted headers and bodies cross here |
| Host -> IKeyIntrospector | The raw session key goes only to the (stub) popeye port |
| Host -> Postgres | Tenant is derived from the introspected key and applied to every statement |
| process environment/.env -> service config | Misconfiguration (missing environment, migrator URL present) crosses here |
| DI container -> request pipeline | A stub registered in production would replace real identity |
| service -> Postgres | A privileged connection would bypass RLS |
| service -> log sinks/error pages | Secrets could leave through logs or exception pages |
| GitHub Actions runner -> repo | CI executes build and test code and downloads packages |
| developer workstation -> .env | Local secrets live in a gitignored file |
| README/.env.example -> developer behaviour | Documentation can steer a developer into an unsafe configuration |
| client to API | Untrusted JSON body crosses here; strings may contain invalid UTF-16 escapes |
| application role to database | olive_app session (buggy code path or compromise) writes to proposal |
| CI to NuGet vulnerability feed | Feed or tooling failure must not be read as "clean" |
| agent client to POST /v1/proposals | Untrusted JSON numbers and strings in parameters cross here |
| API to PostgreSQL jsonb | Values must be valid for jsonb before reaching the database |
| application role olive_app to proposal table | A buggy path or compromised session must not create pre-approved rows |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-1-01-01 | Tampering | contracts/vendored/vocabulary.yaml | mitigate | `.gitattributes` `-text` rule last; FND_03 tests assert LF-only, constant equality and VENDORED.md equality | closed |
| T-1-01-02 | Elevation of privilege | Domain/Application/Executor | mitigate | BannedApiAnalyzers RS0030 error plus FND_02 referenced-assembly test; HttpClient ban verified by a failing build | closed |
| T-1-01-03 | Tampering | NuGet dependencies | mitigate | CPM with transitive pinning, lockfiles, `--locked-mode` restore; FND_02 package allowlist per project | closed |
| T-1-01-SC | Tampering | npm/pip/cargo/NuGet installs | mitigate | Only audited NuGet packages from RESEARCH Package Legitimacy Audit are declared; unverified NetArchTest.Rules is deliberately NOT installed; no [SLOP]/[SUS] packages; `dotnet list package --vulnerable` runs in CI (01-07) | closed |
| T-1-01-04 | Repudiation | deleted scaffold tests | accept | Tests retired per D-02; retirement of REQ_ACT_04 recorded in SUMMARY; git history keeps the originals | closed |
| T-1-02-01 | Elevation of privilege | olive_app / RLS | mitigate | Roles NOSUPERUSER NOBYPASSRLS re-asserted by every bootstrap run; FORCE RLS on every table; fixture asserts rolsuper/rolbypassrls false and not owner member before any test runs | closed |
| T-1-02-02 | Information disclosure | policy `current_setting` without missing_ok | mitigate | DB_03 asserts the policy text has no missing_ok and every table has the policy; unset tenant raises (tested in 01-04) | closed |
| T-1-02-03 | Tampering / Repudiation | proposal_event history | mitigate | No UPDATE/DELETE/TRUNCATE grant; DB_04 matrix plus live 42501 attempts; composite FK keeps child rows in the parent's tenant | closed |
| T-1-02-04 | Tampering | applied migration files | mitigate | SHA-256 per file in schema_migrations, drift fails the run, advisory lock, per-file transaction; `.gitattributes` forces LF so the checksum is stable across OS | closed |
| T-1-02-05 | Information disclosure | connection URL / SQL in errors | mitigate | PostgresUrl and migrator exceptions are static messages; no Include Error Detail; MigratorCommand never prints the URL | closed |
| T-1-02-06 | Denial of service / Tampering | shared deploy-postgres-1 | mitigate | Tests use Testcontainers only; `NoSharedPostgres.Assert` rejects localhost:5432 (D-16); the dev instance is only touched by the one-time version check (read-only `SHOW`/`\dn`) | closed |
| T-1-02-07 | Elevation of privilege | olive_app creating objects | mitigate | `revoke all on schema public from public`; DB_04 asserts olive_app `create table` gives 42501 | closed |
| T-1-03-01 | Information disclosure | SessionKey / ServiceToken | mitigate | Sealed non-record classes; ToString, IFormattable, DebuggerDisplay and JsonConverter all redact; FND_06 sentinel tests plus reflection test forbidding records that hold secrets | closed |
| T-1-03-02 | Information disclosure | ExposeSecret reach | mitigate | Source-scan test limits callers to Popeye and the secret types | closed |
| T-1-03-03 | Spoofing | stub introspector in production | mitigate | Stub implements INonProductionOnly; reflection test requires the marker on Stub*/Fixture* types; the production guard is enforced in 01-06 | closed |
| T-1-03-04 | Information disclosure | key-existence probing | mitigate | Unknown, expired and inactive keys all return the same Inactive result; schema forbids any other field next to active:false | closed |
| T-1-03-05 | Spoofing / Elevation | malformed active response (non-UUID key_id, principal_type outside the vocabulary) | mitigate | Stub fails closed with IntrospectionUnavailableException (503 in 01-05); schema and tests cover each case | closed |
| T-1-03-06 | Tampering | agent allowlist override | mitigate | Allowlist is an embedded, change-managed file; test asserts no environment/IConfiguration read; empty or malformed fails closed | closed |
| T-1-03-07 | Spoofing | assumed Authorization header (D-12) | accept | Header isolated in AgentKeyHeader; open item for the nono owner logged in STATE.md by 01-07; low change cost | closed |
| T-1-04-01 | Information disclosure | cross-tenant read | mitigate | App predicate (`tenant_id = @tenant_id`) plus forced RLS; DB_06 removes each layer in turn and shows the other blocks | closed |
| T-1-04-02 | Tampering | cross-tenant write | mitigate | Same two layers; DB_06 asserts 42501 on cross-tenant INSERT and 0 rows on cross-tenant UPDATE/TransitionAsync | closed |
| T-1-04-03 | Information disclosure | tenant GUC leaking across pooled connections | mitigate | `set_config(..., true)` inside an explicit transaction only; no session SET; tests with Max Pool Size=1 assert transaction-locality | closed |
| T-1-04-04 | Elevation of privilege | unset tenant silently returns nothing | mitigate | Policies use current_setting without missing_ok; DB_06 asserts a PostgresException for unset tenant (fresh and reused) | closed |
| T-1-04-05 | Tampering | SQL injection | mitigate | Parameters only (Dapper); the tenant GUC is set through a bind parameter; ablation helper validates table names against a strict regex | closed |
| T-1-04-06 | Information disclosure | existence oracle via unique violation | mitigate | Uniqueness includes tenant_id; duplicate returns null; same (agent, run) in two tenants both succeed (tested) | closed |
| T-1-04-07 | Information disclosure | Postgres error detail | mitigate | No Include Error Detail; store does not catch-and-rethrow messages; Host maps PostgresException to a generic problem in 01-05 | closed |
| T-1-05-01 | Spoofing | authentication handler | mitigate | Introspect on every request first; inactive/unknown/expired/malformed all give identical 401 key_inactive; fail closed 503 on introspection error; IDN_01 tests including body-not-processed-first | closed |
| T-1-05-02 | Elevation of privilege | principal checks | mitigate | agent-only plus allowlist enforced before validation; IDN_02 tests for user, application and non-allowlisted agent | closed |
| T-1-05-03 | Information disclosure | cross-tenant GET | mitigate | tenant from key only; app predicate plus RLS (01-04); 404 identical to missing (D-11); API_02 tests | closed |
| T-1-05-04 | Tampering | body-supplied tenant or nono_session_id | mitigate | Body tenant ignored; nono_session_id stored as advisory only; IDN_04 tests | closed |
| T-1-05-05 | Information disclosure | session key / Authorization header in logs, responses, errors | mitigate | SessionKey redacts; no header logging, no HttpLogging, no developer exception page (builder forced to Production); exception handler logs type name only and returns static problems; API_01 error-path test greps responses and logs | closed |
| T-1-05-06 | Denial of service | oversized or deeply nested JSON | mitigate | 1 MiB bounded read (413) and Kestrel MaxRequestBodySize, JsonDocument MaxDepth 64 (400) | closed |
| T-1-05-07 | Tampering | SQL injection through body fields | mitigate | Parameterised Dapper only; parameters stored as jsonb via bound text parameter | closed |
| T-1-05-08 | Information disclosure | Postgres error text in responses | mitigate | PostgresException maps to generic 500 internal_error; no Include Error Detail | closed |
| T-1-05-09 | Repudiation | stored identity | mitigate | key_id, on_behalf_of, purpose, tenant_id, key_expires_at persisted with the proposal plus a submitted event (IDN-04) | closed |
| T-1-06-01 | Spoofing | INonProductionOnly stub in production | mitigate | Descriptor scan plus resolved-instance check (catches factory registrations) in an IStartupFilter before listen; FND_05 tests incl. instance, factory and type registrations and a positive control **Residual:** the guard sees through decorators only if they implement IComponentDecorator. | closed |
| T-1-06-02 | Elevation of privilege | superuser/BYPASSRLS/owner connection | mitigate | DatabaseSafetyChecks codes at startup; DB_07 tests for each role class; fixture asserts the same for olive_app | closed |
| T-1-06-03 | Tampering | vendored vocabulary | mitigate | SHA-256 of raw bytes at startup against the constant, copied (not embedded) so runtime tamper is detected; CI and FND_03 test compare the constant with VENDORED.md | closed |
| T-1-06-04 | Elevation of privilege | migrator credentials in the service | mitigate | Service refuses to start if OLIVE_MIGRATOR_URL exists in configuration or .env; only the Migrator reads it | closed |
| T-1-06-05 | Information disclosure | session key in logs, headers, responses, exceptions | mitigate | SENTINEL tests on success, inactive and hostile-exception paths; framework exception logging suppressed, handler logs type names only; developer exception page never enabled | closed |
| T-1-06-06 | Information disclosure | guard failure messages | mitigate | Static messages with rule codes only; tests assert no URL, user, password or configured value appears | closed |
| T-1-06-07 | Tampering | schema/version drift | mitigate | Embedded migration list must equal schema_migrations exactly (behind, ahead, checksum); the service refuses to start otherwise | closed |
| T-1-06-08 | Denial of service | startup guard bypass in tests only | accept | WebApplicationFactory uses the same filter (no test-only bypass exists); OLIVE_ENVIRONMENT=ci is a valid, guarded value | closed |
| T-1-07-01 | Tampering | CI dependency resolution | mitigate | `--locked-mode` restore and a vulnerable-package gate that fails the job; permissions limited to `contents: read` | closed |
| T-1-07-02 | Tampering | vendored vocabulary in CI | mitigate | `sha256sum -c` step before restore; test asserts the workflow hash equals the code constant | closed |
| T-1-07-03 | Information disclosure | secrets committed via .env | mitigate | `.env` is gitignored (verified); `.env.example` carries only a visible placeholder password; `OLIVE_MIGRATOR_URL` is commented and the service refuses to start if it is set | closed |
| T-1-07-04 | Elevation of privilege | migrator credential reaching the service | mitigate | README and `.env.example` direct the migrator URL to the shell only; startup guard (01-06) is the enforcement | closed |
| T-1-07-05 | Tampering | unpinned third-party actions | accept | Only first-party `actions/checkout` and `actions/setup-dotnet` at major tags; no third-party actions; revisit SHA pinning with live write paths (not in this milestone) | closed |
| T-1-07-06 | Denial of service | CI Testcontainers flakiness | accept | Single job; failures are visible; no shared infrastructure is touched by tests | closed |
| T-01-08-01 | Denial of Service | ProposalEndpoints body validation | mitigate | Reject unpaired surrogates and NUL in run_id, optional fields, parameters names and values before reaching jsonb; return 400 so error alerting is not floodable | closed |
| T-01-08-02 | Information Disclosure | 500 path and logs | mitigate | Static ProblemDetails text unchanged; no Error log on client faults, asserted by test | closed |
| T-01-09-01 | Tampering | proposal row | mitigate | Column-level UPDATE (state) only; denial tests for parameters, tenant_id, key_id, run_id | closed |
| T-01-09-02 | Tampering | migration 0001 | mitigate | 0001 untouched; checksum check still enforced; change shipped as 0002 | closed |
| T-01-10-01 | Tampering | CI vulnerable-package gate | mitigate | pipefail, explicit bash, positive-evidence check; static test guards against regression | closed |
| T-01-10-SC | Tampering | dependency supply chain | accept | No package installs added by this plan | closed |
| T-01-11-01 | Denial of Service | AllStringsValid number branch | mitigate | Per-number raw length cap 64 and exponent range -308..308 at the request edge | closed |
| T-01-11-02 | Denial of Service | amplification: many in-range numbers expanding in jsonb and parameters::text on GET | mitigate | Total numeric-expansion budget per request (sum over Number elements of mantissa digits + abs(exponent), capped at 65536) bounds worst-case expanded numeric text to about 65536 digits; tests post 1e131071 and an over-budget body of many 1e308 values (400) and an under-budget body (201). Residual: non-numeric payload size is bounded only by the body limit **Current (WR-10, 021e1aa):** each number charges max(0, mantissaDigits + |exp| - raw.Length) against the 65536 budget (growth beyond wire size only). | closed |
| T-01-11-03 | Tampering | class-22 PostgresException became 500 plus ERROR log (alert flooding) | mitigate | Backstop maps class 22 to 400 with no Error log; tested with a throwing store double **Current (WR-11, ab3e495):** only 22003, 22P02, 22P05, 22021 map to 400 with a Warning log of the SQL state; other class-22 codes give 500. | closed |
| T-01-11-04 | Information Disclosure | exception text echoing parameter values | mitigate | Backstop does not log or return exception message | closed |
| T-01-11-SC | Tampering | package installs | accept | No packages added | closed |
| T-01-12-01 | Elevation of Privilege | proposal INSERT of arbitrary state | mitigate | Column-level INSERT grant in 0003 excludes state and created_at; tests assert 42501 | closed |
| T-01-12-02 | Repudiation | pre-approved row with no proposal_event | mitigate | State forced to default 'proposed'; existing submitted event pairing retained | closed |
| T-01-12-03 | Tampering | edit of checksum-pinned 0001/0002 | mitigate | New migration only; acceptance check that 0001/0002 are unchanged | closed |
| T-01-12-SC | Tampering | package installs | accept | No packages added | closed |
| T-01-13-01 | Tampering | vulnerable-package gate passing on a dropped project | mitigate | Exact equality of clean-line count to the `dotnet sln list` project count, tested with canned outputs | closed |
| T-01-13-02 | Tampering | dotnet list failure hidden by tee | mitigate | PIPESTATUS exit code passed to the script, which fails on non-zero | closed |
| T-01-13-03 | Elevation of Privilege | script argument injection | accept | Arguments come from the workflow only; script validates the numeric count and quotes all expansions | closed |
| T-01-13-SC | Tampering | package installs | accept | No packages added | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-01 | T-1-01-04 | Scaffold tests were retired per D-02 and git history keeps the originals. REQ_ACT_04 was later re-added as a metadata test (ArchitectureTests.cs). | plan 01-01 | 2026-10-22 |
| AR-02 | T-1-03-07 | The assumed Authorization header (D-12) is isolated in AgentKeyHeader. The open item for the nono owner is logged in STATE.md, and changing it costs little. | plan 01-03 | 2026-10-22 |
| AR-03 | T-1-06-08 | WebApplicationFactory runs the same startup filter, so no test-only bypass exists. OLIVE_ENVIRONMENT=ci is a valid, guarded value. | plan 01-06 | 2026-10-22 |
| AR-04 | T-1-07-05 | CI uses only the first-party actions/checkout@v4 and actions/setup-dotnet@v4 at major tags (enforced by FND_07_workflow_uses_only_first_party_actions). Revisit SHA pinning when live write paths arrive. | plan 01-07 | 2026-10-22 |
| AR-05 | T-1-07-06 | CI Testcontainers flakiness: there is one job, failures are visible, and no shared infrastructure is touched. | plan 01-07 | 2026-10-22 |
| AR-06 | T-01-10-SC | No package installs in plan 01-10. | plan 01-10 | 2026-10-22 |
| AR-07 | T-01-11-SC | No package installs in plan 01-11. | plan 01-11 | 2026-10-22 |
| AR-08 | T-01-12-SC | No package installs in plan 01-12. | plan 01-12 | 2026-10-22 |
| AR-09 | T-01-13-03 | Script arguments come only from the workflow. The script validates the numeric count and quotes every expansion. | plan 01-13 | 2026-10-22 |
| AR-10 | T-01-13-SC | No package installs in plan 01-13. | plan 01-13 | 2026-10-22 |

*Accepted risks do not resurface in future audit runs.*

---

## Known Review Items (not in register)

Code-review findings that fall outside the plan-time register. None blocks this phase (block_on: high).

| Item | Concern | Status |
|------|---------|--------|
| WR-01 | Any active principal in a tenant can GET any proposal in that tenant. Restricting this is an authorization-policy ruling (Decision §7 / IDN-03, Phase 8). The cross-tenant case is covered by T-1-05-03. | Awaiting human ruling |
| IN-12 | olive_app can INSERT proposal_event with any created_at, and can insert arbitrary transition events. This sits next to T-01-12-02, whose own mitigation is in place. | Open (info) |

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-22 | 68 | 68 | 0 | gsd-security-auditor (verify mode, register authored at plan time) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-22
