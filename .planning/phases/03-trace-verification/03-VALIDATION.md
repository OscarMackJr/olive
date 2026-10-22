---
phase: 3
slug: trace-verification
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-22
---

# Phase 3 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing 10.0.13 |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration`, `Category=Architecture`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Estimated runtime** | ~90 seconds (full, with Testcontainers) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet restore --locked-mode`
- **Max feedback latency:** 90 seconds

---

## Per-Task Verification Map

Filled in by the planner and executor. Requirement-to-test map from research:

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| TRC-01 | Fixtures conform to the seven-field schema; fixture hash equals `Jcs.ProposalHash` of its proposal | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~TRC_01"` | ❌ W0 | ⬜ pending |
| TRC-02 | Unknown trace -> 422, `retryable:true`, zero rows; same `run_id` retry after fixture added -> 201 | integration | `--filter "FullyQualifiedName~TRC_02"` | ❌ W0 | ⬜ pending |
| TRC-03 | Null/differing `run_id` -> stored `trace_unresolved`; `trace_schema_version` recorded | integration | `--filter "FullyQualifiedName~TRC_03"` | ❌ W0 | ⬜ pending |
| TRC-04 | Tenant mismatch -> `trace_tenant_mismatch` | integration | `--filter "FullyQualifiedName~TRC_04"` | ❌ W0 | ⬜ pending |
| TRC-05 | `agent_id` null or differs from token `delegated_agent_id` -> `trace_agent_mismatch` | integration | `--filter "FullyQualifiedName~TRC_05"` | ❌ W0 | ⬜ pending |
| TRC-06 | Changed `justification` / null hash -> `trace_proposal_mismatch`; key reorder passes; unknown key hashed | integration + unit | `--filter "FullyQualifiedName~TRC_06"` | ❌ W0 | ⬜ pending |
| TRC-07 | Resolver unavailable/hang/401/403/malformed -> 503, alarm where specified, zero rows | integration | `--filter "FullyQualifiedName~TRC_07"` | ❌ W0 | ⬜ pending |
| TRC-08 | Positive cache hit; 404 never cached; 503/401/403 never cached | unit | `--filter "FullyQualifiedName~TRC_08"` | ❌ W0 | ⬜ pending |
| TRC-09 | Source `fixture` recorded; CHECK rejects others; production + fixture resolver refuses start | integration | `--filter "FullyQualifiedName~TRC_09"` | ❌ W0 | ⬜ pending |
| IDN-05 | Stub issues grants with all four fields; unregistered tenant/agent refused | unit | `--filter "FullyQualifiedName~IDN_05"` | ❌ W0 | ⬜ pending |
| IDN-06 | Token cache by `(tenant, agent, key_id)`; never outlives key (`ManualTimeProvider`) | unit | `--filter "FullyQualifiedName~IDN_06"` | ❌ W0 | ⬜ pending |
| IDN-07 | Claim disagreement -> discarded, 503, alarm, zero rows | unit + integration | `--filter "FullyQualifiedName~IDN_07"` | ❌ W0 | ⬜ pending |
| IDN-08 | `[OutboundPort]` reflection test; key only on popeye ports; sentinel scan at Trace level | architecture + integration | `--filter "FullyQualifiedName~IDN_08"` | ❌ W0 | ⬜ pending |
| Order | introspect -> FindByRun -> token -> resolve -> insert; replay/409/400 emit no token/resolve | integration | `--filter "FullyQualifiedName~TRC_ORDER"` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/Olive.Tests/ManualTimeProvider.cs`, `RecordingPorts.cs` (resolver/issuer/alarm recorders, fault doubles), `SentinelScan.cs`
- [ ] `OliveApiFactory` trace mode (auto-match default for legacy tests, explicit for Phase 3 tests) and recording decorators on all ports
- [ ] Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` for migration 0006
- [ ] New `StartupGuardTests` case: production + `FixtureTraceResolver` / `StubTokenIssuer`, including behind a `CachingTraceResolver` decorator
- [ ] Contract examples for changed submit/GET response schemas; `ContractSchemaTests` additions
- [ ] No framework install needed

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Popeye token-issuance hand-off and JCS golden set coordination recorded | IDN-05, TRC-06 | Cross-team coordination, not code | Confirm STATE.md / contract README lists the open items |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 90s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
