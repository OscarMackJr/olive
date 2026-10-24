---
phase: 06
slug: simulated-execution-and-audit-chain
status: verified
threats_open: 0
asvs_level: 1
created: 2026-10-24
---

# Phase 06 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Audited against HEAD `d9f6319`, which includes the post-plan review fixes WR-01 (`70efcc5`), WR-02 (`3e0d0cd`) and WR-03 (`9892e76`).

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| app role to database | `olive_app` is the only runtime writer and must not be able to rewrite history | ledger rows (INSERT only) |
| reader role to chain view | `olive_reader` reads a view that joins approver rationale | audit chain incl. rationale (tenant-scoped) |
| application to executor port | Only allow-listed request fields cross | tenant, proposal, action, target, parameters, time |
| executor to filesystem | The JSONL file stands in for the target system | allow-listed JSONL line incl. `would_send` parameters |
| reviewer HTTP request to executor | An authenticated approve now causes a (simulated) side effect | proposal id, tenant id |
| host config to filesystem | The output path comes from the operator | `OLIVE_EXECUTOR_OUTPUT_PATH` |
| startup sweep to tenant data | A system actor works through tenants one at a time | approved proposal ids per tenant |
| host startup to executor write | Execution must happen only on a host that passed every guard | n/a |
| demo transcript to readers | A shareable artifact that prints audit data | chain row, JSONL line |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation / Evidence | Status |
|-----------|----------|-----------|-------------|------------------------|--------|
| T-06-01 | Tampering / Repudiation | execution_ledger | mitigate | `sql/migrations/0009_execution_ledger.sql:29-31` (revoke all; `grant select, insert` to olive_app; select to olive_reader); `tests/Olive.Tests/ExecutionLedgerTests.cs:79` asserts 42501 on UPDATE/DELETE/TRUNCATE; `PrivilegeMatrixTests.cs:23-26` | closed |
| T-06-02 | Tampering | duplicate execution | mitigate | `0009_execution_ledger.sql:16` `unique (tenant_id, proposal_id, phase)`; `ExecutionLedgerTests.cs:43` (23505); `ExecutionConcurrencyTests.cs:93` | closed |
| T-06-03 | Information Disclosure | execution_chain view | mitigate | `0009:35` `with (security_invoker = true)`; forced RLS on proposal/proposal_event (0001:39,45), queue_item (0008:89), execution_ledger (0009:25); view omits parameters/justification/canonical_body; `ExecutionChainViewTests.cs:90,102` | closed |
| T-06-04 | Tampering | execution_mode | mitigate | `0009:10` `check (execution_mode = 'simulated')`; `ExecutionSql.InsertLedger` hard-codes `'simulated'`; `ExecutionLedgerTests.cs:52` (23514) | closed |
| T-06-05 | Information Disclosure | reason column | mitigate | `0009:12` `reason ~ '^[a-z][a-z_]{0,63}$'`; `ExecutionLedgerTests.cs:70` | closed |
| T-06-06 | Information Disclosure | JSONL line | mitigate | `src/Olive.Application/IActionExecutor.cs` `ExecutionRequest` has no secret fields; `SimulatedActionExecutor.cs` writes fields one by one with `Utf8JsonWriter`; `SimulatedActionExecutorTests.cs:27` asserts the exact property set | closed |
| T-06-07 | Information Disclosure | failure reasons | mitigate | `ExecutionReasons.cs` constants; `ExecutionService.cs:59` `catch (Exception)` with no binding, so exception text is never read; no `.Message`/`ToString()` in ExecutionService, ExecutionSweep, QueueEndpoints or PostgresExecutionStore (grep) | closed |
| T-06-08 | Tampering | double target write | mitigate | `ExecutionService.cs:23` (finished means replay), `:26-31` (started means in doubt, no executor call); `ExecutionServiceTests.cs:109,121` | closed |
| T-06-09 | Elevation of Privilege | real network write | mitigate | `ArchitectureTests.cs:68` FND_02 (Executor.Simulated assembly checked), `:90` REQ_ACT_04, `:119` package allowlist; no csproj or lockfile change in phase 6 (git log) | closed |
| T-06-10 | Denial of Service | interleaved JSONL lines | mitigate | `SimulatedActionExecutor.cs` `SemaphoreSlim(1,1)` plus one `WriteAsync` per line; registered as singleton (`Program.cs:41`); `SimulatedActionExecutorTests.cs:64` 20-way test | closed |
| T-06-11 | Tampering | double execution via concurrent approve | mitigate | `ExecutionSql.LockExecution` `pg_advisory_xact_lock` held on its own connection (`TenantUnitOfWork.AcquireAdvisoryLockAsync`, `PostgresExecutionStore.BeginAttemptAsync`) plus the unique phase key; `ExecutionEndpointTests.cs:118` repeat approve gives one JSONL line | closed |
| T-06-12 | Elevation of Privilege | execution inside approve transaction | mitigate | `QueueEndpoints.cs:149-150` hook runs after `DecideAsync` returns, on new connections; `:181` returns unless `Decision == "approve"`; `ExecutionEndpointTests.cs:104` reject does not execute | closed |
| T-06-13 | Information Disclosure | logs and responses on execution fault | mitigate | `QueueEndpoints.cs:193-196` bare catch, static template (ids as structured fields only, WR-03); response mapped from `DecisionResult` only; `ExecutionEndpointTests.cs:90` shape unchanged; `ExecutionFailureTests.cs:71` sentinel absent from the response, logs and rows | closed |
| T-06-14 | Denial of Service | stuck lock holder | mitigate | HEAD: `TenantUnitOfWork.cs:202-214` `set local lock_timeout`, command timeout = lock timeout + 5 s (WR-02), 55P03 returns null; `PostgresExecutionStore.DefaultLockTimeout` = 5 s; `ExecutionService.cs:17` returns `LockTimeout`; `ExecutionConcurrencyTests.cs:70` (1 s timeout); WR-02 test `EXE_02_lock_timeout_outcome_is_deterministic_when_command_timeout_is_shorter` | closed |
| T-06-15 | Tampering | misconfigured output path | mitigate | `StartupGuards.CheckExecutorOutput` (static reason, catch-all rethrows a static message) called at `StartupGuardFilter.cs:19`; `StartupGuardTests.cs:560,570,578` (message never contains the path) | closed |
| T-06-16 | Tampering | client disconnect mid-execution | mitigate | HEAD: `QueueEndpoints.cs:187` and `ExecutionSweep.cs:89` both pass `CancellationToken.None` (sweep added in WR-01); `ExecutionSweepTests.cs:215` shutdown drains without in-doubt | closed |
| T-06-17 | Tampering | concurrent double write | mitigate | `ExecutionConcurrencyTests.cs:45` 8-way race with GateExecutor: 1 executed, 7 replayed, 1 executor call, 1 started, 1 finished, 1 JSONL line | closed |
| T-06-18 | Tampering | crash between write and state update | mitigate | `ExecutionService.cs:26-31` in doubt becomes `execution_failed`, executor not called; `ExecutionFailureTests.cs:144` target never rewritten (single JSONL line, 1 call) | closed |
| T-06-19 | Information Disclosure | exception text in rows, logs, responses | mitigate | `ExecutionFailureTests.cs:63-68,71,144,176` sentinel exceptions (FaultingExecutionStore, ThrowingExecutor) checked against logs, ledger and proposal_event rows, and responses | closed |
| T-06-20 | Repudiation | stale approval replayed after expiry or catalog change | mitigate | `ExecutionService.cs:39-49`; `CatalogExecutionCheck.cs` (current release plus schema re-validation); `ExecutionServiceTests.cs:134,158`; `ExecutionFailureTests.cs:88,108`; `ExecutionSweepTests.cs:155,173` | closed |
| T-06-21 | Elevation of Privilege | cross-tenant enumeration | mitigate | `ExecutionSweep.cs:57` `IReviewerTenantMap.AllTenants`; every ExecutionSql statement and `ProposalSql.UpdateState` has `tenant_id = @tenant_id`; tenant set by `TenantUnitOfWork` (transaction-local GUC); no SECURITY DEFINER or RLS change in 0009, and 0001-0008 untouched in phase 6 | closed |
| T-06-22 | Tampering | re-write after crash | mitigate | `ExecutionService.cs:26-31`; `ExecutionSweepTests.cs:135` restart resolves in doubt, JSONL empty on second host | closed |
| T-06-23 | Denial of Service | sweep fault blocks start | mitigate | HEAD: sweep runs via `Task.Run` from `ApplicationStarted` (`ExecutionSweep.cs:23`); outer catch-all (`:37`); per-tenant and per-proposal catch (`:77,:95`); `ExecutionSweepTests.cs:251` | closed |
| T-06-24 | Information Disclosure | logs on sweep fault | mitigate | `ExecutionSweep.cs:61,79,92,97` static templates with id fields only; exception never bound; `ExecutionSweepTests.cs:251` sentinel absent from logs | closed |
| T-06-25 | Information Disclosure | cross-tenant view read | mitigate | security_invoker plus forced RLS; `ExecutionChainViewTests.ChainSelect` has a `tenant_id = @tenant_id` predicate; `ExecutionChainViewTests.cs:102` zero rows | closed |
| T-06-26 | Information Disclosure | secrets in JSONL | mitigate | `SentinelScan.AddJsonl`; `ContainmentTests.cs:191-202` scans session key, token and rationale over JSONL, logs, responses and ledger rows; `:216` justification absent from the JSONL and the view row | closed |
| T-06-27 | Information Disclosure | justification or parameters via the view | mitigate | `ExecutionChainViewTests.cs:90` column-list test | closed |
| T-06-28 | Repudiation | unfinished attempts invisible | mitigate | View anchored on the started row with left join to finished (`0009`); `ExecutionChainViewTests.cs:136` | closed |
| T-06-29 | Information Disclosure | transcript content | mitigate | `Beat2DemoTests` builds its factory with `ContainmentTests.FixedTokenIssuer` and the sentinel `TokenSentinel`. It asserts that a token-issuer call happened, then scans the transcript for `AlphaKey`, `TokenSentinel` and `JustSentinel` (`5b14c91`). The view row omits parameters. | closed |
| T-06-30 | Information Disclosure | rationale in transcript | accept | See Accepted Risks (AR-02) | closed |
| T-06-31 | Repudiation | demo implying live trace verification | mitigate | `Beat2DemoTests.cs:36-37` transcript states that fixtures were used, not a live hometown; `:87` asserts "fixture" is in the transcript | closed |
| T-06-SC | Tampering | package installs | accept | See Accepted Risks (AR-01); verified that no csproj or lockfile changed in phase 6 | closed |
| T-06-08-01 | Elevation of Privilege | sweep before StartupGuardFilter | mitigate | `ExecutionSweep.cs:23` starts only on `ApplicationStarted`; `StartupGuardFilter` runs while the pipeline is built; `ExecutionSweepTests.cs:192` | closed |
| T-06-08-02 | Information Disclosure | sweep fault logging | mitigate | Same as T-06-24; `ExecutionSweepTests.cs:251` | closed |
| T-06-08-03 | Denial of Service | background sweep fault | mitigate | `ExecutionSweep.cs:31-45` `Completion` only ever `TrySetResult` (never faults); `StopAsync` swallows OCE and completes (`:105-118`) | closed |
| T-06-08-04 | Tampering | duplicate guard logic drifting | accept | See Accepted Risks (AR-03); verified ExecutionSweep calls no guard | closed |
| T-06-09-01 | Repudiation | ledger reason on executor exception | mitigate | `ExecutionService.cs:59-65` records `execution_in_doubt` once the started row exists; `ExecutionServiceTests.cs:179`; `ExecutionFailureTests.cs:176` | closed |
| T-06-09-02 | Elevation of Privilege | expired proposal executed if lifecycle table changes | mitigate | `ExecutionService.cs:39-42` expiry check does not depend on `_canTransition`; fallback `ExecutionFailed`; `ExecutionServiceTests.cs:146` | closed |
| T-06-09-03 | Information Disclosure | exception text in ledger | mitigate | `ExecutionReasons` constants only; `ExecutionServiceTests.cs:179` asserts no `SECRET`; DB regex check (T-06-05) | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Open Threats

