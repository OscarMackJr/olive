# Phase 6: Simulated Execution and Audit Chain - Context

**Gathered:** 2026-10-23
**Status:** Ready for planning

<domain>
## Phase Boundary

An approved proposal is executed exactly once by the simulated executor. Execution happens after the approval transaction commits, never inside it. It writes INSERT-only `execution_ledger` rows with `execution_mode = "simulated"` and one JSONL line, which stands in for the target system (Nexus CRM) and involves no network write. Duplicate or concurrent attempts return the first result. Catalog validity and expiry are checked again just before execution. Failures, including faults that leave it unclear whether the target write happened, end in a terminal state. A startup sweep picks up approved proposals that haven't run yet. One query against a `security_invoker` view, starting from an execution record, returns the proposal, the trace verification, the policy decision, the named approver with rationale, and the execution result. A scripted beat 2 demo proves the chain end to end.

Requirements: EXE-01..EXE-06, EVD-02. Carry-over: the IDN-08 sentinel scan extends to the JSONL output.

Not in this phase:
- The Razor UI and its audit page, and real Entra OIDC (Phase 7).
- The `identity_review` kind (Phase 8).
- A real executor or any write-capable credential (charter §3.3 gate, REQ-ACT-04).
- Any new API endpoint (Q18).

</domain>

<decisions>
## Implementation Decisions

### Execution trigger
- **D-01:** **The executor runs in the approve request, after commit.** The decision endpoint commits the approval (the Phase 5 atomic decide is unchanged), then calls the executor in the same request on a fresh connection and transaction. The decide response returns the decision, as in Phase 5. A failed execution doesn't change that 200, and execution failure is never part of the approve transaction. Rejects don't trigger execution.
- **D-02:** **Exactly once rests on two things.**
  - `pg_advisory_xact_lock` keyed on (tenant_id, proposal_id) is held for each attempt. This follows the Phase 4 `LockRate` pattern in `ProposalSql`.
  - Unique ledger keys back the lock up (see D-07).
  The losing attempt, whether in the request path or the sweep, reads the existing ledger and returns the first result without writing the target.
- **D-03:** **The sweep runs only at startup** (Q5), as a hosted service. It finds `approved` proposals with no finished ledger row and runs each through the same executor path, under the same lock. Because RLS is forced and `olive.tenant_id` has no `missing_ok`, the sweep has to enumerate tenants and set the tenant context for each one. How to get the tenant list is left to research and planning (see Discretion). There's no periodic timer.
- **D-04:** **The result is surfaced only as state.** `GET /v1/proposals/{id}` already shows `executed`, `execution_failed` or `expired`. No fields are added to the proposal response, because shape changes need hometown's agreement. Full execution detail is available only through the chain view (D-14).

### Failure and refusal states
- **D-05:** **An invalid catalog entry at execution gives terminal `execution_failed`** with the static reason `catalog_invalid` (Q16 re-check). It writes a finished ledger row with outcome failed and a `proposal_event`, and does not write the target.
- **D-06:** **A proposal that has expired by execution time moves to `expired`**, through the existing `Approved → Expired` edge, consistent with POL-08 and the Phase 5 lazy expiry. It writes a finished ledger row with outcome failed and reason `proposal_expired`, plus a `proposal_event`, so the chain stays queryable. It does not write the target. Entitlements are not re-checked (Q16).
- **D-07:** **The ledger has at most two INSERT-only rows per proposal: started and finished.**
  - A `started` row is committed **before** the target write.
  - A `finished` row is written after it, carrying an outcome (`succeeded` | `failed`), a static reason code and the result reference.
  - Uniqueness is on (tenant_id, proposal_id, phase).
  - Every attempt, including refusals at execution such as `catalog_invalid` and `proposal_expired`, ends with a finished row. A refusal can write the started and finished rows together, because nothing is written to the target.
  - The proposal state change (`approved` → `executed` | `execution_failed` | `expired`) and its `proposal_event` commit in the same transaction as the finished row.
  - EXE-02's "a unique ledger row per (tenant_id, proposal_id)" is read as one row per phase; the planner should note this reading in the requirement trace.
- **D-08:** **An in-doubt attempt fails closed and the target is never written again.** A `started` row without a `finished` row means the target write may or may not have happened, for example after a crash or an injected fault between the ledger write and the state update. The next attempt, or the startup sweep, writes a finished row with outcome failed and reason `execution_in_doubt` and moves the proposal to terminal `execution_failed` (Q6). It never writes the target again, and it doesn't read the JSONL back to reconcile. Running exactly once takes priority over always running. A fault-injection test covers this (EXE-05).
- **D-09:** **Failure reasons are static codes, never exception text or parameter values**, following the existing rule that policy detail text is static.

