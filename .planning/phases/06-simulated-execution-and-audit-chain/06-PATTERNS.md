# Phase 6: Simulated Execution and Audit Chain - Pattern Map

**Mapped:** 2026-10-23
**Files analyzed:** 22 new or modified
**Analogs found:** 21 / 22

All paths are relative to `C:\Users\OMack\olive`. Line numbers refer to the files as read on 2026-10-23.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `sql/migrations/0009_execution_ledger.sql` | migration | CRUD (INSERT-only) | `sql/migrations/0008_review_queue.sql` | exact |
| `src/Olive.Application/IActionExecutor.cs` | port | request-response | `src/Olive.Application/IQueueStore.cs` (result records) | role-match |
| `src/Olive.Application/IExecutionStore.cs` | port | CRUD | `src/Olive.Application/IQueueStore.cs` | exact |
| `src/Olive.Application/IActionCatalogCheck.cs` | port | transform | `src/Olive.Application/IReviewerTenantMap.cs` (one-line interface) | role-match |
| `src/Olive.Application/ExecutionService.cs` | service | request-response | `src/Olive.Application/QueueService.cs` | exact |
| `src/Olive.Application/ExecutionReasons.cs` | utility (constants) | n/a | `src/Olive.Domain/ProposalStates.cs`, `QueueStates.cs` | role-match |
| `src/Olive.Application/IReviewerTenantMap.cs` (add `AllTenants`) | port (modify) | n/a | itself | exact |
| `src/Olive.Catalog/ReviewerTenantMap.cs` (implement `AllTenants`) | config (modify) | n/a | itself, line 285 | exact |
| `src/Olive.Catalog/CatalogExecutionCheck.cs` | service | transform | `src/Olive.Catalog/SubmissionValidator.cs`, `CatalogModels.cs` | role-match |
| `src/Olive.Persistence/ExecutionSql.cs` | utility (SQL consts) | CRUD | `src/Olive.Persistence/ProposalSql.cs`, `QueueSql.cs` | exact |
| `src/Olive.Persistence/PostgresExecutionStore.cs` | store | CRUD, lock-holder tx | `src/Olive.Persistence/PostgresQueueStore.cs` + `TenantUnitOfWork.cs` | exact (lock part has no analog) |
| `src/Olive.Persistence/TenantUnitOfWork.cs` (add lock-holder sibling) | utility (modify) | n/a | itself | exact |
| `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs` + `SimulatedExecutorOptions.cs` | adapter | file-I/O | no code analog in the repo (see No Analog) | none |
| `src/Olive.Host/Startup/ExecutionSweep.cs` | hosted service | batch | no hosted service exists; tenant loop uses `QueueEndpoints` / `PostgresQueueStore.ListAsync` | partial |
| `src/Olive.Host/Startup/StartupGuards.cs` + `StartupGuardFilter.cs` (add `CheckExecutorOutput`) | guard (modify) | request-response | `StartupGuards.CheckVendoredVocabulary` / `CheckMigratorUrlAbsent` | exact |
| `src/Olive.Host/Endpoints/QueueEndpoints.cs` (hook in `DecisionAsync`) | route (modify) | request-response | itself, lines 120-166 | exact |
| `src/Olive.Host/Program.cs` (register services) | config (modify) | n/a | itself, lines 34-37, 60-63 | exact |
| `contracts/execution-record/v1/*` | contract | n/a | `contracts/queue-item/v1/` | exact |
| `tests/Olive.Tests/Execution*Tests.cs` | test | integration | `QueueDecisionStoreTests.cs`, `QueueDecisionTests.cs` | role-match |
| `tests/Olive.Tests/FaultingExecutionStore` (in `FaultingDoubles.cs` or new file) | test double | decorator | `tests/Olive.Tests/FaultingDoubles.cs` | exact |
| `tests/Olive.Tests/Beat2DemoTests.cs`, `build/demo-beat2.sh` | demo test, script | transcript | `Beat4DemoTests.cs`, `build/demo-beat4.sh` | exact |
| `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`, `MigrationRunnerTests`, `ContractSchemaTests`, `ContainmentTests`, `OliveApiFactory` (modify) | test (modify) | n/a | themselves | exact |

## Pattern Assignments

### `sql/migrations/0009_execution_ledger.sql` (migration)

