# Phase 6: Simulated Execution and Audit Chain - Research

**Researched:** 2026-10-23
**Domain:** Exactly-once simulated execution after an approval commit, in .NET 10 with PostgreSQL 17 (Npgsql, Dapper, forced RLS)
**Confidence:** HIGH on codebase facts and the design; MEDIUM on three Postgres and Npgsql behaviours tagged [ASSUMED]

## Summary

Phase 6 adds one INSERT-only ledger table, one `security_invoker` view, an executor port with a simulated JSONL implementation, an orchestrating `ExecutionService`, an endpoint hook, a startup sweep, and the Beat 2 demo. No new NuGet package is needed. Everything builds on Dapper, Npgsql, `TenantUnitOfWork`, the `ProposalSql` and `QueueSql` patterns, and the Phase 5 `Approved -> Executed | ExecutionFailed | Expired` edges already in `ProposalStates`.

**The central finding is a design conflict inside D-02, D-07 and D-12.** D-07 requires the `started` row to be committed before the target write. D-02 requires a `pg_advisory_xact_lock` held for each attempt. A transaction-scoped lock disappears at COMMIT, so if the lock lived in the same transaction as the `started` insert, the lock would be released before the JSONL write. A second attempt could then see `started` without `finished` and wrongly classify a live in-flight attempt as in doubt. Resolution: use a **lock-holder transaction** on connection A. It stays open for the whole attempt and holds `pg_advisory_xact_lock(tenant, proposal)`. All ledger and state work runs in separate short transactions on connection B. A crash kills connection A, which releases the lock, so "I hold the lock and see `started` without `finished`" proves the previous attempt is dead. That proof is what makes D-08 sound.

The sweep must enumerate tenants from source-controlled config (`ReviewerTenantMap`). A `SECURITY DEFINER` function does not work here, because FORCE RLS binds the table owner too and the roles are `NOBYPASSRLS`.

**Primary recommendation:** Add `IActionExecutor`, `IExecutionStore` and `IActionCatalogCheck` ports in `Olive.Application`. Put the orchestrator `ExecutionService` in `Olive.Application`. Put the JSONL executor in `Olive.Executor.Simulated`, the SQL in `Olive.Persistence`, and the catalog re-check in `Olive.Catalog`. Add migration `0009_execution_ledger.sql`. Call the service from `QueueEndpoints.DecisionAsync` after an approve commits, with `CancellationToken.None`.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** The executor runs in the approve request, after commit. The decision endpoint commits the approval (the Phase 5 atomic decide is unchanged), then calls the executor in the same request on a fresh connection and transaction. The decide response returns the decision, as in Phase 5. A failed execution doesn't change that 200, and execution failure is never part of the approve transaction. Rejects don't trigger execution.
- **D-02:** Exactly once rests on two things. `pg_advisory_xact_lock` keyed on (tenant_id, proposal_id) is held for each attempt (the Phase 4 `LockRate` pattern in `ProposalSql`). Unique ledger keys back the lock up (D-07). The losing attempt, whether in the request path or the sweep, reads the existing ledger and returns the first result without writing the target.
- **D-03:** The sweep runs only at startup (Q5), as a hosted service. It finds `approved` proposals with no finished ledger row and runs each through the same executor path, under the same lock. Because RLS is forced and `olive.tenant_id` has no `missing_ok`, the sweep has to enumerate tenants and set the tenant context for each one. How to get the tenant list is left to research and planning. There is no periodic timer.
- **D-04:** The result is surfaced only as state. `GET /v1/proposals/{id}` already shows `executed`, `execution_failed` or `expired`. No fields are added to the proposal response. Full execution detail is available only through the chain view (D-14).
- **D-05:** An invalid catalog entry at execution gives terminal `execution_failed` with the static reason `catalog_invalid` (Q16 re-check). It writes a finished ledger row with outcome failed and a `proposal_event`, and does not write the target.
- **D-06:** A proposal that has expired by execution time moves to `expired`, through the existing `Approved -> Expired` edge, consistent with POL-08 and the Phase 5 lazy expiry. It writes a finished ledger row with outcome failed and reason `proposal_expired`, plus a `proposal_event`. It does not write the target. Entitlements are not re-checked (Q16).
- **D-07:** The ledger has at most two INSERT-only rows per proposal: started and finished. A `started` row is committed before the target write. A `finished` row is written after it, carrying an outcome (`succeeded` | `failed`), a static reason code and the result reference. Uniqueness is on (tenant_id, proposal_id, phase). Every attempt, including refusals at execution such as `catalog_invalid` and `proposal_expired`, ends with a finished row. A refusal can write the started and finished rows together. The proposal state change (`approved` -> `executed` | `execution_failed` | `expired`) and its `proposal_event` commit in the same transaction as the finished row. EXE-02's "a unique ledger row per (tenant_id, proposal_id)" is read as one row per phase; the planner should note this reading in the requirement trace.
- **D-08:** An in-doubt attempt fails closed and the target is never written again. A `started` row without a `finished` row means the target write may or may not have happened. The next attempt, or the startup sweep, writes a finished row with outcome failed and reason `execution_in_doubt` and moves the proposal to terminal `execution_failed` (Q6). It never writes the target again, and it does not read the JSONL back. A fault-injection test covers this (EXE-05).
- **D-09:** Failure reasons are static codes, never exception text or parameter values.
- **D-10:** Each JSONL line holds `proposal_id`, `tenant_id`, `action_id`, `target_system`, `execution_mode = "simulated"`, `executed_at`, and `would_send` (the validated proposal parameters). It never contains the session key, tokens, the rationale or the justification.
- **D-11:** Output goes to one file at a configured path, with `tenant_id` on every line. The path comes from configuration (for example `OLIVE_EXECUTOR_OUTPUT_PATH`) with a dev default under a gitignored folder. Tests point it at a temp file. A missing or unwritable path fails startup via a startup guard.
- **D-12:** The order of writes is: started ledger row committed, then the JSONL line appended, flushed and fsynced, then the finished row and state change committed. A crash before the fsync finishes leaves the attempt in doubt (D-08).
- **D-13:** The simulated executor is allowed in production and always labelled. Every ledger row and every JSONL line says `execution_mode = "simulated"`. A startup log line states it. It is not barred like the fixture resolvers. The executor sits behind an `IActionExecutor` interface that a real executor will implement later (Decision section 1).
- **D-14:** The `security_invoker` chain view joins, keyed by execution record: the proposal (id, tenant, agent, on_behalf_of, purpose, action_id, state, `olive:proposal/<id>` reference); trace verification (trace_ref, `trace_verification`, `trace_schema_version`, `proposal_hash`); the policy decision from the `policy_evaluated` event; the approver (`decided_by`, `auth_source`, `decided_at`, rationale text); the started and finished ledger rows (mode, outcome, reason, times). It does not include parameters or justification.
- **D-15:** `olive_reader` and `olive_app` get SELECT on the view. The privilege-matrix, RLS and ablation tests are extended to cover `execution_ledger` and the view. For `olive_app`, the ledger is INSERT plus the SELECT it needs, with no UPDATE or DELETE (Decision section 13).
- **D-16:** The beat 2 demo is an xUnit test with a transcript, like Beat 4. It carries the `Demo=beat2` trait, runs on Testcontainers, and is run by `build/demo-beat2.sh`. The transcript prints the exact single SELECT against the chain view, run as `olive_reader`, with its one result row, followed by the JSONL line. It states plainly that trace verification used fixtures (Decision section 6 exit note).
- **D-17:** The IDN-08 sentinel scan is extended to cover the JSONL output and the beat 2 transcript through `SentinelScan.AddSource`. It checks the session key, tokens and the rationale sentinel.

