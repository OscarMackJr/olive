---
phase: 2
slug: contract-integrity-and-idempotent-submission
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-21
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits: `Category=Integration`, `Category=Architecture`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Estimated runtime** | ~20 seconds quick, ~120 seconds full |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet restore --locked-mode` and the vulnerable-package gate
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 02-01-T1,T2 | 02-01 | 1 | API-09 | — | JCS output matches RFC 8785 App. B and cyberphone vectors; golden vectors in `contracts/jcs/v1/` recompute byte-for-byte | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_09"` | ❌ W0 | ⬜ pending |
| 02-04-T1,T2; 02-06-T1,T2 | 02-04, 02-06 | 2, 3 | API-04 | — | Duplicate keys, lone surrogates, lossy numbers, null optional, coercion-needed values → 400, nothing stored, no 500 | unit + integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_04"` | ❌ W0 | ⬜ pending |
| 02-02-T2,T3; 02-06-T1 | 02-02, 02-06 | 1, 3 | API-05 | — | Contract schemas accept valid and reject invalid examples (format assertion on) | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_05"` | ❌ W0 | ⬜ pending |
| 02-03-T1,T2; 02-07-T1,T2 | 02-03, 02-07 | 1, 4 | API-06 | — | Same body replay → 200 same id + current state; different body → 409, no new row, log has no body; run_id missing/invalid → 400 first | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_06"` | ❌ W0 | ⬜ pending |
| 02-07-T1,T2 | 02-07 | 4 | API-07 | — | N parallel first submissions → exactly one row | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_07"` | ❌ W0 | ⬜ pending |
| 02-05-T2 | 02-05 | 2 | API-03 | — | GET catalog returns `crm.case_note@1` with parameter schema; 404 unknown; 401 inactive | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_03"` | ❌ W0 | ⬜ pending |
| 02-05-T1,T2; 02-06 | 02-05 | 2 | API-10 | — | YamlDotNet load; D-02 violations and duplicate YAML keys fail startup; no runtime registration | unit + architecture | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_10"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Planner fills Task ID / Plan / Wave and Threat Ref once plans exist.*

---

## Wave 0 Requirements

- [ ] `tests/Olive.Tests/JcsTests.cs` (+ vector loader) — API-09
- [ ] `tests/Olive.Tests/IJsonGateTests.cs` — API-04 / D-05 lossy-number table
- [ ] `tests/Olive.Tests/IdempotencyTests.cs` — API-06, API-07
- [ ] `tests/Olive.Tests/CatalogTests.cs` — API-03, API-10
- [ ] `tests/Olive.Tests/ContractSchemaTests.cs` — API-05; replace `JsonSchemaLite` in `IntrospectionFixtureTests`
- [ ] Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` for migration `0004`

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| hometown runs the shared golden-vector set | API-09 (Q20) | Cross-team execution outside this repo | Hand `contracts/jcs/v1/` to the hometown owner and record their pass result |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
