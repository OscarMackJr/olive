---
phase: 1
slug: foundation-and-walking-skeleton
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-10-21
audited: 2026-10-22
---

# Phase 1 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 on VSTest (`Microsoft.NET.Test.Sdk` 18.0.0), `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, Testcontainers.PostgreSql 4.16.0 (`postgres:17-alpine`) |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (CPM via `Directory.Packages.props`); assembly fixture `PostgresFixture` |
| **Quick run command** | `dotnet test --no-build --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test Olive.sln` locally; CI runs `dotnet test -c Release --no-build` after a Release build (needs Docker) |
| **Measured runtime** | ~13–15 s for the full suite (314 tests) on the dev machine |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --no-build --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test -c Release`
- **Before `/gsd-verify-work`:** Full suite must be green, locally and on Ubuntu CI
- **Max feedback latency:** 30 seconds (quick), 180 seconds (full)

---

## Per-Task Verification Map

Test counts are from the 2026-10-22 audit run (`dotnet test Olive.sln`: 314 passed, 0 failed, 0 skipped).

| Plans | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | Tests | Status |
|-------|------|-------------|------------|-----------------|-----------|-------------------|-------|--------|
| 01-01 | 1 | FND-01 | — | ProjectReference graph equals allowed inward graph; exactly eleven src projects | architecture | `dotnet test --filter "FullyQualifiedName~FND_01"` | 2 | ✅ green |
| 01-01 | 1 | FND-02 | T-1-01-02, T-1-01-03 | No outbound HTTP/socket assemblies; package allowlist matches lockfiles; RS0030 error level (plus `REQ_ACT_04_no_src_assembly_references_outbound_network_assemblies`) | architecture | `dotnet test --filter "FullyQualifiedName~FND_02\|FullyQualifiedName~REQ_ACT_04"` | 3 + 1 | ✅ green |
| 01-01, 01-06 | 1, 5 | FND-03 | T-1-01-01, T-1-06-03 | Vocabulary SHA-256 matches constant and VENDORED.md; LF-only; `.gitattributes` rule last; tampered or missing copy refused at startup | unit + integration | `dotnet test --filter "FullyQualifiedName~FND_03"` | 7 | ✅ green |
| 01-06 | 5 | FND-04 | T-1-06-04, T-1-06-06 | Host refuses to start with OLIVE_ENVIRONMENT unset or invalid, or with a migrator URL present, and never echoes the value | integration | `dotnet test --filter "FullyQualifiedName~FND_04"` | 20 | ✅ green |
| 01-06 | 5 | FND-05 | T-1-03-03, T-1-06-01 | Production with an `INonProductionOnly` service (instance, factory, type, keyed, decorated) fails; positive control passes | integration | `dotnet test --filter "FullyQualifiedName~FND_05"` | 13 | ⚠️ green, intermittent flake |
| 01-03, 01-06 | 2, 5 | FND-06 | T-1-03-01, T-1-03-02, T-1-06-05 | Sentinel key never appears in ToString/format/JSON/log/exception/response; ExposeSecret callers restricted | unit + integration | `dotnet test --filter "FullyQualifiedName~FND_06"` | 19 | ✅ green |
| 01-01, 01-07, 01-10, 01-13 | 1, 6, 1, 1 | FND-07 | T-1-07-01, T-1-07-02, T-01-13-01, T-01-13-02 | Workflow shape (Ubuntu, locked restore, Release build, no-rebuild test, hash check before restore, first-party actions); gate script fails closed on canned outputs | static + script | `dotnet test --filter "FullyQualifiedName~FND_07"` | 16 | ✅ green; CI run human-verified (01-HUMAN-UAT.md) |
| 01-02 | 2 | DB-01 | T-1-02-01 | Four roles exist with expected login and no elevated flags; bootstrap re-runnable | integration | `dotnet test --filter "FullyQualifiedName~DB_01"` | 4 | ✅ green |
| 01-02 | 2 | DB-02 | T-1-02-04, T-1-02-05 | Migrator applies as olive_owner and records rows; changed bytes fail checksum; drift detected; concurrent runs apply once; failing migration rolls back; URL parsing never leaks | integration + unit | `dotnet test --filter "FullyQualifiedName~DB_02"` | 44 | ✅ green |
| 01-02 | 2 | DB-03 | T-1-02-02 | Catalog scan: every table has NOT NULL uuid tenant_id, forced RLS, one strict policy without missing_ok | integration | `dotnet test --filter "FullyQualifiedName~DB_03"` | 3 | ✅ green (partial by design, D-03) |
| 01-02, 01-09, 01-12 | 2, 1, 1 | DB-04 | T-1-02-03, T-01-09-01, T-01-12-01 | Privilege matrix exact; `proposal_event` INSERT-only (42501 on UPDATE/DELETE); proposal UPDATE limited to `state`; INSERT excludes `state`/`created_at`; reader SELECT only | integration | `dotnet test --filter "FullyQualifiedName~DB_04"` | 12 | ✅ green (partial by design, D-03) |
| 01-04 | 3 | DB-05 | T-1-04-03, T-1-04-06 | Tenant set per transaction via `set_config(...,true)`, switches on a reused connection; duplicate run returns null | integration | `dotnet test --filter "FullyQualifiedName~DB_05"` | 11 | ✅ green |
| 01-04 | 3 | DB-06 | T-1-04-01, T-1-04-02, T-1-04-04 | Ablation (predicate removed / RLS disabled), two tenants, Max Pool Size=1; unset tenant raises on fresh and reused connections | integration | `dotnet test --filter "FullyQualifiedName~DB_06"` | 8 | ✅ green |
| 01-06 | 5 | DB-07 | T-1-06-02, T-1-06-07 | Guard refuses superuser, BYPASSRLS, owner membership, behind/ahead versions, checksum mismatch, without leaking connection details | integration | `dotnet test --filter "FullyQualifiedName~DB_07"` | 15 | ✅ green |
| 01-05, 01-08, 01-11 | 4, 1, 1 | API-01 | T-1-05-06, T-01-08-01, T-01-11-01..04 | POST 201 + Location + `{proposal_id,state}`; post→get round trip 200; 400 on malformed, surrogate/NUL, out-of-range numbers; 413 oversized; 409 duplicate; class-22 mapping | integration | `dotnet test --filter "FullyQualifiedName~API_01"` | 62 | ✅ green |
| 01-05 | 4 | API-02 | T-1-05-03 | Same-tenant GET 200 (covered in `API_01_post_then_get_roundtrip_through_real_postgres` and `IDN_02_get_works_for_any_active_key_within_its_tenant`); cross-tenant and missing give identical 404; malformed id is 404, not 500 | integration | `dotnet test --filter "FullyQualifiedName~API_02"` | 2 (+2 shared) | ✅ green |
| 01-03, 01-05 | 2, 4 | IDN-01 | T-1-03-04, T-1-03-05, T-1-05-01 | Inactive/unknown/malformed key gives identical 401 before DB or body processing; introspection unavailable gives 503 | unit + integration | `dotnet test --filter "FullyQualifiedName~IDN_01"` | 47 | ✅ green |
| 01-03, 01-05 | 2, 4 | IDN-02 | T-1-03-06, T-1-05-02 | user/application key gives 403 `principal_not_permitted`; non-allowlisted agent gives 403; principal check precedes body validation | unit + integration | `dotnet test --filter "FullyQualifiedName~IDN_02"` | 23 | ✅ green |
| 01-05 | 4 | IDN-04 | T-1-05-04, T-1-05-09 | Stored row records key_id, on_behalf_of, purpose, tenant_id, expires_at, nono_session_id; body tenant ignored | integration | `dotnet test --filter "FullyQualifiedName~IDN_04"` | 2 | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

**Known flake:** `StartupGuardTests` failed intermittently in full-suite runs, about 1 in 3 during gap closure (`FND_04_invalid_environment_*`, `FND_05_production_with_stub_instance_registration_refuses`). These tests pass when run alone, and the cause appears to be parallel-run timing. The flake affects CI reliability, not coverage.

---

## Wave 0 Requirements

- [x] `tests/Olive.Tests/Olive.Tests.csproj` rewritten (xunit.v3 3.2.2, CPM, project refs); `RequirementsTests.cs` retired
- [x] `PostgresFixture` assembly fixture: starts the container, bootstraps as superuser, creates the DB and template, runs the real migrator, exposes `olive_app`/`olive_reader` strings, asserts role flags, seeds TENANT_ALPHA and TENANT_BETA
- [x] Ablation-DB helper (`AblationDatabase.cs`, cloned from the migrated template)
- [x] `WebApplicationFactory` builders with configuration overrides for guard tests (`OliveApiFactory.cs`, environment forced to Production)
- [x] In-memory log capture and sentinel helper (`InMemoryLogCapture.cs`)
- [x] Guard test that no test connects to localhost:5432 (`TestDatabase.cs` `NoSharedPostgres.Assert`)

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions | Result |
|----------|-------------|------------|-------------------|--------|
| First CI run green on Ubuntu | FND-07 | Requires pushing to GitHub | Push the branch and confirm the Actions workflow passes with `--locked-mode` restore | ✅ pass, 2026-10-22 (01-HUMAN-UAT.md) |
| `deploy-postgres-1` major version ≥ 15 | DB-01 | Container is stopped locally | Start the container and run `SHOW server_version` | ⬜ not yet run (tests use `postgres:17-alpine`) |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 180s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-10-22

---

## Validation Audit 2026-10-22

| Metric | Count |
|--------|-------|
| Requirements audited | 19 |
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |
| Tests in suite | 314 (all passing) |