### Claude's Discretion
- How the startup sweep enumerates tenants under forced RLS.
- Ledger table and column names, the migration number (next after 0008), the view name, and the reason-code constants (`catalog_invalid`, `proposal_expired` and `execution_in_doubt` at minimum).
- Whether `contracts/execution-record/v0` is replaced by a v1 schema matching the ledger and view row, with valid and invalid examples (probably yes, following the Phase 5 queue-item v0 -> v1 precedent).
- How the fault-injection seam is built, keeping test doubles out of production composition.
- Where the executor service and the store live (`Olive.Executor.Simulated` already exists as an empty project referencing Application and Domain).
- How the decide endpoint calls the executor after commit, provided the approve transaction has already committed.

### Deferred Ideas (OUT OF SCOPE)
- A periodic re-execution sweep on a timer.
- Optional execution fields on the proposal API response.
- Reconciling an in-doubt attempt by reading the target back.
- The UI audit page that reads the chain view (Phase 7).
- Also out of scope: the Razor UI and real Entra OIDC (Phase 7), the `identity_review` kind (Phase 8), any real executor or write-capable credential (charter 3.3, REQ-ACT-04), any new API endpoint (Q18).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| EXE-01 | Executed after approval commits; startup sweep | Endpoint hook after `DecideAsync` returns `Decided`; `ExecutionSweep` hosted service; tenant list from `ReviewerTenantMap` |
| EXE-02 | Idempotent on `proposal_id`; unique ledger row; per-proposal lock | Lock-holder transaction with `pg_advisory_xact_lock`; `unique (tenant_id, proposal_id, phase)`; loser reads the finished row |
| EXE-03 | Catalog validity and expiry re-checked just before execution | `IActionCatalogCheck` (Catalog implements); `TimeProvider` expiry check; both under the lock, before `started` |
| EXE-04 | `execution_mode = "simulated"` ledger rows and JSONL; no network write | CHECK constraint on the ledger; JSONL executor in `Olive.Executor.Simulated`; existing `REQ_ACT_04` and `FND_02` architecture tests stay green |
| EXE-05 | Executor failure is terminal `execution_failed`; fault-injection test | Static reason codes; `started`-without-`finished` resolves to `execution_in_doubt`; decorator seam on the attempt object |
| EXE-06 | Chain queryable through a `security_invoker` view | View `execution_chain` created `WITH (security_invoker = true)`; granted to `olive_reader` and `olive_app` |
| EVD-02 | Scripted beat 2 demo | `Beat2DemoTests` plus `build/demo-beat2.sh`, copying the Beat 4 pattern |
</phase_requirements>

## Project Constraints (from CLAUDE.md and AGENTS.md)

CLAUDE.md describes the early scaffold. The real stack is the multi-project solution under `src/` (Application, Catalog, Contracts, Domain, Executor.Simulated, Host, Integrations.*, Migrator, Persistence). Directives that still bind:
- No `HttpClient` or network assembly in any `src` project. `ArchitectureTests` enforces `REQ_ACT_04` and `FND_02` on built assemblies, and `BannedSymbols.txt` is an error-level analyzer. The JSONL executor must use only `System.IO`.
- Queue payloads are references only. This phase adds no queue write.
- Tenant predicates in every application query (`tenant_id = @tenant_id`) and forced RLS. Both are kept.
- `contracts/vendored/vocabulary.yaml` is never edited.
- New behaviour cites a requirement ID in the test name (`EXE_02_...`).
- Policy and failure detail text is static and never includes parameter values (D-09).
- Time is injected (`TimeProvider`), never read inside services.
- Business outcomes are result values, never exceptions, inside `TenantUnitOfWork`. A thrown exception rolls the transaction back.
- Contracts get paired `.valid.json` and `.invalid.json` examples.
- Migrations are forward-only, numbered, and checksum-pinned. 0001-0008 must stay unchanged.
- Commit trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Trigger execution after approve | API / Backend (Host endpoint) | Application `ExecutionService` | D-01. The service stays free of HTTP, and the endpoint decides when to call it. |
| Execution orchestration (checks, ordering, result classification) | Application | — | Pure logic against ports. The `QueueService` result-union style applies. |
| Per-proposal lock, ledger rows, state CAS, events | Database / Storage (Persistence) | — | Raw SQL belongs only in Persistence. Unique keys back up the lock. |
| JSONL target write | Executor.Simulated | — | `IActionExecutor` implementation. File I/O only. |
| Catalog validity re-check | Catalog | Application port | Only `Olive.Catalog` can see `CatalogSet`. Executor and Application cannot reference it (`FND_01`). |
| Startup sweep | Host (hosted service) | Persistence (list approved) | Hosted service composition lives in the Host. |
| Tenant enumeration for the sweep | Catalog config (`ReviewerTenantMap`) | — | Source-controlled and keeps both isolation layers. |
| Chain view | Database / Storage | — | `security_invoker`, so RLS binds the caller. |
| Output-path guard | Host (`StartupGuards`) | — | Fail-closed startup. |