| Threat ID | Severity | Expected Mitigation | Gap | Files Searched | Suggested Action |
|-----------|----------|---------------------|-----|----------------|------------------|
None. T-06-29 was closed on 2026-10-24 by `5b14c91`, using Option 1 (sentinel token via `FixedTokenIssuer`, added to `AssertAbsent`). The original gap record is kept below.

| ~~T-06-29~~ (closed) | Low (non-blocking under `block_on: high`) | Sentinel scan of the transcript for session key, **token** and justification sentinel | `Beat2DemoTests` called `scan.AssertAbsent(ApiProposalTests.AlphaKey, JustSentinel)` and does not include a token. It uses the default `OliveApiFactory` (a `StubTokenIssuer` behind `RecordingTokenIssuer`), so the test never knows the token value. Plan 06-07 task text softened this to "the service token if exposed". The threat register does not include that qualifier. | `tests/Olive.Tests/Beat2DemoTests.cs`, `tests/Olive.Tests/SentinelScan.cs`, `tests/Olive.Tests/OliveApiFactory.cs`, `tests/Olive.Tests/ContainmentTests.cs` | Option 1: build the Beat 2 factory with a `FixedTokenIssuer` and a sentinel token (as `ContainmentTests.Factory` does) and add that token to `AssertAbsent`. Option 2: record T-06-29 (token part) as an accepted risk, because the transcript contains only the chain-view row and the allow-listed JSONL line, and token absence from both is already proven by `ContainmentTests.cs:191`. |

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-01 | T-06-SC | No package installs in any phase 6 plan. Verified: the last change to a csproj or `packages.lock.json` is `8123d01` (phase 05-05). | Phase 6 plan threat register (06-01..06-07) | 2026-10-24 |
| AR-02 | T-06-30 | The approver rationale belongs to the audit chain by design (D-14). The Beat 2 demo uses a literal rationale that is not a sentinel. Rationale leakage is scanned separately in the JSONL, logs and responses (`ContainmentTests.cs:191`). | Phase 6 plan 06-07 threat register | 2026-10-24 |
| AR-03 | T-06-08-04 | Startup guards live only in `StartupGuardFilter`. The sweep depends on `ApplicationStarted` ordering, not on duplicated guard logic. Verified that `ExecutionSweep.cs` calls no guard. | Phase 6 plan 06-08 threat register | 2026-10-24 |

*Accepted risks do not resurface in future audit runs.*

---

## Unregistered Flags

None. 06-01..06-07 SUMMARY `## Threat Flags` sections all say "None". 06-08 and 06-09 SUMMARY files have no `## Threat Flags` section.

Informational (each one maps to an existing threat, so none is a new flag):
- WR-03 added `IOperationalAlarm` raises (code `execution_in_doubt`, component `executor`) and structured `proposal_id`/`tenant_id` log fields. They map to T-06-13/T-06-24. Identifiers only; no exception text or parameters.
- WR-04 (skipped): the execution-record v1 JSON Schema is looser than the DB `execution_ledger_shape` check. The DB still enforces the strict shape, so this is a documentation and consumer-validation gap only. It is outside the threat register.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-24 | 39 | 38 | 1 | gsd-security-auditor (Claude) |
| 2026-10-24 | 39 | 39 | 0 | orchestrator: T-06-29 closed by `5b14c91` (user chose to fix the test). Beat2 + Containment 12/12; full suite 1216/1216 |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-24