**Analog:** `sql/migrations/0008_review_queue.sql`. The research skeleton (RESEARCH Pattern 7) is consistent with it. Copy these conventions.

**Header comment convention** (0008 lines 1-5): a comment names the requirement IDs, the INSERT-only intent, and "0001-0008 are checksum-pinned and unchanged".

**Table constraints and FK style** (0008 lines 26-27, 8-9):
```sql
primary key (tenant_id, item_id),
constraint queue_item_producer_unique unique (tenant_id, producer, producer_ref),
foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id),
withdrawn_reason text null check (withdrawn_reason is null or withdrawn_reason ~ '^[a-z][a-z_]{0,63}$'),
```
Use the same reason regex for `execution_ledger.reason`. Use a named `unique (tenant_id, proposal_id, phase)` and a shape CHECK like `queue_item_state_shape`.

**RLS block** (0008 lines 79-89) is repeated verbatim for the new table:
```sql
alter table queue_item_event enable row level security;
alter table queue_item_event force row level security;
create policy tenant_isolation on queue_item_event
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);
```

**Grants** (0008 lines 91-93):
```sql
revoke all on queue_item, queue_item_event from public;
grant select on queue_item, queue_item_event to olive_reader;
grant select, insert on queue_item_event to olive_app;
```
Mirror this for `execution_ledger`: `grant select, insert ... to olive_app; grant select ... to olive_reader`. No UPDATE, DELETE or TRUNCATE. Add `create view execution_chain with (security_invoker = true) as ...` and `grant select on execution_chain to olive_reader, olive_app`. Use `recorded_at` from `@now`, not a `now()` default (`QueueSql` header rule).

**Do not touch** 0001-0008. The new file must be exactly `0009_*.sql` (`DB_02` contiguity test).

---

### `src/Olive.Application/IExecutionStore.cs` and `IActionExecutor.cs` (ports)

**Analog:** `src/Olive.Application/IQueueStore.cs`

**Pattern** (lines 1-4, 31, 156-173): business outcomes are nested sealed records under an abstract record, never exceptions.
```csharp
namespace Olive.Application;

public interface IQueueStore
{
    Task<DecideResult> DecideAsync(Guid tenantId, Guid itemId, string actor, ..., DateTimeOffset now, CancellationToken ct);
}

public abstract record DecideResult
{
    public sealed record Decided(QueueItemDetail Item) : DecideResult;
    public sealed record NotFound : DecideResult;
}
```
Model `ExecutionResult` (`Executed`, `Replayed`, `Failed`, `NotApproved`, `NotFound`, `LockTimeout`) the same way. Time is a `DateTimeOffset now` parameter, not read inside the port. `IActionExecutor` is not an outbound network port, so do not put `[OutboundPort]` on it. `OutboundDestination` only has Popeye, Hometown and Directory (`OutboundPortAttribute.cs` lines 3-12). Check `ArchitectureTests` for any rule that enumerates ports before deciding.

---

### `src/Olive.Application/ExecutionService.cs` (service, request-response)

**Analog:** `src/Olive.Application/QueueService.cs` (176 lines; read it for primary-constructor and result-union orchestration). Skeleton to follow is the RESEARCH "Orchestrator core" block, which already uses `ProposalStates.CanTransition` and `TimeProvider`.

**Conventions to copy:**
- `sealed class ExecutionService(IExecutionStore store, IActionExecutor executor, IActionCatalogCheck catalogCheck, TimeProvider time)`. This is the primary-constructor style used by `QueueService`.
- It returns result records and never throws for business outcomes.
- Reason strings are constants in `ExecutionReasons` (snake_case, pattern `^[a-z][a-z_]{0,63}$`): `catalog_invalid`, `proposal_expired`, `execution_in_doubt`, `executor_failed`. Follow the const-string style of `src/Olive.Domain/ProposalStates.cs` lines 4-11.
- Never use exception text as a reason (D-09).

---

### `src/Olive.Persistence/ExecutionSql.cs` (SQL constants)

**Analog:** `src/Olive.Persistence/ProposalSql.cs` and `QueueSql.cs`