## Standard Stack

### Core (all already in the solution; no new packages)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Npgsql | as pinned in `packages.lock.json` | Postgres access; hosts the lock-holder connection | Already the only data driver. |
| Dapper | as pinned | SQL mapping, `CommandDefinition` pattern | Already used in `PostgresQueueStore`. |
| System.IO (`FileStream`) | BCL | Append, flush to disk (`Flush(true)`) | No dependency, and nothing network-capable. |
| System.Text.Json (`Utf8JsonWriter`) | BCL | Build the JSONL line | Avoids serializing the whole proposal; field-by-field writing is an allow-list. |
| xunit.v3 and Testcontainers (postgres:17-alpine) | as pinned | Tests and the demo | Existing fixtures (`PostgresFixture`, `TestDatabase`). |

**Installation:** none. The `FND_02_direct_packages_match_allowlist_in_lockfiles` test fails if any direct package is added to `Olive.Executor.Simulated` or `Olive.Application`. Do not add one.

## Package Legitimacy Audit

No external package is installed in this phase.

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

## Architecture Patterns

### System Architecture Diagram

```
 reviewer POST /v1/queue/items/{id}/decision {approve, rationale}
        |
        v
 QueueEndpoints.DecisionAsync --> QueueService.DecideAsync --> PostgresQueueStore.DecideAsync
        |                                  (atomic Phase 5 tx: item decided, proposal approved, events)  COMMIT
        | result = Decided/AlreadyDecidedSame AND decision == approve  (item.ProposalId present)
        v
 ExecutionService.ExecuteAsync(tenantId, proposalId, CancellationToken.None)        <-- also called by ExecutionSweep at startup, per tenant
        |
        v
 IExecutionStore.BeginAttemptAsync  --> conn A: BEGIN; set tenant; set local lock_timeout; pg_advisory_xact_lock(tenant,proposal)   [held to end of attempt]
        |
        v
 attempt.LoadAsync  (conn B, short tx): proposal row (state, action_type, parameters, expires_at) + ledger rows
        |
        +-- state terminal (executed / execution_failed / expired) with a finished row ------> return FIRST RESULT (no write)
        +-- state != approved and no ledger rows -------------------------------------------> return NotApproved (no write)
        +-- finished row exists but state approved -> impossible (same tx), throw
        +-- started row, no finished row (lock proves holder is dead) ---> finish(failed, execution_in_doubt) + state execution_failed
        +-- clean approved:
              expires_at <= now ?  --> started+finished(failed, proposal_expired) + state expired + event       (no target write)
              catalog check fails? --> started+finished(failed, catalog_invalid) + state execution_failed + event
              else:
                 attempt.RecordStartedAsync          conn B tx: insert ledger 'started' ; COMMIT
                 IActionExecutor.ExecuteAsync        append JSONL line, Flush(true)   (the "target write")
                 attempt.RecordFinishedAsync         conn B tx: insert ledger 'finished' + state CAS approved->executed + proposal_event ; COMMIT
        |
        v
 dispose attempt: conn A tx ends (lock released)

 chain query (olive_reader, tenant GUC set):  SELECT ... FROM execution_chain WHERE tenant_id=@t AND execution_id=@e
```

### Recommended Project Structure
```
src/Olive.Application/
  IActionExecutor.cs        # port: ExecuteAsync(ExecutionRequest) -> ExecutionTargetResult(ResultRef); [OutboundPort] if that attribute is the convention for ports
  IExecutionStore.cs        # port: BeginAttemptAsync -> IExecutionAttempt (IAsyncDisposable); ListApprovedAsync(tenant)
  IActionCatalogCheck.cs    # port: bool IsExecutable(actionId, parametersJson)
  ExecutionService.cs       # orchestrator and the ExecutionResult union
  ExecutionReasons.cs       # catalog_invalid, proposal_expired, execution_in_doubt, executor_failed
src/Olive.Executor.Simulated/
  SimulatedActionExecutor.cs   # JSONL appender: SemaphoreSlim, FileStream, Flush(true)
  SimulatedExecutorOptions.cs  # OutputPath
src/Olive.Persistence/
  ExecutionSql.cs, PostgresExecutionStore.cs
src/Olive.Catalog/
  CatalogExecutionCheck.cs     # IActionCatalogCheck over CatalogSet
src/Olive.Host/
  Startup/ExecutionSweep.cs    # IHostedService (StartAsync runs the sweep once)
  Startup/StartupGuards.cs     # + CheckExecutorOutput
sql/migrations/0009_execution_ledger.sql
contracts/execution-record/v1/ (schema + valid + invalid)
tests/Olive.Tests/ Execution*Tests.cs, Beat2DemoTests.cs; build/demo-beat2.sh
```

`Olive.Persistence` and `Olive.Executor.Simulated` may reference only Application and Domain (`ReferenceAllowlist`), so every cross-project dependency goes through an Application port. `ArchitectureTests` has a `PackageAllowlist`, and the Host already references both Persistence and Executor.Simulated.

### Pattern 1: Lock-holder transaction (resolves the research-flag question)

**What:** A dedicated connection A runs `BEGIN`, sets `olive.tenant_id`, sets `lock_timeout`, and calls `pg_advisory_xact_lock(key)`. It stays open and idle for the whole attempt. All real work runs on connection B in ordinary short `TenantUnitOfWork`-style transactions. The lock key mirrors `LockRate`:
```sql
select pg_advisory_xact_lock(hashtextextended('exec:' || @tenant_id::text || ':' || @proposal_id::text, 0))
```
**Why:**
- The lock survives the `started` commit and is held through the JSONL write and the finish commit.
- It is released on commit, rollback or connection death. A crash in the middle therefore releases it, and the next holder knows the previous attempt is dead.
- It honours D-02's `pg_advisory_xact_lock`.