### JSONL target output
- **D-10:** **Each line holds the would-be write:**
  - `proposal_id`, `tenant_id`, `action_id`, `target_system`, `execution_mode = "simulated"` and `executed_at`
  - `would_send`: the validated proposal parameters, which is what the target would receive
  The line never contains the session key, tokens, the rationale or the justification. The sentinel scan proves this (D-17).
- **D-11:** **Output goes to one file at a configured path,** with `tenant_id` on every line.
  - The path comes from configuration, for example `OLIVE_EXECUTOR_OUTPUT_PATH`, with a dev default under a gitignored folder (`*.jsonl` is already ignored).
  - Tests point it at a temp file.
  - A missing or unwritable path fails startup and is checked by a startup guard.
- **D-12:** **The order of writes is:** started ledger row committed, then the JSONL line appended, flushed and fsynced, then the finished row and state change committed. A crash before the fsync finishes leaves the attempt in doubt, which D-08 handles.
- **D-13:** **The simulated executor is allowed in production and always labelled.** Phase 1 is simulation-only by Decision, so the simulated executor is the real executor for this milestone.
  - Every ledger row and every JSONL line says `execution_mode = "simulated"`.
  - A startup log line states it.
  - It is not barred like the fixture resolvers.
  - The project still contains no real executor, connector, HttpClient or credential.
  - The executor sits behind an `IActionExecutor` interface that a real executor will implement later (Decision §1).

### Chain view and beat 2 demo
- **D-14:** **The `security_invoker` chain view joins the following, keyed by execution record:**
  - the proposal: id, tenant, agent, on_behalf_of, purpose, action_id, state, and the `olive:proposal/<id>` reference
  - trace verification: trace_ref, `trace_verification`, `trace_schema_version` and `proposal_hash`
  - the policy decision, from the `policy_evaluated` event
  - the approver: `decided_by`, `auth_source`, `decided_at` and the **rationale text**
  - the started and finished ledger rows: mode, outcome, reason and times
  The view does **not** include parameters or justification. The rationale appears in the view but remains banned from logs, the JSONL and problem responses.
- **D-15:** **`olive_reader` and `olive_app` get SELECT on the view.** `security_invoker` means RLS on the underlying tables binds both roles. The privilege-matrix, RLS and ablation tests are extended to cover `execution_ledger` and the view. For `olive_app`, the ledger is INSERT plus the SELECT it needs, with no UPDATE or DELETE (Decision §13).
- **D-16:** **The beat 2 demo is an xUnit test with a transcript, like Beat 4.**
  - It carries the `Demo=beat2` trait, runs on Testcontainers, and is run by `build/demo-beat2.sh`.
  - Steps: submit a fixture-traced proposal, claim and approve as a dev reviewer, which triggers execution.
  - The transcript prints the **exact single SELECT against the chain view**, run as `olive_reader`, with its one result row, followed by the JSONL line.
  - It states plainly that trace verification used fixtures (Decision §6 exit note).
- **D-17:** **The IDN-08 sentinel scan is extended** to cover the JSONL output and the beat 2 transcript through `SentinelScan.AddSource`. It checks for the session key, tokens and the rationale sentinel.

### Claude's Discretion
- How the startup sweep enumerates tenants under forced RLS: from source-controlled config, through a narrow SECURITY DEFINER function, or another option that keeps both isolation layers. Research decides, and the choice must not weaken the RLS or grant pattern.
- Ledger table and column names, the migration number (next after 0008), the view name, and the reason-code constants (`catalog_invalid`, `proposal_expired` and `execution_in_doubt` at minimum).
- Whether `contracts/execution-record/v0` is replaced by a v1 schema that matches the ledger and view row, with valid and invalid examples. Probably yes, following the Phase 5 queue-item v0 → v1 precedent.
- How the fault-injection seam is built (for example a test-only executor or a decorator that throws between steps), keeping test doubles out of production composition.
- Where the executor service and the store live (`Olive.Executor.Simulated` already exists as an empty project referencing Application and Domain).
- How the decide endpoint calls the executor after commit (a service call inside `QueueService` after a successful approve, or in the endpoint), provided the approve transaction has already committed.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Ratified decision record
- `specs/DECISION_olive-poc2-phase1_v0.5.md` — the relevant sections are:
  - §1: simulated executor behind the same interface as the real one, `execution_mode = "simulated"`, no network write.
  - §10: JSONL is the simulated target, not Olive's ledger.
  - §13: roles, INSERT-only `execution_ledger`, ENABLE + FORCE RLS on all tables.
  - §6: exit note that the beat 2 demo must state fixture trace verification.