**Lock key pattern** (ProposalSql lines 43-47) is the template for the new key:
```csharp
public const string RateLockKey =
    "hashtextextended(@tenant_id::text || ':' || length(@agent_id)::text || ':' || @agent_id || ':' || @action_type, 0)";
public const string LockRate = "select pg_advisory_xact_lock(" + RateLockKey + ")";
```
For execution use `"exec:" || @tenant_id::text || ':' || @proposal_id::text` (fixed-length uuids, so no ambiguity).

**Rules:**
- Every statement keeps `tenant_id = @tenant_id` (QueueSql header comment, line 3).
- Time comes from `@now`, never `now()` (QueueSql header).
- Reuse `ProposalSql.UpdateState` (lines 61-62) and `ProposalSql.InsertEvent` (lines 36-38) as-is for the finish transaction. Do not duplicate them.
- Load the proposal with the columns needed (`state, action_type, parameters::text, expires_at, tenant_id`). Model it on `ProposalSql.Get` (lines 54-56) and `QueueSql.Payload` (lines 105-108, `p.parameters::text as parameters_json`).

---

### `src/Olive.Persistence/PostgresExecutionStore.cs` and `TenantUnitOfWork.cs` addition (store, CRUD)

**Analog:** `src/Olive.Persistence/PostgresQueueStore.cs` (decide at lines 229-300) and `TenantUnitOfWork.cs`.

**Class header and Dapper mapping** (PostgresQueueStore lines 1-12):
```csharp
using Dapper;
using Npgsql;
using Olive.Application;
using Olive.Domain;

namespace Olive.Persistence;

public sealed class PostgresQueueStore(TenantUnitOfWork uow) : IQueueStore
{
    static PostgresQueueStore() => DefaultTypeMap.MatchNamesWithUnderscores = true;
```
Row classes are private sealed classes with `DateTime` for timestamptz. Use the `Utc(DateTime)` helpers (lines 59-61) and pass `now.UtcDateTime`.

**Connection B work uses the sanctioned entry point** (TenantUnitOfWork lines 11-24):
```csharp
public async Task<T> ExecuteAsync<T>(Guid tenantId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
{
    ...
    await using var conn = await dataSource.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    await conn.ExecuteScalarAsync<string>(new CommandDefinition(SetTenant, new { t = tenantId.ToString("D") }, tx, cancellationToken: ct));
    var result = await work(conn, tx);
    await tx.CommitAsync(ct);
    return result;
}
```
Add a sibling method to this class that opens connection A, begins the tx, sets the tenant GUC, sets `lock_timeout`, runs the advisory lock, and returns an `IAsyncDisposable` handle. Do not commit it. Disposing rolls back and releases the lock. Keep all `set_config` calls inside `TenantUnitOfWork` (DB-05).

**Finish transaction (copy the decide pattern, lines 266-290):**
```csharp
if (!ProposalStates.CanTransition(ProposalStates.AwaitingApproval, target))
    throw new InvalidOperationException("Lifecycle table does not permit this decision.");

var p = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState,
    new { tenant_id = tenantId, proposal_id = proposalId, from_state = ..., to_state = target }, tx, cancellationToken: ct));
if (p != 1) { throw new InvalidOperationException("Decision found the proposal in an unexpected state."); }

await conn.ExecuteAsync(new CommandDefinition(ProposalSql.InsertEvent, new
{
    tenant_id = tenantId, event_id = Guid.NewGuid(), proposal_id = proposalId,
    event_type = "transition", from_state = ..., to_state = target, actor, detail,
}, tx, cancellationToken: ct));
```
For execution use `from_state = ProposalStates.Approved`, `to_state` in (`Executed`, `ExecutionFailed`, `Expired`), `actor = "system"`, and a static `detail` JSON. `ExpireInTransactionAsync` (lines 133-179) is a second example: `actor = "system"` and `detail = "{\"trigger\":\"...\"}"`. A row count other than 1 throws and rolls back.

**Do not call** `IProposalStore.TransitionAsync`. It opens its own transaction (see the comment at lines 226-228).

---

### `src/Olive.Application/IActionCatalogCheck.cs` and `src/Olive.Catalog/CatalogExecutionCheck.cs`

**Analog:** `src/Olive.Application/IReviewerTenantMap.cs` (one-line port) and `src/Olive.Catalog/ReviewerTenantMap.cs` (implementation lives in Catalog, port in Application). Catalog types are `CatalogSet`, `CatalogEntry(…, ParametersSchema …)` and `CatalogSet.TryFindAction` (current release only). Read `CatalogModels.cs` and `SubmissionValidator.cs` while implementing. I did not extract their contents, so the planner should open them for the schema-validate call signature (`SchemaValidator`).