**Rules:**
- Take the lock before reading the ledger.
- Set `lock_timeout` locally (for example `set local lock_timeout = '30s'`) so a stuck holder cannot block forever. [ASSUMED] that `lock_timeout` also covers advisory-lock waits; the planner should include a test that proves it, or fall back to a `pg_try_advisory_xact_lock` poll loop.
- A timeout surfaces as `LockTimeout`, which is a result value. It is not "in doubt".
- Connection A needs a pool of at least 2. Default Npgsql pools are 100. The ablation tests use `WithMaxPoolSize(…, 1)`, so execution tests must not reuse that helper.
- Waiters each hold a connection A while queueing. Concurrent attempts per proposal are bounded in practice (one request path plus one sweep).
- Alternative considered: a session-level `pg_advisory_lock` on a single connection. It saves a connection but departs from D-02's xact-lock wording and needs a guaranteed unlock in `finally`.

### Pattern 2: Attempt object as the fault-injection seam

`IExecutionStore.BeginAttemptAsync` returns an `IExecutionAttempt` exposing `LoadAsync`, `RecordStartedAsync`, `RecordFinishedAsync` (finish plus state CAS plus event) and `RecordRefusalAsync` (started and finished together). A test-only decorator, `FaultingExecutionStore` in `tests/Olive.Tests`, wraps the real store through `ConfigureTestServices` in `OliveApiFactory` (`configureServices`) and throws inside `RecordFinishedAsync`. That is exactly "between the ledger write and the state update". No test double exists in `src`. The existing `FaultingDoubles.cs` is the precedent for naming and placement.

**Test double naming:** `SourceHygieneTests.FND_05` requires every type named `Stub*` or `Fixture*` to implement `INonProductionOnly`. Name the double `FaultingExecutionStore`, not `Stub…` or `Fixture…`.

### Pattern 3: Classify by what the lock holder sees

| Under the lock the attempt sees | Action |
|---|---|
| Finished row present | Return it (first result). Never writes. |
| State not `approved` and no ledger rows | Return `NotApproved`. |
| `started` without `finished` | `finished(failed, execution_in_doubt)`, state `execution_failed`, event. No target write. |
| Clean `approved` | Expiry check, catalog check, then the started / write / finished sequence. |

**Executor exception handling:**
- If `IActionExecutor` throws, the attempt may have partly written. Best-effort `RecordFinishedAsync(failed, executor_failed)` and state `execution_failed`. Nothing will retry it, so it is safe.
- If `RecordFinishedAsync` itself throws, do not mask it. Rethrow. The row stays `started`, and the next attempt or the sweep resolves it as `execution_in_doubt`.

**Order of the pre-checks:** expiry, then catalog. This is a choice (D-05 and D-06 do not order them). The planner should note it.

### Pattern 4: Endpoint hook after commit (Discretion item)

Call the service from `QueueEndpoints.DecisionAsync`, not from `QueueService`. `QueueServiceDecideTests` and others construct `QueueService(store, registry, check, time)` directly, so adding a constructor dependency would break them.

- Trigger on `DecisionResult.Decided d` when `d.Item.Decision == "approve"`.
- Also trigger on `AlreadyDecidedSame` with approve. A retry after a crash then self-heals, and it is idempotent.
- Use `item.ProposalId`, which `QueueItemDetail` already carries.
- Wrap in `try/catch`, log a static message with no exception text, and still return the 200 (D-01).
- **Pass `CancellationToken.None`** (or the application-stopping token), not `HttpContext.RequestAborted`. A client disconnect mid-execution would otherwise turn a clean attempt into an in-doubt one.

### Pattern 5: Startup sweep

- `ExecutionSweep : IHostedService`. `StartAsync` runs the sweep once to completion, then returns. This makes tests deterministic, because `WebApplicationFactory` host start waits for it.
- For each tenant from `IReviewerTenantMap.AllTenants` (new additive member), list `select proposal_id from proposal where tenant_id=@tenant_id and state='approved' order by created_at` inside `TenantUnitOfWork`. Then call `ExecutionService.ExecuteAsync` for each.
- Process the snapshot once. Every attempt terminalises or throws, so there is no retry loop.
- Catch per proposal and log a static message. A sweep fault must not stop host start.
- `state='approved'` is sufficient as the "not yet executed" filter, because the finished row and the state change commit together.
- **Why not a `SECURITY DEFINER` function:** the roles are `NOBYPASSRLS` and the policies are FORCEd for the table owner as well. A definer function owned by `olive_owner` would still be filtered by RLS (and would raise when the GUC is unset), so it cannot enumerate tenants without weakening the pattern.
- **Why `ReviewerTenantMap` is complete:** approval requires a reviewer whose tenant comes from that map, so every `approved` proposal belongs to a mapped tenant.
- **Test hooks:** `OliveApiFactory` must set `OLIVE_EXECUTOR_OUTPUT_PATH` to a unique temp file for every factory (see pitfall 4), and tests that need a pre-sweep state should seed `approved` rows directly before starting the host.

### Pattern 6: JSONL executor

```csharp
// Source: BCL (System.IO.FileStream.Flush(bool) writes through to disk) [CITED: learn.microsoft.com/dotnet/api/system.io.filestream.flush]
await _gate.WaitAsync(ct);
try
{
    await using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
    await fs.WriteAsync(lineBytes, ct);   // one write: the whole line including '\n'
    fs.Flush(flushToDisk: true);
}
finally { _gate.Release(); }
```

Build the line with `Utf8JsonWriter` from named fields only: `proposal_id`, `tenant_id`, `action_id`, `target_system`, `execution_mode`, `executed_at`, `would_send`. Parameters arrive as raw JSON text from the proposal row. Do not log them. `would_send` is the only free-form content.

