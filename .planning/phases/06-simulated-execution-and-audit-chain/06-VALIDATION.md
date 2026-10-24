---
phase: 6
slug: simulated-execution-and-audit-chain
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-10-23
audited: 2026-10-24
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
| 06-01-01 | 01 | 1 | EXE-02, EXE-04 | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~ExecutionLedgerTests\|FullyQualifiedName~MigrationRunnerTests\|FullyQualifiedName~DB_02"` | ✅ green |
| 06-01-02 | 01 | 1 | EXE-02, EXE-06 | integration | `--filter "FullyQualifiedName~PrivilegeMatrixTests\|FullyQualifiedName~RlsPatternTests\|FullyQualifiedName~TenantIsolationAblationTests"` | ✅ green |
| 06-01-03 | 01 | 1 | EXE-04 | contract | `--filter "FullyQualifiedName~ContractSchemaTests"` | ✅ green |
| 06-02-01 | 02 | 2 | EXE-01..05 | unit + architecture | `--filter "FullyQualifiedName~ExecutionServiceTests\|Category=Architecture"` | ✅ green |
| 06-02-02 | 02 | 2 | EXE-04 | unit + architecture | `--filter "FullyQualifiedName~SimulatedActionExecutorTests\|FullyQualifiedName~FND_02\|FullyQualifiedName~REQ_ACT_04\|FullyQualifiedName~FND_05"` | ✅ green |
| 06-02-03 | 02 | 2 | EXE-03 | unit | `--filter "FullyQualifiedName~CatalogExecutionCheckTests"` | ✅ green |
| 06-03-01 | 03 | 3 | EXE-02 | integration | `--filter "FullyQualifiedName~TenantUnitOfWorkTests\|Category=Architecture"` | ✅ green |
| 06-03-02 | 03 | 3 | EXE-04 | full suite | `dotnet test` | ✅ green |
| 06-03-03 | 03 | 3 | EXE-01 | integration | `--filter "FullyQualifiedName~ExecutionEndpointTests\|FullyQualifiedName~Queue\|FullyQualifiedName~Beat4"` | ✅ green |
| 06-04-01 | 04 | 4 | EXE-05 | architecture | `--filter "FullyQualifiedName~FND_05"` | ✅ green |
| 06-04-02 | 04 | 4 | EXE-02 | integration | `--filter "FullyQualifiedName~ExecutionConcurrencyTests"` | ✅ green |
| 06-04-03 | 04 | 4 | EXE-03, EXE-05 | integration | `--filter "FullyQualifiedName~ExecutionFailureTests"` | ✅ green |
| 06-05-01 | 05 | 5 | EXE-01 | unit + integration | `--filter "FullyQualifiedName~ReviewerTenantMapTests\|FullyQualifiedName~StartupGuardTests"` | ✅ green |
| 06-05-02 | 05 | 5 | EXE-01, EXE-03, EXE-05 | full suite | `dotnet test` | ✅ green |
| 06-06-01 | 06 | 5 | EXE-06 | integration | `--filter "FullyQualifiedName~ExecutionChainViewTests"` | ✅ green |
| 06-06-02 | 06 | 5 | IDN-08 | integration | `--filter "FullyQualifiedName~ContainmentTests"` | ✅ green |
| 06-07-01 | 07 | 6 | EVD-02, EXE-06 | demo | `--filter "Demo=beat2"` | ✅ green |
| 06-07-02 | 07 | 6 | EVD-02 | demo | `bash build/demo-beat2.sh` | ✅ green |
| 06-08-01 | 08 | 1 | EXE-01, EXE-04 | integration | `--filter "FullyQualifiedName~EXE_04_failing_startup_guard_prevents_sweep_execution"` | ✅ green |
| 06-08-02 | 08 | 1 | EXE-01, EXE-04 | integration | `--filter "FullyQualifiedName~ExecutionSweepTests"` | ✅ green |
| 06-09-01 | 09 | 1 | EXE-03 | unit + integration | `--filter "FullyQualifiedName~EXE_03_expiry\|FullyQualifiedName~EXE_03_expired"` | ✅ green |
| 06-09-02 | 09 | 1 | EXE-05 | unit + integration | `--filter "FullyQualifiedName~EXE_05"` | ✅ green |

