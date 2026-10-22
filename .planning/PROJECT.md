# Olive — Action Broker and Review Queue

## What This Is

Olive is the POC-2 program's action broker and shared review queue. It is the smallest component that can hold the program's one write credential. An identified agent principal (Liam) submits an AI-suggested action that hometown has traced. Olive authenticates the agent through popeye, verifies the trace, and applies catalog and policy checks. Only after those checks does a proposal reach a human review queue. An approved proposal is executed idempotently and recorded in an append-only ledger. This milestone delivers Olive **Phase 1**, which is simulation-only under `specs/DECISION_olive-poc2-phase1_v0.5.md`. Every part of the path is real except the final write, which goes to a simulated executor.

## Core Value

**No action reaches a target system unless it traces to a verified hometown answer, passed policy before any human saw it, was approved by a named reviewer with a rationale, and runs exactly once.** If everything else is cut, this chain must hold and be provable from the execution record.

## Requirements

### Validated

<!-- Existing scaffold capabilities. They are being restructured, not discarded. -->

- ✓ The action catalog is file-only and source-controlled (`catalog/crm.case_note@1.yaml`), loaded and validated at startup. Runtime registration is rejected (REQ-ACT-06). Existing scaffold.
- ✓ JSON Schemas for action type, proposal, queue item and execution record, with valid and invalid examples (`contracts/schemas/`, `contracts/examples/`). Existing scaffold.
- ✓ A simulated executor writes JSONL. It has no network write and no HTTP client. Existing scaffold.
- ✓ A Postgres migration sketch with `tenant_id` and RLS policies (`db/migrations/V001__action_broker.sql`). Existing scaffold; needs the §13 role model and corrections.
- ✓ Tests named by requirement ID (`tests/Olive.Tests/RequirementsTests.cs`). Existing scaffold convention.

- ✓ A forward-only SQL migration runner with a `schema_migrations` table. Validated in Phase 1: Foundation and Walking Skeleton.
- ✓ Testcontainers Postgres in CI. In-memory stores are used only in unit tests. Validated in Phase 1: Foundation and Walking Skeleton.
- ✓ Submission idempotency on `(agent_id, run_id)` using an RFC 8785 JCS `body_sha256`. An identical body (and the same `on_behalf_of` and `purpose`) returns `200` with the current state; a different body returns `409 idempotency_conflict`. A unique constraint resolves concurrent first submissions. Validated in Phase 2: Contract Integrity and Idempotent Submission.
- ✓ Schema validation is non-mutating: no coercion, no defaults, no key stripping. A `400` is not stored. Validated in Phase 2: Contract Integrity and Idempotent Submission.
- ✓ `GET /v1/catalog/{version}` serves the YamlDotNet-loaded catalog, vetted closed and validated at startup. Validated in Phase 2: Contract Integrity and Idempotent Submission.
- ✓ A popeye service-token client using a stub. Tokens are cached by `(tenant_id, delegated_agent_id, key_id)` and never outlive the session key. A token whose claims disagree with Olive's introspection is discarded, and the call fails closed with `503` and an alarm. Validated in Phase 3: Trace Verification.
- ✓ A `TraceResolver` interface with a fixture-backed implementation in `contracts/trace-resolution/`. Its response has seven fields, including `proposal_hash`. Validated in Phase 3: Trace Verification.
- ✓ Ordered checks: `trace_not_found` (`422`, not stored, retryable) → `trace_unresolved` → `trace_tenant_mismatch` → `trace_agent_mismatch` → `trace_proposal_mismatch`. A resolver that is unavailable, or a 401/403 from hometown or popeye, fails closed with `503`, and the auth failures also raise an alarm. Validated in Phase 3: Trace Verification.
- ✓ Positive resolutions are cached by `(tenant_id, trace_id)`. Not-found results are never cached. Validated in Phase 3: Trace Verification.
- ✓ `trace_verification ∈ {fixture, hometown}` and `trace_schema_version` are recorded. The service refuses to start in `production` with the fixture resolver or the token stub, including when it is decorated. Validated in Phase 3: Trace Verification.

### Active

**Contract and API (Decision §6, §12, §15)**
- [ ] `POST /v1/proposals`, `GET /v1/proposals/{proposal_id}` and `GET /v1/catalog/{version}` as ASP.NET Core endpoints.
- [ ] JSON Schema 2020-12 payloads under `contracts/proposal/v1/`, OpenAPI 3.1 that `$ref`s those schemas, and an S-OL-1 → v1 `COMPATIBILITY.md`.
- [ ] The §15.2 / §16 / §17 order of operations: introspect → principal checks → idempotency → schema/catalog → trace resolution → policy → persist and enqueue.