`SimulatedActionExecutor` is not `INonProductionOnly` (D-13), and its name does not start with `Stub` or `Fixture`. Log a startup line saying `execution_mode=simulated`, and never log the path value contents beyond a filename if the project logs paths at all.

### Pattern 7: Ledger table and chain view (migration `0009_execution_ledger.sql`)

```sql
create table execution_ledger (
    tenant_id uuid not null,
    execution_id uuid not null,
    proposal_id uuid not null,
    phase text not null check (phase in ('started','finished')),
    execution_mode text not null check (execution_mode = 'simulated'),
    outcome text null check (outcome in ('succeeded','failed')),
    reason text null check (reason is null or reason ~ '^[a-z][a-z_]{0,63}$'),
    result_ref text null,
    recorded_at timestamptz not null,           -- from TimeProvider (@now), not now()
    primary key (tenant_id, execution_id),
    constraint execution_ledger_phase_unique unique (tenant_id, proposal_id, phase),
    foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id),
    constraint execution_ledger_shape check (
        (phase = 'started'  and outcome is null and reason is null and result_ref is null)
        or (phase = 'finished' and outcome = 'succeeded' and reason is null and result_ref is not null)
        or (phase = 'finished' and outcome = 'failed'    and reason is not null))
);
alter table execution_ledger enable row level security;
alter table execution_ledger force row level security;
create policy tenant_isolation on execution_ledger
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);
revoke all on execution_ledger from public;
grant select, insert on execution_ledger to olive_app;   -- no UPDATE, DELETE, TRUNCATE
grant select on execution_ledger to olive_reader;
```

**Execution record identity:** an "execution record" is the `started` row, which every attempt writes. Give both rows of an attempt the same chain key by anchoring the view on the `started` row and left-joining the `finished` row. Use `execution_id` of the `started` row as the record id. An alternative is a shared `attempt_id` column. Either works, and the planner picks one.

**View:** owned by `olive_owner` through the migrator, which runs `set role olive_owner` (`MigrationRunner`).
```sql
create view execution_chain with (security_invoker = true) as
select s.tenant_id, s.execution_id, p.proposal_id,
       'olive:proposal/' || p.proposal_id::text as proposal_ref,
       p.agent_id, p.on_behalf_of, p.purpose, p.action_type, p.state as proposal_state,
       p.trace_ref, p.trace_verification, p.trace_schema_version, p.proposal_hash,
       pe.detail as policy_decision,
       q.decided_by, q.auth_source, q.decided_at, q.rationale,
       s.execution_mode, s.recorded_at as started_at,
       f.outcome, f.reason, f.result_ref, f.recorded_at as finished_at
from execution_ledger s
join proposal p on p.tenant_id = s.tenant_id and p.proposal_id = s.proposal_id
left join lateral (select detail from proposal_event e
                   where e.tenant_id = p.tenant_id and e.proposal_id = p.proposal_id and e.event_type = 'policy_evaluated'
                   order by e.created_at limit 1) pe on true
left join queue_item q on q.tenant_id = p.tenant_id and q.proposal_id = p.proposal_id and q.kind = 'action_approval' and q.state = 'decided'
left join execution_ledger f on f.tenant_id = s.tenant_id and f.proposal_id = s.proposal_id and f.phase = 'finished'
where s.phase = 'started';
revoke all on execution_chain from public;
grant select on execution_chain to olive_reader, olive_app;
```
The view must not select `parameters`, `justification` or `canonical_body`. `proposal_event` has a `policy_evaluated` row on the accepted path (`PostgresProposalStore` line ~212, `verdict.ToDetailJson()` gives `outcome`, `reason`, `checks`). [VERIFIED: codebase]

**`security_invoker`:** PostgreSQL docs state that base relations are checked against the invoking user's privileges and that RLS policies of the invoking user apply. [CITED: postgresql.org/docs/17/sql-createview.html] The test image is `postgres:17-alpine`, and `security_invoker` needs PostgreSQL 15 or later. [CITED: same page and `PostgresFixture.PostgresImage`]

Because RLS binds the caller, every query on the view needs `olive.tenant_id` set. Both roles can read the view only after the tenant GUC is set. Add `where tenant_id = @tenant_id` in the demo's SELECT so the application predicate also stays.

### Pattern 8: Finish transaction (reuse the decide pattern)

Inside one connection-B transaction:
1. Insert the `finished` row (a unique violation here means a logic error and rolls back).
2. `ProposalSql.UpdateState` with `from_state='approved'` and `to_state` in (`executed`, `execution_failed`, `expired`). Require `ProposalStates.CanTransition` first, and require row count 1. Otherwise throw so the transaction rolls back.
3. `ProposalSql.InsertEvent` with `event_type='transition'`, `actor='system'`, and a static `detail` such as `{"reason":"execution_in_doubt","execution_mode":"simulated"}`.

`olive_app` already has `UPDATE (state)` and `INSERT` on `proposal_event` (0001 and 0002). No new grant is needed on `proposal`.

### Anti-Patterns to Avoid
- **Putting the lock and the `started` insert in one transaction.** The lock is released at commit and the in-doubt classification becomes unsound (see Summary).
- **Wrapping the executor in the approve transaction.** Forbidden by D-01 and Q5.
- **Using `HttpContext.RequestAborted` for the execution call.** See Pattern 4.
- **Reading the JSONL back to reconcile.** Rejected by D-08.
- **Storing exception messages as reasons.** Reasons are constants only (D-09).
- **Adding an endpoint for the chain.** Q18 forbids it.
- **A `SECURITY DEFINER` tenant enumerator.** See Pattern 5.
- **Calling `IProposalStore.TransitionAsync`.** It opens its own transaction (established pattern).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-proposal mutual exclusion | In-process dictionary or semaphore as the guarantee | `pg_advisory_xact_lock` plus unique ledger keys | Works across instances and survives crashes. |
| Exactly-once record | Application-level "already ran" flag | `unique (tenant_id, proposal_id, phase)` | The database is the arbiter even if the lock is bypassed. |
| Tenant context | Manual `set_config` strings | `TenantUnitOfWork` for connection B work (add a sibling method for the lock-holder connection) | Only sanctioned entry point (DB-05). |
| Canonical JSON | Custom serializer | `Utf8JsonWriter`, or the existing `Jcs` where a hash is needed | Escaping and number formatting. |
| Append-only enforcement | Triggers | Grants (INSERT, SELECT only) plus the privilege-matrix test | Matches Decision section 13. |
| Chain query API | New endpoint | The `security_invoker` view | Q18. |

