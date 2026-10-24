---
phase: 6
slug: simulated-execution-and-audit-chain
status: planned
nyquist_compliant: true
wave_0_complete: true
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

| Task ID | Plan | Wave | Requirement | Test Type | Automated Command | Status |
|---------|------|------|-------------|-----------|-------------------|--------|
| 06-01-01 | 01 | 1 | EXE-02, EXE-04 | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionLedgerTests\|FullyQualifiedName~MigrationRunnerTests\|FullyQualifiedName~DB_02"` | ⬜ pending |
| 06-01-02 | 01 | 1 | EXE-02, EXE-06 | integration | `--filter "FullyQualifiedName~PrivilegeMatrixTests\|FullyQualifiedName~RlsPatternTests\|FullyQualifiedName~TenantIsolationAblationTests"` | ⬜ pending |
| 06-01-03 | 01 | 1 | EXE-04 | contract | `--filter "FullyQualifiedName~ContractSchemaTests"` | ⬜ pending |
| 06-02-01 | 02 | 2 | EXE-01..05 | unit + architecture | `--filter "FullyQualifiedName~ExecutionServiceTests\|Category=Architecture"` | ⬜ pending |
| 06-02-02 | 02 | 2 | EXE-04 | unit + architecture | `--filter "FullyQualifiedName~SimulatedActionExecutorTests\|FullyQualifiedName~FND_02\|FullyQualifiedName~REQ_ACT_04\|FullyQualifiedName~FND_05"` | ⬜ pending |
| 06-02-03 | 02 | 2 | EXE-03 | unit | `--filter "FullyQualifiedName~CatalogExecutionCheckTests"` | ⬜ pending |
| 06-03-01 | 03 | 3 | EXE-02 | integration | `--filter "FullyQualifiedName~TenantUnitOfWorkTests\|Category=Architecture"` | ⬜ pending |
| 06-03-02 | 03 | 3 | EXE-04 | full suite | `dotnet test` | ⬜ pending |
| 06-03-03 | 03 | 3 | EXE-01 | integration | `--filter "FullyQualifiedName~ExecutionEndpointTests\|FullyQualifiedName~Queue\|FullyQualifiedName~Beat4"` | ⬜ pending |
| 06-04-01 | 04 | 4 | EXE-05 | architecture | `--filter "FullyQualifiedName~FND_05"` | ⬜ pending |
| 06-04-02 | 04 | 4 | EXE-02 | integration | `--filter "FullyQualifiedName~ExecutionConcurrencyTests"` | ⬜ pending |
| 06-04-03 | 04 | 4 | EXE-03, EXE-05 | integration | `--filter "FullyQualifiedName~ExecutionFailureTests"` | ⬜ pending |
| 06-05-01 | 05 | 5 | EXE-01 | unit + integration | `--filter "FullyQualifiedName~ReviewerTenantMapTests\|FullyQualifiedName~StartupGuardTests"` | ⬜ pending |
| 06-05-02 | 05 | 5 | EXE-01, EXE-03, EXE-05 | full suite | `dotnet test` | ⬜ pending |
| 06-06-01 | 06 | 5 | EXE-06 | integration | `--filter "FullyQualifiedName~ExecutionChainViewTests"` | ⬜ pending |
| 06-06-02 | 06 | 5 | IDN-08 | integration | `--filter "FullyQualifiedName~ContainmentTests"` | ⬜ pending |
| 06-07-01 | 07 | 6 | EVD-02, EXE-06 | demo | `--filter "Demo=beat2"` | ⬜ pending |
| 06-07-02 | 07 | 6 | EVD-02 | demo | `bash build/demo-beat2.sh` | ⬜ pending |

All `--filter` commands are `dotnet test tests/Olive.Tests --filter ...`. Wave 0 test files are created inside the task that first needs them (each task's own automated verify), so no separate Wave 0 plan is required.

Requirement-level targets from RESEARCH.md:

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

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 180s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