**Identity and authorization (Decision §7, §17.1)**
- [ ] A popeye introspection client behind an interface, with a stub for Phase 1. It covers `active`, `key_id`, `principal_type = agent`, the agent allowlist (`liam`), `on_behalf_of`, `purpose`, `tenant_id` and `expires_at`.
- [ ] Separate caller paths. Users and applications receive `403 principal_not_permitted` on the proposal endpoint, and agents and users cannot use Olive's service path.
- [ ] Authorization uses the intersection of the agent's entitlements and the `on_behalf_of` user's entitlements.
- [ ] The session key is sent only to popeye. A capture of every outbound call proves this. (Phase 3 proved it for responses, Trace-level logs and stored rows. The JSONL output check is deferred to Phase 6.)

**Policy and lifecycle (Charter §3)**
- [ ] Policy (purpose, entitlements, per-agent rate limits) runs before queueing. A `refused_policy` proposal is stored and never queued.
- [ ] The full lifecycle: `proposed` → `refused_policy` | `awaiting_approval` → `approved` | `rejected` → `executed` | `execution_failed` | `expired`, with state guards on every transition.
- [ ] Expiry defaults to 8 hours, with a per-action maximum. `expires_in_seconds` can only shorten it, and it is capped at the key's `expires_at`. Expired proposals cannot be approved or executed and return a distinct error.
- [ ] Execution is idempotent on `proposal_id`. A retry returns the first result.

**Persistence (Decision §10, §13)**
- [ ] Postgres tables `proposal`, `proposal_event`, `queue_item` and `execution_ledger`, all with `tenant_id`, ENABLE + FORCE RLS, and `current_setting('olive.tenant_id')` without `missing_ok`.
- [ ] The roles `olive_owner`, `olive_migrator`, `olive_app` and `olive_reader`. `execution_ledger` and `proposal_event` are INSERT-only.
- [ ] Application tenant predicates in every query, plus a negative test that removes each layer in turn.

**Review queue (Charter §4, IR-1)**
- [ ] A generic queue built on IR-1's model: states, decision values and decision history; `subject_ref` and `payload_ref` only, never record bodies; a kind registry.
- [ ] Two registered kinds: Olive's action approval, and `identity_review` (`candidate_pair`). No live bluto traffic. IR-1 create-body rules apply: `tenant_id` and `producer` are rejected in the body.
- [ ] List, claim, decide and audit. Every decision records who, when and why; a decision without `decided_by` or `rationale` is rejected.
- [ ] Approval rules: per proposal, an authenticated reviewer, no self-approval (the approver is not `on_behalf_of`), and the role is checked at decision time.

**Reviewer UI (Decision §8, §11)**
- [ ] A minimal Razor Pages queue UI with list, claim, decide and audit views. No dashboards, bulk operations or SLAs.
- [ ] Entra OIDC wiring behind an interface, with a local dev identity-provider stub. Live approvals stay blocked until the `role:case_supervisor` group is named.

**Evidence (milestone done-criteria)**
- [ ] Every test case in Decision §16.8 and §17.4, plus charter TC-ACT-01..06 and TC-QUE-01..04, runs green in CI.
- [ ] A scripted beat 2 / beat 4 demo. In beat 2, a fixture-traced proposal is approved and executed in simulation, and the full chain can be queried from the execution record. In beat 4, an out-of-purpose proposal never appears in the queue.
- [ ] A contract package (OpenAPI, schemas, COMPATIBILITY.md, Liam fixtures) ready for the §3 joint review.
- [ ] A credential audit showing zero write-capable credentials, plus the session-key capture.

### Out of Scope

- Any real target-system write, connector, SDK or write credential: Phase 2 needs a signed divisional write agreement, a named credential owner, rotation, and CTO/CSO sign-off (Decision §2; AGENTS.md).
- A live hometown trace resolver: it depends on hometown's HTTP server and popeye's service tokens, which popeye has not ratified (§16.7, §17.1).
- Live popeye token issuance or registry changes: popeye owns these and they are not ratified. Olive builds against a stub only.
- Live bluto → Olive identity-review traffic: blocked by IR-1 §7–§8 gates.
- Approving proposals inside Teams: harder to audit (Decision §11). Notifications that link to the UI may come later.
- Irreversible actions and dual approval: POC-2's single action is reversible (Charter §3.2.4).
- A general tool registry, third-party tools, or MCP governance: Charter §5 excludes them.
- Generating proposals, calling models, resolving identity, or reimplementing CRM business rules: Charter §2 "refuses to own".
- A runtime action-type registration API: catalog changes are change-managed (AGENTS.md, REQ-ACT-06).
- Cross-tenant audit roles and production retention: Phase 2 decisions.
- Anchoring the ledger to the zt-infra append-only log: an open charter §6.5 question.

