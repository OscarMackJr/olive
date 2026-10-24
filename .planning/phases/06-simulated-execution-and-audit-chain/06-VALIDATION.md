---
phase: 6
slug: simulated-execution-and-audit-chain
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-23
---

# Phase 6 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 on net10.0, Testcontainers postgres:17-alpine |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~Execution"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~180 seconds (full, Testcontainers) |

---

## Sampling Rate

- **After every task commit:** Run the quick run command (or the task's narrower `--filter`)
- **After every plan wave:** Run `dotnet test`
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** 180 seconds

---

## Per-Task Verification Map

Filled by the planner per task. Requirement-level targets from RESEARCH.md:

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| EXE-01 | Approve then execution runs after commit; reject does not; decide stays 200 when execution faults | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~EXE_01"` | ❌ W0 | ⬜ pending |
| EXE-01 | Startup sweep executes seeded `approved` proposals in each mapped tenant | integration | `--filter "FullyQualifiedName~EXE_01_sweep"` | ❌ W0 | ⬜ pending |
| EXE-02 | N concurrent attempts give one started row, one finished row, one JSONL line, identical results | integration | `--filter "FullyQualifiedName~EXE_02"` | ❌ W0 | ⬜ pending |
| EXE-02 | Ledger INSERT-only for `olive_app` (privilege matrix) | integration | `--filter "FullyQualifiedName~DB_04"` | extend | ⬜ pending |
| EXE-03 | `proposal_expired` / `catalog_invalid` refusals: finished row, correct state, no JSONL line | integration | `--filter "FullyQualifiedName~EXE_03"` | ❌ W0 | ⬜ pending |
| EXE-04 | Rows and JSONL say `simulated`; DB rejects other mode; architecture tests green | integration + architecture | `--filter "FullyQualifiedName~EXE_04\|Category=Architecture"` | ❌ W0 | ⬜ pending |
| EXE-05 | Fault between ledger write and state update → `execution_failed`, `execution_in_doubt`, one JSONL line | integration | `--filter "FullyQualifiedName~EXE_05"` | ❌ W0 | ⬜ pending |
| EXE-06 | One SELECT on `security_invoker` view returns full chain; cross-tenant zero rows | integration | `--filter "FullyQualifiedName~EXE_06"` | ❌ W0 | ⬜ pending |
| EVD-02 | Beat 2 transcript written and asserted | demo | `build/demo-beat2.sh` | ❌ W0 | ⬜ pending |
| IDN-08 carry-over | JSONL text added via `SentinelScan.AddSource`, asserted clean | integration | `--filter "FullyQualifiedName~ContainmentTests"` | extend | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `ExecutionServiceTests` (unit, in-memory fakes of the three ports) covering the classification table
- [ ] `ExecutionLedgerTests` and `ExecutionChainViewTests` (integration)
- [ ] `FaultingExecutionStore` decorator and a gate-blocking executor double in `tests/Olive.Tests`
- [ ] `Beat2DemoTests.cs` plus `build/demo-beat2.sh` (with a script-hygiene test like `EVD_03_runner_script_filters_to_the_beat4_trait`)
- [ ] `OliveApiFactory` default executor output path and cleanup
- [ ] Update `MigrationRunnerTests`, `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`, `ContractSchemaTests`

---

## Manual-Only Verifications

All phase behaviors have automated verification.

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 180s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
