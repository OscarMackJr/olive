<!-- refreshed: 2026-10-21 -->
# Architecture

**Analysis Date:** 2026-10-21

## System Overview

```text
 Externally generated proposals (Liam etc.; NOT created by Olive)
                         │  (no HTTP/API layer exists yet)
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                 ActionBroker (orchestrator)                  │
│        `src/Olive/Domain.cs`  Submit / Approve / Execute     │
├───────────────┬───────────────────┬─────────────────────────┤
│   Catalog     │   PolicyEngine    │   ReviewQueueService    │
│ file-loaded   │ purpose, delegat. │ tenant-scoped items,    │
│ `catalog/*`   │ rate limit        │ refs only               │
└───────────────┴───────────────────┴───────────┬─────────────┘
                                                │ approved
                                                ▼
┌─────────────────────────────────────────────────────────────┐
│  ISimulatedActionExecutor -> SimulatedActionExecutor         │
│  appends "would_send" lines to a local *.jsonl file          │
└─────────────────────────────────────────────────────────────┘

Target persistence design (not wired to code): `db/migrations/V001__action_broker.sql`
Wire contracts: `contracts/schemas/*.schema.json`, `contracts/queue/identity-review/`
```

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| `ActionBroker` | Proposal lifecycle: validate, queue, approve, execute; holds proposals in memory | `src/Olive/Domain.cs` |
| `Catalog` | Loads source-controlled action types from YAML, validates ids and purposes against vendored vocabulary; `AddAtRuntime` always throws | `src/Olive/Domain.cs`, `catalog/crm.case_note@1.yaml` |
| `PolicyEngine` | Three checks per proposal: `purpose_entitlement`, `principal_delegation`, `rate_limit` | `src/Olive/Domain.cs` |
| `ReviewQueueService` | Shared review queue: add, list, claim, decide, audit, tenant-filtered | `src/Olive/Domain.cs` |
| `SimulatedActionExecutor` | Idempotent (by `ProposalId`) simulated execution; JSONL append only | `src/Olive/Domain.cs` |
| `Outcomes` | String constants for all states (match DB check constraints) | `src/Olive/Domain.cs` |
| Records (`Principal`, `ActionType`, `Proposal`, `PolicyDecision`, `QueueItem`, `ExecutionRecord`) | Immutable domain data; mutate via `with` | `src/Olive/Domain.cs` |

## Pattern Overview

**Overall:** Single-assembly domain library (scaffold) implementing an action-broker / approval-gate workflow with in-memory stores.

**Key Characteristics:**
- Olive never originates proposals or calls models; it validates, queues, approves, simulates (`AGENTS.md`).
- Policy refusal happens before queueing (REQ-ACT-02); refused proposals never reach the queue.
- Queue items carry references (`payload_ref`, `subject_ref`), never inline bodies (REQ-QUE-01).
- Tenancy enforced twice: application predicates (`x.TenantId == tenantId` in every lookup) plus PostgreSQL forced RLS.
- Catalog is file-only, loaded at startup; no runtime registration, no `action_types` table.
- No real executor, HTTP client, credential or connector (charter §3.3 gate).

## Layers

**Contracts layer:**
- Purpose: JSON Schemas + valid/invalid examples, queue-kind contracts, vendored vocabulary
- Location: `contracts/`
- Contains: `schemas/`, `examples/`, `queue/identity-review/`, `vendored/` (read-only `vocabulary.yaml`)
- Used by: tests (`RequirementsTests`), external producers/consumers

**Catalog layer:**
- Location: `catalog/` (YAML per action type, named `<action_id>.yaml`)
- Parsed by line-based reader in `Catalog.Parse` (not a YAML library)

**Domain/service layer:**
- Location: `src/Olive/Domain.cs`
- Depends on: BCL only (System.Text.Json, Regex, File IO)

**Persistence layer (design only):**
- Location: `db/migrations/V001__action_broker.sql` — schemas `action_broker` and `integration_outbox`; tables `proposals`, `queue_items`, `executions`, `idempotency_records`, `outbox_facts`; RLS via `action_broker.current_tenant_id()` reading `olive.tenant_id` session setting. No C# code uses it yet.

## Data Flow

### Primary Request Path

1. `ActionBroker.Submit` resolves the `ActionType` from `Catalog.Get(proposal.ActionId)` (`src/Olive/Domain.cs`).
2. `PolicyEngine.Evaluate` returns decisions; all must be `"allowed"`.
3. Proposal saved with state `awaiting_approval` or `refused_policy`; on accept a `QueueItem` (kind `action_approval`, `payload_ref = proposal://<id>`) is added.
4. `ActionBroker.Approve` (expiry check, then `ReviewQueueService.Decide` requiring reviewer + rationale) sets state `approved` and `Approver`.
5. `ActionBroker.Execute` requires `approved`/`executed` state and an approver, then calls the executor; repeat calls return the same `ExecutionRecord`.

### Expiry

`Approve` and `Execute` compare `ExpiresAt <= now`; state becomes `expired` and `Execute` throws.

**State Management:**
- In-memory dictionaries/lists inside `ActionBroker`, `ReviewQueueService`, `PolicyEngine` (rate windows), `SimulatedActionExecutor`. Time is injected via `now` parameters.

## Key Abstractions

**Proposal state machine:** `proposed → refused_policy | awaiting_approval → approved | rejected | expired → executed | execution_failed`; constants in `Outcomes`, mirrored in `db/migrations/V001__action_broker.sql` check constraints.

**ISimulatedActionExecutor:** seam where a real executor would plug in; only the simulated one is permitted until a signed write agreement and scoped credential exist.

**Principal:** `user`/`agent` with `OnBehalfOf` delegation; effective grant is intersection of agent and delegator grants.

## Entry Points

**Library API:** `ActionBroker` in `src/Olive/Domain.cs`. No executable host, API, or Program.cs exists.
**Tests:** `tests/Olive.Tests/RequirementsTests.cs` is the only consumer.

## Architectural Constraints

- **Threading:** Not thread-safe; plain `Dictionary`/`List` without locks.
- **Global state:** None static; all state is per-instance.
- **Circular imports:** None (one file).
- **Serialization:** DB multi-row writes are expected to use SERIALIZABLE isolation with row-count checks before COMMIT (comment in `V001__action_broker.sql`).
- **Vendored file:** `contracts/vendored/vocabulary.yaml` must not be edited (hash in `contracts/vendored/VENDORED.md`).

## Anti-Patterns

### Inline payloads in queue items
**What happens:** Adding body fields to `QueueItem`/queue schema.
**Why it's wrong:** Violates REQ-QUE-01; `contracts/schemas/queue-item.schema.json` has `additionalProperties: false`.
**Do this instead:** Store a `*_ref` string like `proposal://<id>`.

### Dropping tenant predicates
**What happens:** Querying without `TenantId` because RLS exists.
**Do this instead:** Follow `ReviewQueueService.List(tenantId)` and `ActionBroker.Get(tenantId, id)`.

### Runtime action registration or real connectors
**What happens:** Adding registration APIs, `HttpClient`, SDKs.
**Why it's wrong:** `REQ_ACT_04` test fails on `HttpClient` in `Domain.cs`; `REQ_ACT_06` demands source-controlled catalog.
**Do this instead:** Add a YAML file under `catalog/`.

## Error Handling

**Strategy:** Throw exceptions; no result types.
- `InvalidOperationException` for unknown action type, expired or unapproved execution
- `KeyNotFoundException` for missing/cross-tenant proposal or item
- `ArgumentException` for decisions lacking reviewer/rationale/valid outcome
- `NotSupportedException` for runtime catalog adds

## Cross-Cutting Concerns

**Logging:** None. Audit via `QueueItem` decision fields and `ReviewQueueService.Audit`.
**Validation:** Catalog parse validation; JSON schemas in `contracts/schemas/`; policy detail strings never include parameter values.
**Authentication:** Not implemented; `Principal` is supplied by the caller.

---

*Architecture analysis: 2026-10-21*