## Context

- **Program.** POC-2 (fiskroad) has these components. hometown (TypeScript) is the semantic layer that produces traced answers (ATE). popeye is the gateway that introspects keys and will issue tokens. nono is the desktop session proxy that injects agent keys. bluto (C#/.NET) handles identity resolution. Liam is the agent that submits proposals. Olive owns gaps G1 (governed actions), G4 (review queue) and one third of G2 (tool governance).
- **Authoritative specs.** `specs/OLIVE_CHARTER_v0.4.md`, `specs/DECISION_olive-poc2-phase1_v0.5.md` (v0.5.2, §1–§17 ratified 2026-10-20) and `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (ratified 2026-10-21) are in this repo. The §4-referenced specs (`POPEYE_v0.4.2`, `NONO_v0.4.x`, `CROSS_COMPONENT_EXECUTION_SPEC.md`, the agent-key decisions, `PROGRAM_SHARED_VOCABULARY_v0.4`) are present in `~/fiskroad/specs/`. The Liam specs are in `~/liam/specs/`. Under the §4 stop rule, if any of these is missing at run time, the implementer stops and reports it.
- **Target action.** `crm.case_note@1` on `nexus_crm` (Nexus is a first-party app with synthetic data). Phase 1 makes an engineering proof only.
- **Environment.** The shared local Postgres is `deploy-postgres-1` on `deploy_default`, started by Nexus CRM `deploy/`, with its own `olive` database. Connection variables follow §15.4 (`OLIVE_POSTGRES_URL`, `OLIVE_MIGRATOR_URL`).
- **Codebase state** (see `.planning/codebase/`). One `Domain.cs` file with in-memory stores, a hand-rolled YAML parser and no web host. `CONCERNS.md` lists known defects: no approval state guard, a duplicate enqueue on resubmit, an unknown action throwing instead of refusing, null grants failing open, refusals consuming the rate limit, no thread safety, and the RLS migration lacking roles and grants. The IR-1 queue model conflicts with the scaffold's queue states. `docs/STATUS.md` is stale.
- **People.** Ratifier: Oscar Mack (architecture and risk). Independent reviewer: James O'Meara. Informed: CTO Kamal Shah and CSO Chris Planell; their sign-off is required for Phase 2 only.

## Constraints

- **Tech stack**: C# / .NET 10, ASP.NET Core and Razor Pages, PostgreSQL with Npgsql, xUnit v3 with Testcontainers. Charter §6.2 recommends .NET, which matches bluto and is familiar to reviewers.
- **Security**: no write-capable credential anywhere (REQ-ACT-04). The session key goes only to popeye. Tokens are never logged or stored. The service fails closed on any resolver or auth failure.
- **Data**: queue payloads are references only, never bodies (REQ-QUE-01). Tenant isolation has two layers, an application predicate and forced RLS, and both are kept.
- **Contracts**: `contracts/vendored/vocabulary.yaml` is never edited. `principal_type` is limited to `user | application | agent`. Within `v1`, changes are additive and optional only.
- **Dependencies**: popeye and hometown are stub- or fixture-backed until their sides are ratified and live. Fixtures are barred in production.
- **Change control**: changes to Decision §1–§3 need re-ratification. Changes to the response shape need hometown's agreement. Changes to the IR-1 contract need agreement from both the olive and bluto owners.

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Milestone 1 covers all of Phase 1 (simulation-only) | The ratified record defines it fully; only the final write is simulated | — Pending |
| Rebuild the queue on the IR-1 model, with action-approval and identity_review kinds | IR-1 is ratified; the queue is shared by three consumers and is built once | — Pending |
| Restructure into API, domain, persistence and executor projects | The single-file scaffold can't host an HTTP API, Postgres and a UI cleanly | — Pending |
| Razor Pages for the reviewer UI | Same solution, no JS build, minimal attack surface; fits "deliberately minimal" | — Pending |
| Entra OIDC behind an interface, with a local stub IdP | The approver group is unnamed; build the real path without blocking on it | — Pending |
| Popeye and hometown behind interfaces, with stubs and fixtures | Their sides aren't live or ratified; switching to live is a transport-only change | — Pending |
| Reconcile the migration location (`db/migrations/V001__…` vs Decision §13's `sql/migrations/0001_init.sql`) | The ratified record names a convention; the scaffold deviates | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-10-22 after Phase 3 (Trace Verification) completed. Proposals proceed only when the fixture trace matches on run, tenant, agent and hash. Mismatches are stored as `refused_policy`, and resolver or auth failures fail closed with 422 or 503, raising alarms where specified. The session key is shown to reach only popeye, except that the JSONL check is deferred to Phase 6. 735 tests are green.*