**Port pattern:**
```csharp
namespace Olive.Application;
public interface IReviewerTenantMap { IReadOnlyCollection<Guid> TenantsFor(IEnumerable<string> groups); }
```

---

### `src/Olive.Application/IReviewerTenantMap.cs` and `src/Olive.Catalog/ReviewerTenantMap.cs` (modify: sweep tenant list)

Add `IReadOnlyCollection<Guid> AllTenants { get; }` to the interface. Implement in `ReviewerTenantMap` beside line 285:
```csharp
public IReadOnlyCollection<Guid> TenantsFor(IEnumerable<string> groups) =>
    groups.Where(g => g is not null && _groups.ContainsKey(g)).Select(g => _groups[g]).Distinct().ToArray();
// add: public IReadOnlyCollection<Guid> AllTenants => _groups.Values.Distinct().ToArray();
```
Any test or fake that implements `IReviewerTenantMap` must also change (grep `IReviewerTenantMap` in `tests/`).

---

### `src/Olive.Executor.Simulated/SimulatedActionExecutor.cs`

No code analog. Existing project is empty (`AssemblyMarker.cs`), references Application and Domain only, and the package allowlist forbids new packages. Use the RESEARCH Pattern 6 snippet (`SemaphoreSlim`, `FileStream(FileMode.Append, FileAccess.Write, FileShare.Read)`, one `WriteAsync`, `Flush(true)`) and `Utf8JsonWriter` with an allow-list of fields. Register with a name that does not start with `Stub` or `Fixture` and do not implement `INonProductionOnly` (D-13; `SourceHygieneTests.FND_05`). `INonProductionOnly` is the marker used by `Fixture*` and `Stub*` types, so the simulated executor deliberately differs from `FixtureTraceResolver`.

---

### `src/Olive.Host/Startup/StartupGuards.cs` + `StartupGuardFilter.cs` (guard)

**Analog:** `StartupGuards.CheckMigratorUrlAbsent` / `CheckVendoredVocabulary` (StartupGuards lines 12-26), wired in `StartupGuardFilter.Configure` (lines 112-119).

```csharp
public static void CheckVendoredVocabulary(string path)
{
    if (!VendoredContracts.VerifyVocabulary(path))
    {
        throw new StartupGuardException("FND-03", "the vendored vocabulary is missing or does not match the pinned hash");
    }
}
```
```csharp
StartupGuards.CheckVendoredVocabulary(Path.Combine(AppContext.BaseDirectory, "contracts", "vendored", "vocabulary.yaml"));
StartupGuards.CheckCatalog(provider);
```
Add `StartupGuards.CheckExecutorOutput(configuration)` to `Configure`. Throw `StartupGuardException("EXE-04", "<static message>")` and never put the path in the message. For catch-all fault mapping to a static message, copy `CheckCatalog` (lines 31-45).

---

### `src/Olive.Host/Startup/ExecutionSweep.cs` (hosted service, batch)

No hosted service exists in the repo. Compose from:
- Tenant loop over `IReviewerTenantMap.AllTenants` (new member).
- List query inside `TenantUnitOfWork.ExecuteAsync(tenantId, ...)`, like `PostgresQueueStore.ListAsync` (lines 63-70), with `select proposal_id from proposal where tenant_id = @tenant_id and state = 'approved' order by created_at`.
- Per-proposal `ExecutionService.ExecuteAsync(...)` with a per-item try/catch and a static log message.
- Use the `Program.cs` registration style: `builder.Services.AddHostedService<ExecutionSweep>()`. Note that `StartupGuards.CheckNoNonProductionComponents` resolves every closed registered service (StartupGuards lines ~104-135), so the sweep and its dependencies must resolve cleanly at guard time.

---

### `src/Olive.Host/Endpoints/QueueEndpoints.cs` (modify `DecisionAsync`)