All `--filter` commands are `dotnet test tests/Olive.Tests --filter ...`. Wave 0 test files are created inside the task that first needs them (each task's own automated verify), so no separate Wave 0 plan is required.

Requirement-level targets from RESEARCH.md:

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| EXE-01 | Approve then execution runs after commit; reject does not; decide stays 200 when execution faults | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~EXE_01"` | ✅ | ✅ green |
| EXE-01 | Startup sweep executes seeded `approved` proposals in each mapped tenant | integration | `--filter "FullyQualifiedName~EXE_01_sweep"` | ✅ | ✅ green |
| EXE-02 | N concurrent attempts give one started row, one finished row, one JSONL line, identical results | integration | `--filter "FullyQualifiedName~EXE_02"` | ✅ | ✅ green |
| EXE-02 | Ledger INSERT-only for `olive_app` (privilege matrix) | integration | `--filter "FullyQualifiedName~DB_04"` | ✅ | ✅ green |
| EXE-03 | `proposal_expired` / `catalog_invalid` refusals: finished row, correct state, no JSONL line | integration | `--filter "FullyQualifiedName~EXE_03"` | ✅ | ✅ green |
| EXE-04 | Rows and JSONL say `simulated`; DB rejects other mode; architecture tests green | integration + architecture | `--filter "FullyQualifiedName~EXE_04\|Category=Architecture"` | ✅ | ✅ green |
| EXE-05 | Fault between ledger write and state update → `execution_failed`, `execution_in_doubt`, one JSONL line | integration | `--filter "FullyQualifiedName~EXE_05"` | ✅ | ✅ green |
| EXE-06 | One SELECT on `security_invoker` view returns full chain; cross-tenant zero rows | integration | `--filter "FullyQualifiedName~EXE_06"` | ✅ | ✅ green |
| EVD-02 | Beat 2 transcript written and asserted | demo | `build/demo-beat2.sh` | ✅ | ✅ green |
| IDN-08 carry-over | JSONL text added via `SentinelScan.AddSource`, asserted clean | integration | `--filter "FullyQualifiedName~ContainmentTests"` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `ExecutionServiceTests` (unit, in-memory fakes of the three ports) covering the classification table
- [x] `ExecutionLedgerTests` and `ExecutionChainViewTests` (integration)
- [x] `FaultingExecutionStore` decorator and a gate-blocking executor double in `tests/Olive.Tests`
- [x] `Beat2DemoTests.cs` plus `build/demo-beat2.sh` (with a script-hygiene test like `EVD_03_runner_script_filters_to_the_beat4_trait`)
- [x] `OliveApiFactory` default executor output path and cleanup
- [x] Update `MigrationRunnerTests`, `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`, `ContractSchemaTests`

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

**Approval:** approved 2026-10-24

---

## Validation Audit 2026-10-24

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |

Every requirement (EXE-01..06, EVD-02, IDN-08 carry-over) maps to `EXE_`/`EVD_`/`IDN_` tests that exist and run green. The full suite passed 1216/1216 at `5b14c91`, the last code commit. Gap-closure plans 06-08 and 06-09 are added to the map. The review fixes (WR-01..03) are covered by `EXE_05_shutdown_during_sweep_attempt_drains_and_never_leaves_in_doubt`, `EXE_05_*_raises_execution_in_doubt_alarm`, `EXE_03_definite_refusals_raise_no_alarm` and `EXE_02_lock_timeout_is_returned_when_no_attempt_can_begin`. T-06-29 is covered by the Beat 2 sentinel token scan.

Flakiness note: `StartupGuardTests.QUE_02_host_refuses_start_on_role_drift` (Phase 2 test) failed once during Phase 6 verification and passed on rerun; not a Phase 6 requirement, tracked for observation.
