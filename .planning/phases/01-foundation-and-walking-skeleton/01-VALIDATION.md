---
phase: 1
slug: foundation-and-walking-skeleton
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-21
---

# Phase 1 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 on VSTest (`Microsoft.NET.Test.Sdk` 18.x), `Microsoft.AspNetCore.Mvc.Testing` 10.0.13, Testcontainers.PostgreSql 4.16.0 (`postgres:17-alpine`) |
| **Config file** | none yet — Wave 0 rewrites `tests/Olive.Tests/Olive.Tests.csproj` and adds assembly fixture `PostgresFixture` |
| **Quick run command** | `dotnet test --no-build --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test -c Release` (needs Docker) |
| **Estimated runtime** | ~30 s quick; ~180 s full |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --no-build --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test -c Release`
- **Before `/gsd-verify-work`:** Full suite must be green, locally and on Ubuntu CI
- **Max feedback latency:** 30 seconds (quick), 180 seconds (full)

---

## Per-Task Verification Map

Task IDs are assigned by the planner; each task's `<verify>` must use the requirement filter below.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| TBD | TBD | TBD | FND-01 | — | ProjectReference graph equals allowed inward graph | architecture | `dotnet test --filter "FullyQualifiedName~FND_01"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-02 | T-1-supply | No System.Net.Http in Domain/Application/Executor; package allowlist; RS0030 error | architecture | `dotnet test --filter "FullyQualifiedName~FND_02"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-03 | T-1-tamper | vocabulary SHA-256 matches constant and VENDORED.md; `.gitattributes` present | unit | `dotnet test --filter "FullyQualifiedName~FND_03"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-04 | — | Host fails to start with OLIVE_ENVIRONMENT unset/invalid | integration | `dotnet test --filter "FullyQualifiedName~FND_04"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-05 | T-1-spoof | production + `INonProductionOnly` (instance and factory registration) fails | integration | `dotnet test --filter "FullyQualifiedName~FND_05"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-06 | T-1-disclose | Sentinel key never in ToString/format/JSON/log/exception/response | unit + integration | `dotnet test --filter "FullyQualifiedName~FND_06"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | FND-07 | T-1-supply | CI green on Ubuntu with locked restore | CI | GitHub Actions run | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-01 | T-1-eop | Roles exist with correct attributes; bootstrap re-runnable | integration | `dotnet test --filter "FullyQualifiedName~DB_01"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-02 | T-1-tamper | Applies 0001 as olive_owner, records row; changed file fails; concurrent runs serialize | integration | `dotnet test --filter "FullyQualifiedName~DB_02"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-03 | T-1-disclose | Table-driven catalog scan: tenant_id, RLS enabled+forced, strict policy | integration | `dotnet test --filter "FullyQualifiedName~DB_03"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-04 | T-1-repud | `proposal_event` INSERT-only for olive_app (42501); olive_reader SELECT only | integration | `dotnet test --filter "FullyQualifiedName~DB_04"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-05 | T-1-disclose | Tenant set per tx via `set_config(...,true)`; SQL-constant predicate lint | unit + integration | `dotnet test --filter "FullyQualifiedName~DB_05"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-06 | T-1-disclose | Ablation (predicate removed / RLS disabled), 2 tenants, Max Pool Size=1; unset tenant raises (fresh + reused conn) | integration | `dotnet test --filter "FullyQualifiedName~DB_06"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | DB-07 | T-1-eop | Guard refuses superuser, BYPASSRLS, owner-member, version mismatch, checksum mismatch | integration | `dotnet test --filter "FullyQualifiedName~DB_07"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | API-01 | T-1-dos | POST 201 + Location + `{proposal_id,state}`; 400 malformed; 409 duplicate | integration | `dotnet test --filter "FullyQualifiedName~API_01"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | API-02 | T-1-disclose | GET same tenant 200; other tenant / missing 404 | integration | `dotnet test --filter "FullyQualifiedName~API_02"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDN-01 | T-1-spoof | Inactive/unknown key 401 before DB or body processing | integration | `dotnet test --filter "FullyQualifiedName~IDN_01"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDN-02 | T-1-eop | user/application key 403 `principal_not_permitted`; non-allowlisted agent 403 | integration | `dotnet test --filter "FullyQualifiedName~IDN_02"` | ❌ W0 | ⬜ pending |
| TBD | TBD | TBD | IDN-04 | T-1-repud | Stored row has key_id, on_behalf_of, purpose, tenant_id, expires_at, nono_session_id | integration | `dotnet test --filter "FullyQualifiedName~IDN_04"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/Olive.Tests/Olive.Tests.csproj` rewritten (xunit.v3 3.2.2, CPM, project refs); `RequirementsTests.cs` retired
- [ ] `PostgresFixture` assembly fixture — start container, bootstrap as superuser, create DB + template, run real migrator, expose `olive_app`/`olive_reader` strings, assert role flags, seed TENANT_ALPHA `5325ffe9-5727-4d88-9832-aebabb8d0538` and TENANT_BETA `f92c9dba-50f1-4cad-9690-5d79f8ded335`
- [ ] Ablation-DB helper (clone from migrated template)
- [ ] `WebApplicationFactory` builders with configuration overrides for guard tests (environment name forced to Production)
- [ ] In-memory log capture and sentinel helper
- [ ] Guard test that no test connects to localhost:5432

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| First CI run green on Ubuntu | FND-07 | Requires pushing to GitHub | Push branch, confirm Actions workflow passes with `--locked-mode` restore |
| `deploy-postgres-1` major version ≥ 15 | DB-01 | Container is stopped locally | Start container, run `SHOW server_version` |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 180s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