- `specs/OLIVE_CHARTER_v0.4.md` — §3.3 (no real executor without a divisional agreement and a scoped credential), REQ-ACT-01 (chain), REQ-ACT-03 (idempotent execution) and REQ-ACT-04 (no write-capable credential).

### Requirements and rulings
- `.planning/REQUIREMENTS.md` — EXE-01..06, EVD-02, and the working defaults Q5, Q6, Q16 and Q18.
- `.planning/ROADMAP.md` — Phase 6 success criteria, the IDN-08 carry-over and the research flag topics.

### Prior phase context
- `.planning/phases/05-generic-review-queue-and-action-approval/05-CONTEXT.md` — atomic decide (APR-04), decision fields such as `decided_by`, rationale and `auth_source`, lazy expiry (D-11), and the Beat 4 demo pattern (D-15..D-17).
- `.planning/phases/04-policy-expiry-and-refusal/04-CONTEXT.md` — the advisory-lock pattern, the `policy_evaluated` event and the transition table.
- `.planning/phases/03-trace-verification/03-CONTEXT.md` — the IDN-08 sentinel scan, where the JSONL half was deferred to this phase.

### Existing contracts
- `contracts/execution-record/v0/` — the scaffold schema (`execution_id`, `proposal_id`, `outcome`, `attempted_at`, `target_response_ref` and `approver`), to replace or supersede.

### Codebase maps
- `.planning/codebase/ARCHITECTURE.md`, `.planning/codebase/CONVENTIONS.md` and `.planning/codebase/TESTING.md`

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/Olive.Executor.Simulated/`: an empty project (only `AssemblyMarker`) that already references Application and Domain.
- `src/Olive.Domain/ProposalStates.cs`: `Approved → Executed | ExecutionFailed | Expired` is already in the transition table.
- `src/Olive.Persistence/TenantUnitOfWork.cs`: per-tenant connection and transaction with `olive.tenant_id` set.
- `src/Olive.Persistence/ProposalSql.cs`: the `LockRate` `pg_advisory_xact_lock` pattern from Phase 4.
- `src/Olive.Persistence/PostgresQueueStore.cs` and `QueueSql.cs`: the atomic decide, plus the compare-and-set and event pattern on a shared (conn, tx).
- `src/Olive.Host/Startup/StartupGuards.cs`: where a guard for the executor output path goes.
- `tests/Olive.Tests/Beat4DemoTests.cs` and `build/demo-beat4.sh`: the template for the demo, its transcript and runner.
- `tests/Olive.Tests/ContainmentTests.cs`: `SentinelScan`. Its comment already reserves the JSONL source for Phase 6.
- `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests` and `MigrationRunnerTests`: table-driven, so each needs `execution_ledger` and the view added.

### Established Patterns
- Business outcomes are returned as result unions, never thrown, inside `TenantUnitOfWork`.
- Proposal state changes only move forward and only take effect if the state is still what was read. The `IProposalStore.TransitionAsync` path is not used for multi-table steps.
- Migrations are forward-only SQL in `sql/migrations/`, numbered in order of arrival.
- Time is injected through `TimeProvider`.
- Reason and state strings are snake_case constants.

### Integration Points
- `QueueService.DecideAsync` and the decision endpoint in `QueueEndpoints`: the executor is called after a successful approve commits.
- `Program.cs`: register the executor, the hosted startup sweep and the output-path config.
- `ProblemReasons`: add any new static reason codes.

</code_context>

<specifics>
## Specific Ideas

- The beat 2 transcript should show the literal one-query proof: the SELECT text, its single row, then the JSONL line.
- The ledger uses start and finish rows so that an in-doubt attempt can be detected without updating an INSERT-only table.

</specifics>

<deferred>
## Deferred Ideas

- A periodic re-execution sweep on a timer. Not chosen; the sweep runs at startup only (Q5).
- Optional execution fields on the proposal API response. Not chosen; the result is surfaced as state only.
- Reconciling an in-doubt attempt by reading the target back. Not chosen; such attempts fail closed (D-08).
- The UI audit page that reads the chain view (Phase 7).

</deferred>

---

*Phase: 06-simulated-execution-and-audit-chain*
*Context gathered: 2026-10-23*