**Analog:** itself. Current code (lines 120, 148-153):
```csharp
private static async Task<IResult> DecisionAsync(Guid id, HttpContext http, QueueService service, CancellationToken ct)
...
var result = await service.DecideAsync(http.User.ToReviewerContext()!, id, decision, rationale, ct);
return result switch
{
    DecisionResult.Decided d => Results.Ok(ToDto(service.ToView(d.Item))),
    DecisionResult.AlreadyDecidedSame d => Results.Ok(ToDto(service.ToView(d.Item))),
```
Add `ExecutionService` (and `ILogger`) as handler parameters. After `DecideAsync` returns, if the result is `Decided` or `AlreadyDecidedSame` and `d.Item.Decision == "approve"` and `d.Item.ProposalId` is set, call `ExecutionService.ExecuteAsync(tenant, proposalId, CancellationToken.None)` in a try/catch that logs a static message, then return the same 200. Do not touch `QueueService` (tests construct it directly). Never let the rationale or exception text reach the response or logs.

---

### `src/Olive.Host/Program.cs` (modify)

**Registration style** (lines 34-37, 60-63):
```csharp
builder.Services.AddSingleton<TenantUnitOfWork>();
builder.Services.AddSingleton<IProposalStore, PostgresProposalStore>();
builder.Services.AddSingleton<IQueueStore, PostgresQueueStore>();
builder.Services.AddSingleton<QueueService>();
...
builder.Services.AddSingleton<IReviewerTenantMap>(_ => ReviewerTenantMap.LoadEmbedded());
```
Add `IExecutionStore -> PostgresExecutionStore`, `IActionExecutor -> SimulatedActionExecutor` (options from `OLIVE_EXECUTOR_OUTPUT_PATH`), `IActionCatalogCheck -> CatalogExecutionCheck`, `ExecutionService`, and `AddHostedService<ExecutionSweep>()`. For config reading with a message that names the setting but never the value, copy `ReadTimeoutMs` (lines 83-98). Log one startup line stating `execution_mode=simulated`.

---

### `contracts/execution-record/v1/` (contract)

**Analog:** `contracts/queue-item/v1/` (schema, `.valid.json`, `.invalid.json`, `additionalProperties: false`), and the v0 to v1 precedent from Phase 5. Update `ContractSchemaTests` (read it for the folder list). Suggested fields: `execution_id`, `proposal_id`, `execution_mode` (const `simulated`), `outcome`, `reason` (pattern), `result_ref`, `started_at`, `finished_at`.

---

### Tests

**`FaultingExecutionStore`:** analog `tests/Olive.Tests/FaultingDoubles.cs` (for example `FaultingTraceResolver(ITraceResolver healthy, ResolverFault mode)`, lines 9-30): a decorator over the real implementation with a settable mode enum that throws on demand. Name it `FaultingExecutionStore` (not `Stub*` or `Fixture*`). Register it via `OliveApiFactory(..., configureServices: s => ...)`, as `Beat4DemoTests` does at line 56.

**`Beat2DemoTests` and `build/demo-beat2.sh`:** copy `Beat4DemoTests.cs` whole:
- Class traits (lines 27-28): `[Trait("Category", "Integration")]` and `[Trait("Demo", "beat2")]`.
- `await using var db = await fixture.CreateDatabaseAsync();` and `new OliveApiFactory(db)`.
- Clients: `factory.CreateKeyClient(ApiProposalTests.AlphaKey)` and `factory.CreateReviewerClient("alpha-sup-1")`.
- The transcript is a `StringBuilder` written to `TestResults/beat2-transcript.md` (lines 113-116).
- The scan (lines 120-122): `var scan = new SentinelScan(); scan.AddSource("transcript", written); scan.AssertAbsent(...)`.
- Runner test (lines 125-132): `EVD_03_runner_script_filters_to_the_beat4_trait`, which asserts the script starts with `#!/usr/bin/env bash`, contains `set -euo pipefail` and `Demo=beat2`.
- Script `build/demo-beat2.sh` is `demo-beat4.sh` with `beat4` replaced by `beat2` (lines 1-17): `dotnet test tests/Olive.Tests --filter "Demo=beat2"`, then check that the transcript is non-empty.
- Read-only DB helper: `ApiProposalTests.ReaderScalarAsync<T>(db, TestPaths.TenantAlpha, sql, ("p", id))` runs as `olive_reader` with the tenant GUC. For the one-query proof, run the chain SELECT through the same path or a copy of its connection setup.
- The Beat 4 sentinels are `ParamSentinel` and `JustSentinel` (lines 31-32). Note the D-16 and D-17 tension in RESEARCH Pitfall 7. The transcript shows a non-sentinel rationale, and the rationale-sentinel scan covers the JSONL, logs and responses from a separate test.

