# Requirements: Olive — Action Broker and Review Queue (Phase 1, simulation-only)

**Defined:** 2026-10-21
**Core Value:** No action reaches a target system unless all of the following hold, provable from the execution record:
- it traces to a verified hometown answer;
- it passed policy before any human saw it;
- a named reviewer approved it with a rationale;
- it runs exactly once.

**Authority:** `specs/DECISION_olive-poc2-phase1_v0.5.md` (D§), `specs/OLIVE_CHARTER_v0.4.md` (Charter REQ-ACT and REQ-QUE), and `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (IR-1). Where a requirement below differs from those documents, the documents govern. Open-question numbers (Q#) refer to `.planning/research/SUMMARY.md`.

## Rulings and working decisions (2026-10-21)

**Ruled at initialization.** These go into the next decision-record revision.

| Q# | Ruling |
|---|---|
| Q7 | Only the claimant can release a claim (claimed → open, recorded). There are no lease timers and no admin reassignment. |
| Q9 | Add a `queue_item_event` table: INSERT-only, under RLS. `olive_app` gets INSERT and SELECT only. D§13 is amended to five tables. |
| Q11 | When a proposal expires, its queue item moves to `withdrawn` with reason `proposal_expired`. This is committed lazily, and list queries also filter on `expires_at`. |
| Q13 | A reviewer's tenant comes from a source-controlled server-side map of Entra group or app role to tenant. Each session has one tenant, never taken from a form or query string. |

**Working defaults** (from research; each phase's discuss step may revisit them):

| Q# | Default |
|---|---|
| Q1 | An unparseable or non-I-JSON body, or one whose `run_id` is missing or invalid, gets 400 before the idempotency check. Otherwise the body is hashed and a mismatch gets 409. |
| Q2 | Validation runs in this order. Envelope schema first. Then catalog lookup: an unknown action is a stored `refused_policy` with reason `not_in_catalog`. Then parameter schema: a failure is a 400 and nothing is stored. |
| Q3 | An `expires_in_seconds` value that is ≤0 or above the catalog maximum gets 400; it is never clamped. An expired key gets 401. |
| Q4 | Entitlements come only from the POPEYE_v0.4.2 claims or vocabulary §3.2. If neither defines them, stop under D§4. |
| Q5 | Execution runs in-process after the approval commits, never inside that transaction. A sweep at startup picks up anything approved but not yet executed. |
| Q6 | `execution_failed` is terminal in Phase 1. |
| Q8 | Only the claimant may decide. |
| Q10 | Each queue kind has its own decision set. action_approval uses `approve` / `reject`; identity_review uses `accept` / `reject` / `defer`. |
| Q12 | Action proposals cannot be withdrawn. |
| Q14 | The self-approval check maps the reviewer to `on_behalf_of` explicitly by Entra `oid` and `tid`. If the mapping is missing or ambiguous, the approval is refused. |
| Q15 | The role is checked at decision time using an Entra app role plus either a `max_age` re-challenge or a short-lived cookie that is re-validated. Graph is not used. |
| Q16 | Catalog validity and expiry are checked again at execution. Entitlements are not re-checked. |
| Q17 | No stub of any kind may run in production. |
| Q18 | The audit chain is queried through a `security_invoker` view and the UI audit page. There is no new API endpoint. |
| Q19 | The session-key capture records calls at the port level and is backed by architecture tests. Wire-level capture is added when live transports arrive. |
| Q20 | Shared JCS golden vectors are committed under `contracts/` for hometown to run. |

## v1 Requirements

### Foundation (FND)

- [ ] **FND-01**: The solution is split into Domain, Contracts, Application, Persistence, Catalog, Executor.Simulated, Integrations.Popeye, Integrations.Hometown, Integrations.Entra, Host and Migrator projects. Dependencies point inward.
- [ ] **FND-02**: Architecture tests fail the build if Domain, Application or Executor reference an HTTP client, or if a project references a package outside its allowlist. BannedApiAnalyzers bans `new HttpClient`.
- [ ] **FND-03**: `.gitattributes` keeps vendored, schema and fixture files byte-stable (LF, no conversion). The vendored `vocabulary.yaml` SHA-256 is verified at startup and in CI.
- [ ] **FND-04**: `OLIVE_ENVIRONMENT ∈ {local, ci, nonprod, production}` must be set; it has no default. The service refuses to start if it is unset.
- [ ] **FND-05**: With `OLIVE_ENVIRONMENT=production`, the service refuses to start if any implementation marked `INonProductionOnly` is registered. That covers the fixture resolver, popeye stubs and the dev identity provider.
- [ ] **FND-06**: The session key and the service token are sealed types that redact themselves. Neither value appears in `ToString`, logs, exceptions or serialized output.
- [ ] **FND-07**: CI on Ubuntu builds the solution and runs unit, architecture and Testcontainers integration tests, with locked package versions (Central Package Management plus lockfiles).

### Persistence and tenant isolation (DB)

- [ ] **DB-01**: The `olive_owner`, `olive_migrator`, `olive_app` and `olive_reader` roles are created by an idempotent bootstrap script, with the D§13 privileges.
- [ ] **DB-02**: A forward-only migration runner applies `sql/migrations/NNNN_*.sql` as `olive_owner`. It records each migration in `schema_migrations`, takes an advisory lock, and fails if an applied file has changed. Only the runner reads `OLIVE_MIGRATOR_URL`.
- [ ] **DB-03**: The `proposal`, `proposal_event`, `queue_item`, `queue_item_event` and `execution_ledger` tables all carry `tenant_id uuid NOT NULL`, with ENABLE and FORCE ROW LEVEL SECURITY. Each policy checks `current_setting('olive.tenant_id')` (no `missing_ok`) in both USING and WITH CHECK.
- [ ] **DB-04**: `proposal_event`, `queue_item_event` and `execution_ledger` are INSERT-only for `olive_app`: no UPDATE or DELETE grant or policy. A privilege-matrix test checks that UPDATE and DELETE are denied.
- [ ] **DB-05**: Every database access goes through a tenant unit of work. It sets `set_config('olive.tenant_id', $1, true)` in an explicit transaction, and every query also includes an application-level tenant predicate.
- [ ] **DB-06**: Negative tests remove each isolation layer in turn and show the other still blocks cross-tenant reads and writes. The tests run as `olive_app` (asserting the role is not a superuser and does not bypass RLS), with two seeded tenants, and with `Max Pool Size=1`. An unset tenant raises an error. (Charter REQ-QUE-02)
- [ ] **DB-07**: At startup, the service refuses to run as a superuser, as a role with BYPASSRLS, or as the owner role. It also refuses to run if the migration version doesn't match.

### Proposal API and contract (API)

- [ ] **API-01**: An agent can `POST /v1/proposals` with `action_type`, `party_ref`, `parameters`, `justification`, `trace_ref`, `run_id` and an optional `nono_session_id`. It receives a `proposal_id` and the lifecycle state.
- [ ] **API-02**: A caller can `GET /v1/proposals/{proposal_id}` within its own tenant. An id from another tenant returns 404.
- [ ] **API-03**: A caller can `GET /v1/catalog/{version}` and receives the source-controlled catalog entry, including the parameter JSON Schema.
- [ ] **API-04**: The request body is read as raw bytes, parsed once with duplicate keys rejected, and never bound to a model. A body is rejected with 400 if it needs type coercion, defaults, key stripping or trimming to validate, or if it contains a null optional field.
- [ ] **API-05**: The v1 payload JSON Schemas (2020-12) live under `contracts/proposal/v1/` and are validated with format assertion on.
- [ ] **API-06**: Submission is idempotent on `(agent_id, run_id)`, using a `body_sha256` computed with RFC 8785 JCS. An identical body returns 200 with the original `proposal_id` and its current state. A different body returns `409 idempotency_conflict`, is not stored, and is logged.
- [ ] **API-07**: Two concurrent first submissions with the same key resolve to one proposal (unique constraint plus re-lookup).
- [ ] **API-08**: The submission steps run in a fixed order: introspect → principal checks → idempotency → schema and catalog → trace resolution → policy → persist and enqueue. A test asserts the order, and the first failure wins.
- [ ] **API-09**: The in-house JCS implementation passes the RFC 8785 and cyberphone test vectors. Shared golden vectors are committed under `contracts/` for hometown to run.
- [ ] **API-10**: The catalog is loaded from YAML at startup with YamlDotNet and validated against the action-type schema. The hand-rolled parser is removed, and runtime registration stays impossible. (Charter REQ-ACT-06)

### Identity and service credentials (IDN)

- [ ] **IDN-01**: Every request is introspected through a popeye introspection port, with a stub implementation that follows POPEYE_v0.4.2. Inactive or invalid keys get 401, and introspection always runs first.
- [ ] **IDN-02**: `POST /v1/proposals` accepts only `principal_type = agent` with an allowlisted `agent_id` (`liam`). `user` and `application` get `403 principal_not_permitted`.
- [ ] **IDN-03**: Agents and users can't reach Olive's service-caller path, and the proposal endpoint never accepts a service credential.
- [ ] **IDN-04**: `key_id`, `on_behalf_of`, `purpose`, `tenant_id` and `expires_at` from introspection are recorded on the proposal. `nono_session_id` is recorded as advisory only.
- [ ] **IDN-05**: A popeye token-issuer port, with a stub, returns `application`-type tokens carrying `tenant_id`, `delegated_agent_id`, `key_id` and an expiry.
- [ ] **IDN-06**: Service tokens are cached by `(tenant_id, delegated_agent_id, key_id)` until shortly before the earlier of the token's and the session key's expiry. A token is never reused for a new session key, and a token that expires after its session key is rejected and not cached.
- [ ] **IDN-07**: A token whose `delegated_agent_id` or `tenant_id` differs from Olive's own introspection of the session key is discarded, and the request fails with 503 and an alarm.
- [ ] **IDN-08**: A recording decorator on every outbound port proves the session key reaches only popeye. A sentinel-key search finds no session key or token in any log line (at Trace level), JSONL output or stored row.

### Trace verification (TRC)

- [ ] **TRC-01**: A `TraceResolver` port has a fixture-backed implementation in `contracts/trace-resolution/`. Its fixtures are schema-checked against the seven-field response (`trace_id`, `schema_version`, `run_id`, `tenant_id`, `agent_id`, `proposal_hash`, `created_at`).
- [ ] **TRC-02**: A trace that is not found returns `422 trace_not_found` with `retryable: true`. Nothing is stored, so a retry under the same `run_id` that now resolves is accepted.
- [ ] **TRC-03**: A null or mismatched `run_id` is refused and stored as `refused_policy` with reason `trace_unresolved`. `trace_schema_version` is recorded.
- [ ] **TRC-04**: A tenant mismatch is refused and stored as `refused_policy` with reason `trace_tenant_mismatch`.
- [ ] **TRC-05**: An `agent_id` that is null or differs from the token's `delegated_agent_id` is refused and stored as `refused_policy` with reason `trace_agent_mismatch`.
- [ ] **TRC-06**: Olive computes `proposal_hash` as JCS SHA-256 over exactly `action_type`, `party_ref`, `parameters` and `justification`, as submitted. A null or different hash is refused and stored as `refused_policy` with reason `trace_proposal_mismatch`.
- [ ] **TRC-07**: If the resolver is unavailable or times out, Olive fails closed with 503. If hometown or popeye returns 401 or 403 to Olive, it fails closed with 503 and an operational alarm. Nothing is stored in either case.
- [ ] **TRC-08**: A positive resolution is cached indefinitely by `(tenant_id, trace_id)`. 404, 503, 401 and 403 are never cached, and a 404 followed by a 200 reaches the resolver again.
- [ ] **TRC-09**: Every proposal records `trace_verification ∈ {fixture, hometown}`, taken from the resolver that answered. The schema rejects any other value.

### Policy and lifecycle (POL)

- [ ] **POL-01**: A proposal whose `purpose` is not in the action's `required_purpose` is stored as `refused_policy` and never queued. (Charter REQ-ACT-02)
- [ ] **POL-02**: Authorization uses the intersection of the agent's and the `on_behalf_of` user's entitlements. Missing entitlement data fails closed.
- [ ] **POL-03**: Rate limits are per agent, per action, per window. They count accepted proposals only and are enforced in the database under an advisory lock.
- [ ] **POL-04**: Each policy evaluation appends a `policy_evaluated` event recording the outcome and reasons.
- [ ] **POL-05**: Retrying a stored refusal under the same key returns the same refusal without re-evaluating it.
- [ ] **POL-06**: Every lifecycle transition is a compare-and-set update paired with a `proposal_event` row. Illegal transitions are rejected. (`proposed` → `refused_policy` | `awaiting_approval` → `approved` | `rejected` → `executed` | `execution_failed` | `expired`)
- [ ] **POL-07**: Expiry defaults to 8 hours, with a maximum per action type. `expires_in_seconds` can only shorten it (Q3), and it is capped at the key's `expires_at`. `expires_at` is stored as an absolute time.
- [ ] **POL-08**: An expired proposal can't be approved or executed. It returns a distinct `proposal_expired` error, and the expiry transition is committed before the error is returned. (Charter REQ-ACT-05)

### Review queue (QUE)

- [ ] **QUE-01**: Queue items follow the IR-1 model: its states, `subject_ref` and `payload_ref` (references only, with a CHECK on reference schemes), and its decision history. An item that carries an inline payload is rejected. (Charter REQ-QUE-01)
- [ ] **QUE-02**: A source-controlled kind registry defines each kind's subtypes, decision set, required role and allowed reference schemes. The registered kinds are `action_approval` and `identity_review`, and runtime registration is impossible.
- [ ] **QUE-03**: A proposal that passes policy is enqueued as `action_approval` in the same transaction that persists it.
- [ ] **QUE-04**: A reviewer can list open items for their tenant. Expired action items are filtered out.
- [ ] **QUE-05**: A reviewer can claim an open item. Claiming is compare-and-set, so concurrent claims produce exactly one winner.
- [ ] **QUE-06**: Only the claimant can release their claim. The item returns to open, and the release is recorded (Q7).
- [ ] **QUE-07**: Only the claimant can decide. Every decision records `decided_by`, `decided_at` and `rationale`, and a decision missing `decided_by` or with a blank rationale is rejected. (Charter REQ-QUE-03)
- [ ] **QUE-08**: Resubmitting the same decision by the same reviewer is a no-op that returns the recorded decision. A conflicting decision is rejected.
- [ ] **QUE-09**: Each claim, release, decision and withdrawal is appended to `queue_item_event`, and the full history is available for audit.
- [ ] **QUE-10**: When a queued proposal expires, its item moves to `withdrawn` with reason `proposal_expired` (Q11).

### Approval rules (APR)

- [ ] **APR-01**: Approval is per proposal and needs an authenticated reviewer identity; an approval without one is rejected.
- [ ] **APR-02**: The approver may not be the proposal's `on_behalf_of` user. The check uses the explicit oid and tid mapping, and a missing or ambiguous mapping is refused.
- [ ] **APR-03**: `role:case_supervisor` is checked at decision time, not when the item is claimed.
- [ ] **APR-04**: An action_approval decision (approve or reject) updates the queue item and the proposal state in one transaction.
- [ ] **APR-05**: The reviewer's authentication source (`entra | stub`) is recorded with each decision.

### Execution and audit chain (EXE)

- [ ] **EXE-01**: An approved proposal is executed by the simulated executor after the approval commits. A startup sweep picks up any approved proposal that hasn't run yet.
- [ ] **EXE-02**: Execution is idempotent on `proposal_id`. A unique ledger row per `(tenant_id, proposal_id)` and a per-proposal lock ensure that a duplicate attempt produces one write and returns the first result. (Charter REQ-ACT-03)
- [ ] **EXE-03**: Catalog validity and expiry are checked again immediately before execution.
- [ ] **EXE-04**: The simulated executor writes `execution_mode = "simulated"` ledger rows and JSONL target output. It performs no network write.
- [ ] **EXE-05**: An executor failure records `execution_failed`, which is terminal in Phase 1. A fault-injection test covers failure between the ledger write and the state update.
- [ ] **EXE-06**: The full chain is queryable from an execution record through a `security_invoker` view: proposal, trace verification, policy decision, named approver and rationale, and execution. (Charter REQ-ACT-01)

### Reviewer UI (UI)

- [ ] **UI-01**: A reviewer can sign in through the OpenIdConnect handler, configured for Entra behind an interface. A DevReviewer identity provider is available for local and CI use and is barred in production.
- [ ] **UI-02**: A reviewer can see the open items in their tenant on a Razor Pages list page.
- [ ] **UI-03**: A reviewer can claim and release an item. These POSTs use antiforgery and post-redirect-get.
- [ ] **UI-04**: A reviewer can approve or reject with a required rationale.
- [ ] **UI-05**: A reviewer can view an item's audit history. It shows the four hashed fields, `body_sha256` and `proposal_hash`, plus a banner when the trace was verified by fixture.
- [ ] **UI-06**: The UI has no dashboards, bulk actions, SLA views or assignment rules.
- [ ] **UI-07**: A CI test runs the real OIDC handler against `mock-oauth2-server`. Every credential type is tried against every route group.

### Identity review kind (IRQ)

- [ ] **IRQ-01**: bluto (stub traffic) can create an `identity_review` / `candidate_pair` item following the IR-1 create-body rules. A body containing `tenant_id` or `producer` is rejected, and `link_challenge` is rejected.
- [ ] **IRQ-02**: A reviewer can accept, reject or defer an identity_review item, and the decision is recorded in its history.
- [ ] **IRQ-03**: bluto can read decisions back through the IR-1 polling mechanism, and can withdraw its own items as IR-1 defines.
- [ ] **IRQ-04**: The inbound identity_review path authenticates with a stubbed popeye service token; no live bluto traffic is enabled.

### Evidence and contract package (EVD)

- [ ] **EVD-01**: Every case in the D§16.8 and D§17.4 test tables, plus charter TC-ACT-01..06 and TC-QUE-01..04, has a named test that passes in CI.
- [ ] **EVD-02**: A scripted local beat 2 demo shows a proposal verified against a fixture trace being approved and executed in simulation, with the chain queried from the execution record.
- [ ] **EVD-03**: A scripted local beat 4 demo shows an out-of-purpose proposal being refused, its refusal traced, and the proposal never appearing in the queue.
- [ ] **EVD-04**: A hand-authored OpenAPI 3.1 at `contracts/proposal/v1/openapi.yaml` `$ref`s the v1 schemas, and a drift test checks it against the live routes.
- [ ] **EVD-05**: `contracts/proposal/v1/COMPATIBILITY.md` maps S-OL-1 to v1 field by field, including the 422 behaviour and the Q1–Q3 outcomes. Liam fixtures accompany it for the §3 joint review.
- [ ] **EVD-06**: A credential audit across repos shows zero write-capable credentials, Olive included. (Charter REQ-ACT-04)
- [ ] **EVD-07**: `docs/STATUS.md` is refreshed, Decision v0.1 is marked superseded, and migrations live under `sql/migrations/`.

## v2 Requirements

Deferred. Tracked but not in the current roadmap.

### Live integrations
- **LIVE-01**: The hometown resolver runs against `GET /v1/traces/{trace_id}/resolution`, and every demo proposal records `trace_verification = hometown` (D§14.6 exit criterion).
- **LIVE-02**: Real popeye introspection and token issuance replace the stubs once popeye ratifies D§17.1.
- **LIVE-03**: Wire-level session-key capture (WireMock plus DiagnosticListener) is added alongside the live transports.
- **LIVE-04**: Live bluto → Olive identity-review traffic, after the IR-1 §7–§8 gates.

### Phase 2 (real write)
- **P2-01**: A real `nexus_crm` executor with a vault-held, narrowly scoped credential, a named owner and a rotation procedure.
- **P2-02**: Retry of transient execution failures.
- **P2-03**: A cross-tenant audit role and production retention.
- **P2-04**: Teams notifications that link to the web UI.

## Out of Scope

| Feature | Reason |
|---------|--------|
| A real target-system write, connector, SDK or credential | Phase 2 needs a signed divisional agreement and CTO/CSO sign-off (D§2, AGENTS.md) |
| Runtime registration of action types or queue kinds | Catalog changes go through change management (Charter §3.1, REQ-ACT-06) |
| Approving inside Teams | Harder to audit (D§11) |
| Dashboards, bulk operations, SLAs, assignment rules | The charter §4 queue is deliberately minimal |
| Editing a proposal before approval | It would break the `proposal_hash` binding (D§17.2) |
| Standing, automatic or dual approval; irreversible actions | Charter §3.2: per-proposal approval only, and the single action is reversible |
| Withdrawing action proposals | Would change the Charter §3.2 lifecycle (Q12) |
| Lease timeouts or admin reassignment of claims | Ruled out under Q7 |
| Generating proposals, calling models, identity resolution, CRM business rules | Charter §2 "refuses to own" |
| A general tool registry, third-party tools, MCP governance | Excluded by Charter §5 |
| Anchoring the ledger to the zt-infra log | Open charter §6.5 question |
| Editing `contracts/vendored/vocabulary.yaml` | AGENTS.md; vendored contract |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| FND-01 | Phase 1 | Pending |
| FND-02 | Phase 1 | Pending |
| FND-03 | Phase 1 | Pending |
| FND-04 | Phase 1 | Pending |
| FND-05 | Phase 1 | Pending |
| FND-06 | Phase 1 | Pending |
| FND-07 | Phase 1 | Pending |
| DB-01 | Phase 1 | Pending |
| DB-02 | Phase 1 | Pending |
| DB-03 | Phase 1 | Partial (pattern established in Phase 1; tables completed in Phases 5/6) |
| DB-04 | Phase 1 | Partial (pattern established in Phase 1; tables completed in Phases 5/6) |
| DB-05 | Phase 1 | Pending |
| DB-06 | Phase 1 | Pending |
| DB-07 | Phase 1 | Pending |
| API-01 | Phase 1 | Pending |
| API-02 | Phase 1 | Pending |
| API-03 | Phase 2 | Pending |
| API-04 | Phase 2 | Pending |
| API-05 | Phase 2 | Pending |
| API-06 | Phase 2 | Pending |
| API-07 | Phase 2 | Pending |
| API-08 | Phase 4 | Pending |
| API-09 | Phase 2 | Pending |
| API-10 | Phase 2 | Pending |
| IDN-01 | Phase 1 | Pending |
| IDN-02 | Phase 1 | Pending |
| IDN-03 | Phase 8 | Pending |
| IDN-04 | Phase 1 | Pending |
| IDN-05 | Phase 3 | Pending |
| IDN-06 | Phase 3 | Pending |
| IDN-07 | Phase 3 | Pending |
| IDN-08 | Phase 3 | Pending |
| TRC-01 | Phase 3 | Pending |
| TRC-02 | Phase 3 | Pending |
| TRC-03 | Phase 3 | Pending |
| TRC-04 | Phase 3 | Pending |
| TRC-05 | Phase 3 | Pending |
| TRC-06 | Phase 3 | Pending |
| TRC-07 | Phase 3 | Pending |
| TRC-08 | Phase 3 | Pending |
| TRC-09 | Phase 3 | Pending |
| POL-01 | Phase 4 | Pending |
| POL-02 | Phase 4 | Pending |
| POL-03 | Phase 4 | Pending |
| POL-04 | Phase 4 | Pending |
| POL-05 | Phase 4 | Pending |
| POL-06 | Phase 4 | Pending |
| POL-07 | Phase 4 | Pending |
| POL-08 | Phase 5 | Pending |
| QUE-01 | Phase 5 | Pending |
| QUE-02 | Phase 5 | Pending |
| QUE-03 | Phase 5 | Pending |
| QUE-04 | Phase 5 | Pending |
| QUE-05 | Phase 5 | Pending |
| QUE-06 | Phase 5 | Pending |
| QUE-07 | Phase 5 | Pending |
| QUE-08 | Phase 5 | Pending |
| QUE-09 | Phase 5 | Pending |
| QUE-10 | Phase 5 | Pending |
| APR-01 | Phase 5 | Pending |
| APR-02 | Phase 5 | Pending |
| APR-03 | Phase 7 | Pending |
| APR-04 | Phase 5 | Pending |
| APR-05 | Phase 5 | Pending |
| EXE-01 | Phase 6 | Pending |
| EXE-02 | Phase 6 | Pending |
| EXE-03 | Phase 6 | Pending |
| EXE-04 | Phase 6 | Pending |
| EXE-05 | Phase 6 | Pending |
| EXE-06 | Phase 6 | Pending |
| UI-01 | Phase 7 | Pending |
| UI-02 | Phase 7 | Pending |
| UI-03 | Phase 7 | Pending |
| UI-04 | Phase 7 | Pending |
| UI-05 | Phase 7 | Pending |
| UI-06 | Phase 7 | Pending |
| UI-07 | Phase 7 | Pending |
| IRQ-01 | Phase 8 | Pending |
| IRQ-02 | Phase 8 | Pending |
| IRQ-03 | Phase 8 | Pending |
| IRQ-04 | Phase 8 | Pending |
| EVD-01 | Phase 9 | Pending |
| EVD-02 | Phase 6 | Pending |
| EVD-03 | Phase 5 | Pending |
| EVD-04 | Phase 9 | Pending |
| EVD-05 | Phase 9 | Pending |
| EVD-06 | Phase 9 | Pending |
| EVD-07 | Phase 9 | Pending |

**Coverage:**
- v1 requirements: 88 total
- Mapped to phases: 88
- Unmapped: 0

---
*Requirements defined: 2026-10-21*
*Last updated: 2026-10-21 after roadmap creation*
