# External Integrations

**Analysis Date:** 2026-10-21

**Present state:** no live integration exists. `AGENTS.md` and `docs/STATUS.md` state there is no target-system connector, SDK, HTTP client, credential, or model call. Real executors require a signed divisional write agreement and a scoped credential (charter section 3.3). Everything below marked "Planned" is specified in `specs/` or `contracts/queue/` only.

## APIs & External Services

**Target system (write path):**
- Present: `SimulatedActionExecutor` in `src/Olive/Domain.cs` appends `{proposal_id, action_id, would_send}` lines to a local JSONL file. No network call.
- Catalog target name: `nexus_crm` (`catalog/crm.case_note@1.yaml`, action `crm.case_note@1`). Nexus is an internal app with synthetic data; target selection is still open (`docs/STATUS.md`).
- Do not add a real executor until the signed agreement and scoped credential exist.

**Identity/token service (popeye) - Planned, not implemented:**
- Agent session-key introspection and tenant-scoped service token issuance (`application` principal with `delegated_agent_id` claim). Spec: `specs/DECISION_olive-poc2-phase1_v0.5.md` sections 7 and 17.1. The popeye dependency is only acknowledged as proposed on popeye's side. `POPEYE_v0.4.2` is not in this repo.

**Trace resolution (hometown) - Planned, not implemented:**
- `GET /v1/traces/{trace_id}/resolution` against hometown, authenticated with a popeye-issued service token (same decision record, sections 14, 16, 17). The session key must never be sent to hometown or a target system.

**bluto identity review - Planned, stub-only:**
- bluto produces `identity_review` queue items and polls for decisions per tenant. Contract: `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (ratified 2026-10-21, no live traffic until popeye registers bluto). Olive never calls bluto.

**Model calls:** none. Proposals originate outside Olive.

## Data Storage

**Databases:**
- Present: schema only, `db/migrations/V001__action_broker.sql`. Schemas `action_broker` (`proposals`, `queue_items`, `executions`, `idempotency_records`) and `integration_outbox` (`outbox_facts`). All tables carry `tenant_id`, with forced RLS using `current_setting('olive.tenant_id')` via `action_broker.current_tenant_id()`.
- No client code connects to it. Application queries must still include tenant predicates.
- Planned: database `olive` on the shared local Postgres (`deploy-postgres-1`), connection via `OLIVE_POSTGRES_*` variables. Not the Azure Postgres Flexible Server (popeye's ledger).

**File Storage:**
- Local filesystem: simulated executor JSONL (gitignored `*.jsonl`).

**Caching:**
- None.

## Authentication & Identity

**Auth Provider:**
- Not implemented. Planned: Entra ID OIDC for queue reviewers (approval requires an authenticated reviewer); popeye for application and agent callers; Entra group for `role:identity_steward` not yet named.
- Principal types are limited to `user`, `application`, `agent` (shared vocabulary `contracts/vendored/vocabulary.yaml`).

## Monitoring & Observability

**Error Tracking:**
- None.

**Logs:**
- None. Audit is via DB tables; whether it also anchors to zt-infra's append-only log is an open decision (`docs/STATUS.md`).

## CI/CD & Deployment

**Hosting:**
- Not defined.

**CI Pipeline:**
- None detected. Planned: throwaway Postgres via Testcontainers per CI run.

## Environment Configuration

**Required env vars:**
- None today. Planned: `OLIVE_POSTGRES_URL` and split HOST/PORT/DB/USER/PASSWORD variants.

**Secrets location:**
- Planned gitignored local `.env` with committed `.env.example` (neither exists). Never commit secrets.

## Webhooks & Callbacks

**Incoming:**
- None implemented. Planned: proposal endpoint and queue create/poll API (`contracts/queue/identity-review/...`).

**Outgoing:**
- None. Contract says olive never calls bluto; no callbacks.

## Contract Artifacts

- JSON Schemas: `contracts/schemas/{action-type,proposal,queue-item,execution-record}.schema.json`, with valid/invalid fixtures in `contracts/examples/`.
- Vendored shared vocabulary: `contracts/vendored/vocabulary.yaml` (source `~/_agent-inputs/olive/PROGRAM_SHARED_VOCABULARY_v0.4.md`, read-only). Do not edit.
- Queue payloads are references only, never inline bodies.

---

*Integration audit: 2026-10-21*