**Existing tests to extend (table-driven):**
- `PrivilegeMatrixTests.Expected` (lines 12-22): add `("execution_ledger","olive_app",["SELECT","INSERT"])`, `("execution_ledger","olive_reader",["SELECT"])`, and view rows. The existing comment at line 11 already reserves `execution_ledger` here.
- `RlsPatternTests.ExpectedTables`.
- `TenantIsolationAblationTests`. Note it uses a max pool size of 1, so do not reuse that helper for execution tests (two connections per attempt).
- `MigrationRunnerTests` pins version list and checksums (references 0008 at about line 50).
- `ContainmentTests` / `SentinelScan`: the comment at `ContainmentTests.cs` line 14 reserves the JSONL source. Add via `AddSource`.
- `OliveApiFactory` (config dictionary merged with `extraConfig`): add a unique temp `OLIVE_EXECUTOR_OUTPUT_PATH` per factory and delete it on dispose, otherwise every existing host fails the new startup guard.

---

## Shared Patterns

### Tenant isolation (two layers)
**Source:** `src/Olive.Persistence/TenantUnitOfWork.cs` (all DB access) and `QueueSql.cs`/`ProposalSql.cs` (all SQL).
**Apply to:** `ExecutionSql`, `PostgresExecutionStore`, the chain view demo SELECT, and the sweep.
Every statement has `tenant_id = @tenant_id`. Every connection sets `olive.tenant_id` via `set_config(..., true)`.

### Business outcomes are values, invariants throw
**Source:** `PostgresQueueStore.DecideAsync` (lines 229-300): return result records for expected outcomes. `throw new InvalidOperationException("... did not affect exactly one row.")` for invariant breaks so the transaction rolls back.
**Apply to:** `PostgresExecutionStore` and `ExecutionService`.

### Static reasons and logs
**Source:** `StartupGuards.cs` messages and `ProblemReasons`. Static text only, no config values, exception text, parameters or rationale.
**Apply to:** ledger `reason` codes, `proposal_event.detail`, log lines in the endpoint hook and the sweep.

### Time injection
**Source:** `TimeProvider` registered at `Program.cs` line 22; store methods take `DateTimeOffset now`.
**Apply to:** expiry check, `recorded_at`, `executed_at` in JSONL. Tests use `ManualTimeProvider`.

### Non-production marker rule
**Source:** `StartupGuards.CheckNoNonProductionComponents` (resolves all closed services; barred types implement `INonProductionOnly`).
**Apply to:** the simulated executor is intentionally not marked; `Faulting*` test doubles live only in `tests/`.

### Layering
**Source:** `ArchitectureTests` `ReferenceAllowlist`. Persistence and Executor.Simulated reference only Application and Domain. Catalog is reached only through an Application port. No `HttpClient` (`REQ_ACT_04`, `FND_02`, `BannedSymbols.txt`).

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `SimulatedActionExecutor.cs` | adapter | file-I/O | No file-writing or append-only adapter exists. Use RESEARCH Pattern 6. |
| `ExecutionSweep.cs` (hosted-service shell) | hosted service | batch | No `IHostedService` exists in `src`. Compose from the tenant loop and registration patterns above. |
| Lock-holder transaction (connection A) in `TenantUnitOfWork` | utility | lock | No long-lived advisory-lock holder exists. Closest is `ProposalSql.LockRate`, which is taken inside one short tx. Use RESEARCH Pattern 1. |

## Metadata

**Analog search scope:** `src/Olive.Application`, `src/Olive.Persistence`, `src/Olive.Host`, `src/Olive.Catalog`, `src/Olive.Executor.Simulated`, `src/Olive.Domain`, `sql/migrations`, `tests/Olive.Tests`, `build/`.
**Files read in full or in part:** about 18.
**Not read (planner should open before editing):** `QueueService.cs` body, `CatalogModels.cs`, `SubmissionValidator.cs`, `SentinelScan.cs`, `ArchitectureTests.cs`, `RlsPatternTests.cs`, `MigrationRunnerTests.cs`, `ContractSchemaTests.cs`.
**Pattern extraction date:** 2026-10-23