## Runtime State Inventory

Not a rename or migration phase. The only runtime-state point is that the sweep, not a data migration, handles `approved` rows left from earlier phases or crashes. Existing rows in `approved` state in a dev database will execute at the first startup after this phase ships. That is intended behaviour.

## Common Pitfalls

### Pitfall 1: Misclassifying a live attempt as in doubt
**What goes wrong:** attempt 2 sees `started` without `finished` while attempt 1 is still running.
**Why:** the lock was released at the `started` commit.
**How to avoid:** lock-holder transaction (Pattern 1). Test: run two concurrent attempts with an executor that blocks on a gate. The second must wait, then return the first result. The JSONL must have exactly one line.
**Warning signs:** `execution_in_doubt` rows in a success-path concurrency test.

### Pitfall 2: Request-abort cancellation producing in-doubt rows
Use `CancellationToken.None` for the execution call (Pattern 4).

### Pitfall 3: Pool exhaustion or single-connection pools
Lock-holder plus work connection is two connections per attempt. Do not run execution tests with `Maximum Pool Size=1`.

### Pitfall 4: Every host now runs a sweep and a startup guard
Once `CheckExecutorOutput` and the sweep exist, every `OliveApiFactory` host needs a valid `OLIVE_EXECUTOR_OUTPUT_PATH`. Without it the guard fails and the whole existing suite breaks. Set a unique temp-file default in the factory's config dictionary (it merges `extraConfig`), and delete it on dispose. Also give the sweep a fast path when no tenant has `approved` rows.

### Pitfall 5: Migration pins in existing tests
`MigrationRunnerTests` pins the applied version list and per-file checksums (it references `0008_review_queue.sql` at line ~50). Adding `0009` requires updating those expectations. `DB_02` (`migration_files_on_disk_have_valid_names_and_contiguous_versions`) requires contiguous numbering, so the file must be exactly `0009_...sql`.

### Pitfall 6: Table-driven tests silently go stale
`PrivilegeMatrixTests.Expected` and `RlsPatternTests.ExpectedTables` list tables explicitly, and the RLS discovery query only matches `relkind = 'r'`, so the view is not discovered there. Add `execution_ledger` to both lists (`olive_app`: SELECT and INSERT, `olive_reader`: SELECT). Add the view as a separate assertion: `pg_class.reloptions` contains `security_invoker=true`, and a cross-tenant SELECT through the view as `olive_reader` returns zero rows. Extend `TenantIsolationAblationTests` for the ledger.

### Pitfall 7: Rationale leakage
The view legitimately exposes the rationale. The JSONL, logs, problem responses and the decide response must not. The Beat 2 transcript prints the view row, so the transcript will contain the rationale text unless the demo uses a non-sentinel rationale. D-17 says the scan checks the "rationale sentinel" in the JSONL and transcript, but D-16 says the transcript prints the view's single row, and that row includes the rationale. Plan resolution: the transcript prints the view row with the `rationale` column shown as the demo's literal, non-sentinel rationale text. The sentinel-rationale used for the leakage scan comes from a separate test (approve with a sentinel rationale, then scan the JSONL, logs and responses). The planner must not scan the transcript for the same sentinel that the view row is required to show. Flag this to the user as a mild D-16 and D-17 tension (see Open Questions).

### Pitfall 8: Catalog "no longer valid" test setup
`CatalogSet.TryFindAction` only looks at `Current` (the highest release). Simulate an invalid catalog by starting a second host over the same database with a `CatalogSet` lacking the entry (or with parameters schema that now rejects the stored parameters), then running the sweep. That also exercises EXE-01's restart sweep. Define "invalid" as: entry missing from the current release, or stored parameters no longer validate against `ParametersSchema`. Purpose and entitlement are not re-checked (Q16).

### Pitfall 9: Time source
`proposal.expires_at` is `timestamptz`. Compare in application code using `TimeProvider` (`ManualTimeProvider` exists in tests). Do not use `now()` in SQL for the expiry check. `QueueSql` already follows the "time from the caller" rule. Note `recorded_at` for ledger rows should also use the injected time.

### Pitfall 10: Dapper `DateTime` mapping
`PostgresQueueStore` maps `timestamptz` to `DateTime` and uses `Utc()` helpers. Reuse the same helpers and pass `now.UtcDateTime`.

### Pitfall 11: JSONL concurrency and sharing
Multiple concurrent attempts append to one file. Serialise with a process-wide `SemaphoreSlim`, and write each line in a single `WriteAsync`. Tests that read the file while the host holds it need `FileShare.ReadWrite` on the read side (the writer opens with `FileShare.Read`, and a reader asking for `FileShare.Read` only can fail on Windows). The writer opens and closes per line, so contention is brief.

## Code Examples

### Lock-holder and attempt skeleton (Persistence)
```csharp
// Source: pattern derived from ProposalSql.LockRate and TenantUnitOfWork in this repo
await using var lockConn = await dataSource.OpenConnectionAsync(ct);
await using var lockTx = await lockConn.BeginTransactionAsync(ct);
await lockConn.ExecuteScalarAsync<string>(new CommandDefinition("select set_config('olive.tenant_id', @t, true)", new { t = tenantId.ToString("D") }, lockTx, cancellationToken: ct));
await lockConn.ExecuteAsync(new CommandDefinition("set local lock_timeout = '30s'", transaction: lockTx, cancellationToken: ct));
await lockConn.ExecuteScalarAsync(new CommandDefinition(ExecutionSql.LockExecution, new { tenant_id = tenantId, proposal_id = proposalId }, lockTx, cancellationToken: ct));
// ... attempt methods run on uow.ExecuteAsync(tenantId, ...) (connection B) ...
// Disposing without commit rolls back lockTx and releases the lock.
```

### Orchestrator core (Application)
```csharp
// Result values, never exceptions, for business outcomes (matches QueueService style).
await using var attempt = await store.BeginAttemptAsync(tenantId, proposalId, ct);
var snap = await attempt.LoadAsync(ct);                      // proposal fields + ledger rows
if (snap is null) return new ExecutionResult.NotFound();
if (snap.Finished is { } first) return new ExecutionResult.Replayed(first);
if (snap.State != ProposalStates.Approved && snap.Started is null) return new ExecutionResult.NotApproved();
var now = time.GetUtcNow();
if (snap.Started is not null)  return await attempt.RecordFinishedAsync(Failed(ExecutionReasons.ExecutionInDoubt), ProposalStates.ExecutionFailed, now, ct);
if (snap.ExpiresAt is { } e && e <= now) return await attempt.RecordRefusalAsync(ExecutionReasons.ProposalExpired, ProposalStates.Expired, now, ct);
if (!catalogCheck.IsExecutable(snap.ActionType, snap.ParametersJson)) return await attempt.RecordRefusalAsync(ExecutionReasons.CatalogInvalid, ProposalStates.ExecutionFailed, now, ct);
await attempt.RecordStartedAsync(now, ct);                   // commits
ExecutionTargetResult target;
try { target = await executor.ExecuteAsync(request, ct); }
catch (Exception) { return await attempt.RecordFinishedAsync(Failed(ExecutionReasons.ExecutorFailed), ProposalStates.ExecutionFailed, time.GetUtcNow(), ct); }
return await attempt.RecordFinishedAsync(Succeeded(target.ResultRef), ProposalStates.Executed, time.GetUtcNow(), ct);
```

### Startup guard
```csharp
// Fail closed: the configured file must be creatable/appendable. Open with FileMode.Append and write nothing.
public static void CheckExecutorOutput(IConfiguration c) { /* path set, parent dir creatable, FileStream(Append) opens; else StartupGuardException("EXE-04", "the executor output path is missing or not writable") */ }
```
Add it to `StartupGuardFilter.Configure`. Never include the path in the exception text if the project treats config values as sensitive; a static message is enough.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| View runs with owner privileges (bypasses caller RLS) | `WITH (security_invoker = true)` | PostgreSQL 15 | Required for the chain view to stay tenant-isolated. [CITED: postgresql.org] |
| `contracts/execution-record/v0` scaffold (`executed` or `execution_failed`, `approver`, `target_response_ref`) | v1 matching the ledger and view row | This phase | Same precedent as queue-item v0 to v1. |

**v1 schema fields** (suggested; Discretion): `execution_id`, `proposal_id`, `execution_mode` (const `simulated`), `outcome` (`succeeded` | `failed` | null while in flight), `reason` (pattern), `result_ref`, `started_at`, `finished_at`, `additionalProperties: false`. Provide `.valid.json` and `.invalid.json`, and extend `ContractSchemaTests`. Whether v0 is deleted or kept is a call for the planner. Prior phases retired the scaffold test, so deleting v0 and updating any test that lists contract folders is consistent.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `lock_timeout` applies to advisory-lock waits | Pattern 1 | A stuck holder would block waiters forever. Mitigation: add a test, or use a `pg_try_advisory_xact_lock` polling loop. |
| A2 | An idle open transaction on connection A for the duration of one attempt (tens of ms) is acceptable operationally (no `idle_in_transaction_session_timeout` shorter than the JSONL write) | Pattern 1 | Lock tx killed mid-attempt. Check the test container and deploy-postgres-1 settings. |
| A3 | "Catalog valid" = entry in current release plus stored parameters still validate; purpose is not re-checked | Pitfall 8 | If the user also wants purpose re-checked, Q16 ("not entitlements") may allow it. Low risk, confirm. |
| A4 | Expiry check precedes the catalog check | Pattern 3 | Only changes which reason appears when both apply. |
| A5 | `ReviewerTenantMap` covers every tenant that can have an `approved` proposal | Pattern 5 | A tenant with an approved proposal but no mapped group would not be swept. Not possible through the current approve path. |
| A6 | `FileStream.Flush(true)` plus a per-line open/close gives adequate durability for D-12 on Windows and Linux | Pattern 6 | Rare loss after an OS crash. Resolves to in-doubt, which is the designed outcome. |

## Open Questions (RESOLVED)

1. **D-16 vs D-17: transcript contains the rationale by design.**
   - Known: the view row includes the rationale (D-14) and the transcript prints that row (D-16). D-17 says the scan covers the transcript for the rationale sentinel.
   - Unclear: the two cannot both hold for the same sentinel value.
   - Recommendation: scan the transcript for the session key and tokens only. Scan the JSONL, logs and responses for the rationale sentinel (separate approve with a sentinel rationale). Record this reading in the plan.
   - RESOLVED: Plan 06-07 Task 1 uses a literal non-sentinel rationale and scans the transcript for session key, tokens and the justification sentinel; Plan 06-06 Task 2 scans JSONL, logs, responses and ledger/proposal rows for the rationale sentinel.
2. **Execution-record identity.** Anchor the view on the `started` row's `execution_id` (recommended) or add a shared `attempt_id`. Either works; pick one in planning.
   - RESOLVED: Plan 06-01 anchors the `execution_chain` view on the `started` row's `execution_id`.
3. **Idle `olive_app` view SELECT.** `olive_app` receives SELECT on the view per D-15, and it already has SELECT on every underlying table except none missing. Confirm in the privilege matrix that the view (relkind `v`) is checked with `has_table_privilege`.
   - RESOLVED: Plan 06-01 Task 2 grants SELECT on the view and adds `DB_04_execution_chain_view_is_select_only` to the privilege matrix.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build and test | assumed ✓ (global.json 10.0.204+) | 10.x | — |
| Docker (Testcontainers) | integration tests, Beat 2 demo | not probed this session (existing suite already depends on it) | postgres:17-alpine image | — |
| Bash | `build/demo-beat2.sh` | ✓ (Git Bash) | — | Run the dotnet command directly |

No new external dependency.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 on net10.0, Testcontainers postgres:17-alpine |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` |
| Quick run command | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~Execution"` |
| Full suite command | `dotnet test` |

### Phase Requirements to Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| EXE-01 | Approve then execution runs after commit; reject does not; decide stays 200 when execution faults | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~EXE_01"` | Wave 0 |
| EXE-01 | Startup sweep executes seeded `approved` proposals in each mapped tenant; skips others | integration | `--filter "FullyQualifiedName~EXE_01_sweep"` | Wave 0 |
| EXE-02 | N concurrent attempts give one started row, one finished row, one JSONL line, identical results | integration | `--filter "FullyQualifiedName~EXE_02"` | Wave 0 |
| EXE-02 | Ledger is INSERT-only for `olive_app` (privilege matrix, UPDATE and DELETE rejected) | integration | `--filter "FullyQualifiedName~DB_04"` | extend |
| EXE-03 | `proposal_expired` and `catalog_invalid` refusals: finished row, correct state, no JSONL line | integration | `--filter "FullyQualifiedName~EXE_03"` | Wave 0 |
| EXE-04 | Rows and JSONL say `simulated`; DB rejects another mode; architecture tests stay green | integration and architecture | `--filter "FullyQualifiedName~EXE_04|Category=Architecture"` | Wave 0 |
| EXE-05 | Fault between ledger write and state update, then next attempt or restart sweep: `execution_failed`, reason `execution_in_doubt`, JSONL still one line | integration | `--filter "FullyQualifiedName~EXE_05"` | Wave 0 |
| EXE-06 | One SELECT on the view returns proposal, trace verification, policy decision, approver and rationale, and execution; cross-tenant returns zero; `reloptions` has `security_invoker=true`; no parameters or justification column | integration | `--filter "FullyQualifiedName~EXE_06"` | Wave 0 |
| EVD-02 | Beat 2 transcript written and asserted | demo | `build/demo-beat2.sh` (`--filter "Demo=beat2"`) | Wave 0 |
| IDN-08 carry-over | JSONL file text added via `SentinelScan.AddSource` and asserted clean | integration | `--filter "FullyQualifiedName~ContainmentTests"` | extend |

### Sampling Rate
- **Per task commit:** the quick command above.
- **Per wave merge:** `dotnet test`.
- **Phase gate:** full suite green before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `ExecutionServiceTests` (unit, with in-memory fakes of the three ports) covering the classification table.
- [ ] `ExecutionLedgerTests` and `ExecutionChainViewTests` (integration).
- [ ] `FaultingExecutionStore` decorator and a gate-blocking executor double in `tests/Olive.Tests`.
- [ ] `Beat2DemoTests.cs` plus `build/demo-beat2.sh` (with a `SourceHygiene`-style script test like `EVD_03_runner_script_filters_to_the_beat4_trait`).
- [ ] `OliveApiFactory` default executor output path and cleanup.
- [ ] Update `MigrationRunnerTests`, `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`, `ContractSchemaTests`.

## Security Domain

### Applicable ASVS Categories (level 1)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no (unchanged; the endpoint reuses the DevReviewer scheme) | existing |
| V3 Session Management | no | — |
| V4 Access Control | yes | Forced RLS plus application tenant predicate; INSERT-only grants on the ledger; `security_invoker` view |
| V5 Input Validation | yes | Static reason codes under a CHECK regex; parameters were validated on submission and are re-validated by the catalog check |
| V6 Cryptography | no | — |
| V7 Logging and error handling | yes | Static log text; no exception text, parameters, rationale or path content |
| V8 Data Protection | yes | JSONL allow-list writer; sentinel scan over JSONL, logs, responses, rows and transcript |

### Known Threat Patterns

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Double execution through concurrent approve and sweep | Tampering | Advisory lock plus `unique (tenant_id, proposal_id, phase)` |
| Ledger rewrite or deletion | Tampering / Repudiation | No UPDATE or DELETE grant; privilege-matrix test |
| Cross-tenant read through the view | Information disclosure | `security_invoker` plus forced RLS plus tenant predicate |
| Secret or rationale leaking into JSONL or logs | Information disclosure | Field allow-list in the line writer; static log messages; sentinel scan |
| Real network write sneaking in | Elevation of privilege | `BannedSymbols.txt`, `REQ_ACT_04`, `FND_02` architecture tests (already in place) |
| Replay of a stale approval after the catalog changed | Tampering | Catalog and expiry re-check at execution (EXE-03) |

## Sources

### Primary (HIGH confidence)
- Repository code read this session: `TenantUnitOfWork`, `PostgresQueueStore`, `QueueSql`, `ProposalSql`, `ProposalStates`, `QueueService`, `QueueEndpoints`, `Program.cs`, `StartupGuards`, `StartupGuardFilter`, `ReviewerTenantMap`, `CatalogModels`, `ArchitectureTests`, `SourceHygieneTests`, `SentinelScan`, `OliveApiFactory`, `Beat4DemoTests`, `PrivilegeMatrixTests`, `RlsPatternTests`, `MigrationRunner`, migrations 0001-0008, `contracts/execution-record/v0`.
- https://www.postgresql.org/docs/17/sql-createview.html - `security_invoker` semantics for privileges and RLS.

### Secondary (MEDIUM confidence)
- BCL `FileStream.Flush(bool)` behaviour, from training knowledge and standard docs (not fetched this session; see A6).

### Tertiary (LOW confidence)
- `lock_timeout` applying to advisory locks (A1), from training knowledge. Verify in a test.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH. No new packages; all reuse is confirmed in code.
- Architecture: HIGH on layering and reference rules (checked against `ArchitectureTests`); MEDIUM on the lock-holder design until A1 and A2 are tested.
- Pitfalls: HIGH for the codebase-derived ones (4, 5, 6, 8); MEDIUM for platform I/O points.

**Research date:** 2026-10-23
**Valid until:** 2026-11-22 (stable stack)
