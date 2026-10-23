# Roadmap: Olive — Action Broker and Review Queue (Phase 1, simulation-only)

## Overview

Olive Phase 1 goes from a single-file in-memory scaffold to a governed action broker. The broker proves the core-value chain from the execution record: verified trace, then policy before queue, then a named approver with a rationale, then exactly-once simulated execution. The build is nine vertical slices, each testable end to end against Testcontainers Postgres. The foundation and both tenant-isolation layers come first, because the traps that let controls pass silently are cheap to prevent only there. Phases 2-4 then follow the ratified submission pipeline order (contract/idempotency, then trace, then policy). Phase 5 adds the IR-1 generic queue and action approval, which completes beat 4. After Phase 5 three phases can run in parallel: simulated execution (beat 2 through the API), the reviewer UI with Entra OIDC, and the identity_review queue kind. A final phase assembles the contract package and evidence for the D§3 joint review.

**Standing preconditions (every phase):**

- **D§4 stop rule.** The referenced specs must be present when a phase runs: `~/fiskroad/specs/` (POPEYE_v0.4.2, NONO_v0.4.x, CROSS_COMPONENT_EXECUTION_SPEC.md, the agent-key decisions, PROGRAM_SHARED_VOCABULARY_v0.4) and `~/liam/specs/`. If any is missing, the implementer stops and reports it. This bites hardest in Phases 1, 3 and 4.
- No write-capable credential, connector, SDK or HTTP client to a target system (AGENTS.md, REQ-ACT-04).
- `contracts/vendored/vocabulary.yaml` is never edited. Queue payloads are references only. The catalog and queue kinds are source-controlled, with no runtime registration.

## Phases

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Foundation and Walking Skeleton** - Restructured solution, guards, both tenant-isolation layers, migrator, and an agent can submit and read a proposal through the introspection stub (completed 2026-10-21)
- [x] **Phase 2: Contract Integrity and Idempotent Submission** - Strict raw-byte validation, v1 schemas, in-house JCS, YamlDotNet catalog endpoint, and race-safe idempotency on (agent_id, run_id)
 (completed 2026-10-22)
- [x] **Phase 3: Trace Verification** - Fixture-backed trace resolver with ordered checks 1-5, fail-closed 503s, popeye token stub and cache, and session-key capture (completed 2026-10-22)
- [ ] **Phase 4: Policy, Expiry and Refusal** - Purpose, entitlement and rate-limit policy before any queueing, guarded lifecycle transitions, absolute expiry, and the asserted pipeline order
- [ ] **Phase 5: Generic Review Queue and Action Approval** - IR-1 queue with kind registry, claim/release/decide/audit, approval rules, lazy expiry withdrawal, and the beat 4 demo
- [ ] **Phase 6: Simulated Execution and Audit Chain** - Exactly-once simulated execution, INSERT-only ledger, chain view, and the beat 2 demo through the API
- [ ] **Phase 7: Reviewer UI and Entra OIDC** - Minimal Razor Pages reviewer UI with OIDC behind an interface, a dev IdP stub, and a role check at decision time
- [ ] **Phase 8: Identity Review Queue Kind** - The identity_review / candidate_pair kind on stub bluto traffic, with a service-token inbound path and IR-1 polling
- [ ] **Phase 9: Contract and Evidence Pack** - OpenAPI 3.1 with drift test, COMPATIBILITY.md, Liam fixtures, full named test suite, credential audit, and refreshed docs

## Phase Details

### Phase 1: Foundation and Walking Skeleton

**Goal**: An identified agent can submit a proposal and read it back through a real ASP.NET Core host on Postgres, with tenant isolation enforced by both layers. Every silent-pass trap (superuser tests, pooled tenant settings, leaking secrets, stubs in production) is closed off before any feature work.
**Mode:** mvp
**Depends on**: Nothing (first phase)
**Requirements**: FND-01, FND-02, FND-03, FND-04, FND-05, FND-06, FND-07, DB-01, DB-02, DB-03, DB-04, DB-05, DB-06, DB-07, API-01, API-02, IDN-01, IDN-02, IDN-04
**Success Criteria** (what must be TRUE):

  1. An agent with an allowlisted `liam` key can `POST /v1/proposals` and get back a `proposal_id` and lifecycle state. The stored row records `key_id`, `on_behalf_of`, `purpose`, `tenant_id`, `expires_at` and an advisory `nono_session_id`. A user or application key gets `403 principal_not_permitted`, and an inactive key gets 401 before anything else runs.
  2. `GET /v1/proposals/{id}` returns the proposal within its own tenant and 404 for an id in another tenant. Negative tests run as `olive_app` with two seeded tenants and `Max Pool Size=1`. They remove the application predicate, then RLS, and in each case the other layer still blocks the cross-tenant read and write. An unset tenant raises an error.
  3. The migrator applies `sql/migrations/0001_*` as `olive_owner`, records it in `schema_migrations`, and fails on a changed applied file. A privilege-matrix test shows `proposal_event` is INSERT-only for `olive_app` (UPDATE and DELETE denied). This is the pattern later migrations extend to `queue_item_event` and `execution_ledger`.
  4. The service refuses to start in each of these cases: `OLIVE_ENVIRONMENT` unset; `production` with any `INonProductionOnly` implementation registered; a superuser, BYPASSRLS or owner database role; a migration version mismatch; a vendored `vocabulary.yaml` SHA-256 mismatch.
  5. Ubuntu CI builds the restructured solution (locked packages) and runs unit, architecture and Testcontainers tests. A Domain/Application/Executor reference to an HTTP client or an off-allowlist package fails the build. Sealed session-key and token types render redacted in `ToString`, logs, exceptions and serialized output.

**Plans**: 10 plans (7 executed, 3 gap closure)
**Wave 1**

- [x] 01-01-PLAN.md — Scaffold restructure: .gitattributes, CPM and lockfiles, banned APIs, 11 projects, FND-01/02/03 tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 01-02-PLAN.md — Role bootstrap, migrator, migration 0001 (proposal, proposal_event), Testcontainers fixture, DB-01..04 tests
- [x] 01-03-PLAN.md — Redacting secret types, introspection port and fixture stub, agent-key header, agents.yaml allowlist

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 01-04-PLAN.md — Tenant unit of work, proposal store, layer-ablation isolation tests (DB-05, DB-06)

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 01-05-PLAN.md — Walking skeleton slice: POST/GET /v1/proposals with introspection and principal checks

**Wave 5** *(blocked on Wave 4 completion)*

- [x] 01-06-PLAN.md — Startup guards (environment, stubs, vocabulary hash, DB role and migrations), .env loader, secret-leak tests

**Wave 6** *(blocked on Wave 5 completion)*

- [x] 01-07-PLAN.md — GitHub Actions CI, .env.example, README dev flow, open coordination items

**Gap closure, Wave 1** *(from 01-VERIFICATION.md and 01-REVIEW.md; independent)*

- [x] 01-08-PLAN.md — CR-01: lone-surrogate/NUL escapes give 400 invalid_request, not 500 (API-01, D-07)
- [x] 01-09-PLAN.md — WR-02: migration 0002 limits olive_app UPDATE on proposal to the state column (DB-04)
- [x] 01-10-PLAN.md — WR-05: CI vulnerable-package gate fails closed (FND-07)

**Notes**: D§4 stop rule: confirm POPEYE_v0.4.2 is present before shaping the introspection stub (and the Q4 entitlement claim shape). Reconcile `db/migrations/` to `sql/migrations/` before a second migration exists. Add `.gitattributes` before any hashed fixture is committed. Spike: Postgres major version of `deploy-postgres-1` (15+ required), Npgsql URI connection strings. Requirements DB-03 and DB-04 name all five tables. This phase establishes the RLS/grant pattern and its tests, and Phases 5 and 6 must apply the same pattern to their migrations.

### Phase 2: Contract Integrity and Idempotent Submission

**Goal**: A submitted body is validated exactly as received and never mutated. Resubmissions converge on one proposal through a canonical JCS hash, and the source-controlled catalog is served from YAML.
**Depends on**: Phase 1
**Requirements**: API-03, API-04, API-05, API-06, API-07, API-09, API-10
**Success Criteria** (what must be TRUE):

  1. Resubmitting an identical body under the same `(agent_id, run_id)` returns 200 with the original `proposal_id` and current state. A different body returns `409 idempotency_conflict`, is not stored, and is logged. Two concurrent first submissions produce exactly one proposal.
  2. A body with duplicate keys, a null optional field, or a value that would need coercion, defaults, stripping or trimming to validate is rejected with 400, and nothing is stored. Per Q1, an unparseable or non-I-JSON body, or a missing or invalid `run_id`, gets 400 before the idempotency check.
  3. `GET /v1/catalog/{version}` returns the `crm.case_note@1` entry with its parameter JSON Schema, loaded via YamlDotNet and validated at startup. The hand-rolled parser is gone, and no runtime registration path exists.
  4. The in-house JCS passes the RFC 8785 and cyberphone vectors. A shared golden-vector set is committed under `contracts/` for hometown to run (Q20).

**Plans**: 7 plans
**Wave 1**

- [x] 02-01-PLAN.md — In-house RFC 8785 JCS, lossless-number rule, golden vectors in contracts/jcs/v1 (API-09)
- [x] 02-02-PLAN.md — JsonSchema.Net 8.0.5, SchemaValidator, versioned contracts and v1 proposal schemas (API-05)
- [x] 02-03-PLAN.md — Migration 0004 (canonical_body, body_sha256, proposal_hash) and tenant-scoped FindByRun (API-06, API-07)

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 02-04-PLAN.md — Whole-body I-JSON gate: duplicate keys, lossy numbers, surrogate-name safety (API-04)
- [x] 02-05-PLAN.md — YamlStream catalog loader, manifest, D-02 vetting, GET /v1/catalog/{version} (API-03, API-10)

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 02-06-PLAN.md — Envelope, catalog and parameter schema validation on POST; Phase 1 fixtures updated (API-04, API-05, API-10)

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 02-07-PLAN.md — Idempotent submission: replay 200, conflict 409, race-safe, stored hashes (API-06, API-07)

**Notes**: Working defaults Q1 and Q2 apply (envelope, then catalog lookup, then parameter schema). The `not_in_catalog` stored refusal is delivered in Phase 4 with the refusal machinery. Spikes: `JsonDocumentOptions.AllowDuplicateProperties`, lone surrogates, the privileges `ON CONFLICT DO NOTHING` needs.

### Phase 3: Trace Verification

**Goal**: Only a proposal whose trace resolves to the same tenant, agent, run and content hash can proceed. Every resolver or auth failure fails closed, and the session key provably reaches only popeye.
**Mode:** mvp
**Depends on**: Phase 2
**Requirements**: TRC-01, TRC-02, TRC-03, TRC-04, TRC-05, TRC-06, TRC-07, TRC-08, TRC-09, IDN-05, IDN-06, IDN-07, IDN-08
**Success Criteria** (what must be TRUE):

  1. A proposal whose fixture trace matches on `run_id`, tenant, agent and `proposal_hash` is accepted and records `trace_verification = fixture` and `trace_schema_version`. A run_id, tenant, agent or hash mismatch is stored as `refused_policy` with the matching reason code (`trace_unresolved`, `trace_tenant_mismatch`, `trace_agent_mismatch`, `trace_proposal_mismatch`), checked in that order.
  2. An unknown trace returns `422 trace_not_found` with `retryable: true` and stores nothing. A retry under the same `run_id` after the fixture appears is accepted, because the not-found result was never cached. Positive resolutions are served from the `(tenant_id, trace_id)` cache.
  3. A resolver that is unavailable or times out yields 503 with nothing stored. A 401 or 403 from the hometown or popeye stub yields 503 plus an alarm a test can assert on.
  4. Service tokens from the popeye stub are cached by `(tenant_id, delegated_agent_id, key_id)` and never outlive the session key. A token whose claims disagree with Olive's introspection is discarded, and the request fails with 503 and an alarm.
  5. Recording decorators on every outbound port show the session key going only to popeye. A sentinel-key search at Trace log level, over JSONL output and stored rows, finds no session key or token.

**Plans**: 6 plans
**Wave 1**

- [x] 03-01-PLAN.md — Migration 0006, InsertRefusedAsync, additive response contract (TRC-03, TRC-09)
- [x] 03-02-PLAN.md — Ports, token provider and cache, trace cache, TraceVerifier with unit tests (TRC-02..08, IDN-05..07)

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 03-03-PLAN.md — Slice: fixture-verified proposal accepted (fixtures, resolver, popeye token stub, endpoint, auto-fixture harness) (TRC-01, TRC-06, TRC-09, IDN-05)

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 03-04-PLAN.md — Slice: stored trace refusals with reasons, 201 body, final replay (TRC-03..06)

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 03-05-PLAN.md — Slice: fail-closed 422/503, alarms, introspection caller-rejected (TRC-02, TRC-07, TRC-08, IDN-06, IDN-07)

**Wave 5** *(blocked on Wave 4 completion)*

- [x] 03-06-PLAN.md — Slice: session-key containment evidence and coordination items (IDN-08)

**Research flag**: Run `/gsd-plan-phase 3 --research-phase`. Topics: the popeye stub against POPEYE_v0.4.2, and the JCS golden set coordination with hometown.
**Notes**: D§4 stop rule: POPEYE_v0.4.2 must be present. Q19 default: port-level capture now, wire-level later (LIVE-03).

### Phase 4: Policy, Expiry and Refusal

**Goal**: Policy decides before any human sees a proposal. Out-of-purpose, unentitled or rate-limited proposals are stored as refusals and never queued, every lifecycle transition is guarded, and the full submission order is enforced and asserted.
**Mode:** mvp
**Depends on**: Phase 3
**Requirements**: POL-01, POL-02, POL-03, POL-04, POL-05, POL-06, POL-07, API-08
**Success Criteria** (what must be TRUE):

  1. A proposal whose `purpose` is not in the action's `required_purpose` is stored as `refused_policy` and is never eligible for queueing. So is an unknown action (`not_in_catalog`). Each evaluation appends a `policy_evaluated` event with its outcome and reasons.
  2. Authorization uses the intersection of agent and `on_behalf_of` entitlements, and missing entitlement data fails closed. The per-agent, per-action rate limit counts accepted proposals only (refusals do not consume it) and holds under concurrent submissions.
  3. Retrying a stored refusal under the same key returns the same refusal without re-evaluating it.
  4. Every lifecycle transition is a compare-and-set paired with a `proposal_event` row, and illegal transitions are rejected. `expires_at` is stored as an absolute time: 8 hours by default, shortened only by `expires_in_seconds`, capped at the per-action maximum and the key's `expires_at`. An out-of-range `expires_in_seconds` gets 400 (Q3).
  5. A test asserts the fixed order (introspect → principal → idempotency → schema/catalog → trace → policy → persist), and a request with several faults gets the first failure's outcome.

**Plans**: 7 plans
**Wave 1**

- [x] 04-01-PLAN.md — Q4 ruling recorded; catalog required_entitlement/max_expiry_seconds and agent entitlements (POL-02, POL-07)

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 04-02-PLAN.md — Slice: unknown-action and wrong-purpose stored refusals with policy_evaluated events (POL-01, POL-04, POL-05)

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 04-03-PLAN.md — Slice: migration 0007, expires_in_seconds, absolute expires_at, GET exposes it (POL-07)

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 04-04-PLAN.md — Directory-group resolver port, fail-closed verifier, entitlement intersection, fixture stub and contracts/directory (POL-02)

**Wave 5** *(blocked on Wave 4 completion)*

- [ ] 04-05-PLAN.md — Slice: directory DI and production guard, endpoint entitlement step, fail-closed 503 (POL-02)

**Wave 6** *(blocked on Wave 5 completion)*

- [ ] 04-06-PLAN.md — Slice: transition table, advisory-lock rate limit, accept to awaiting_approval (POL-03, POL-04, POL-06)

**Wave 7** *(blocked on Wave 6 completion)*

- [ ] 04-07-PLAN.md — API-08 submission-order test, replay matrix, contract docs (API-08, POL-05, POL-06)


**Notes**: D§4 stop rule: the entitlement source must exist in POPEYE_v0.4.2 or vocabulary §3.2 (Q4). If it is in neither, stop. This phase fixes the scaffold defects from CONCERNS.md: an unknown action throwing, null grants failing open, refusals consuming the rate limit, no approval guard.

### Phase 5: Generic Review Queue and Action Approval

**Goal**: A proposal that passes policy appears in a reference-only IR-1 review queue. A named, authenticated reviewer other than the requester can claim it and approve or reject it with a rationale, and every queue action is auditable.
**Mode:** mvp
**Depends on**: Phase 4
**Requirements**: QUE-01, QUE-02, QUE-03, QUE-04, QUE-05, QUE-06, QUE-07, QUE-08, QUE-09, QUE-10, APR-01, APR-02, APR-04, APR-05, POL-08, EVD-03
**Success Criteria** (what must be TRUE):

  1. A proposal that passes policy is enqueued as `action_approval` in the same transaction that persists it. The item carries only `subject_ref` and `payload_ref`, and an inline payload is rejected. A reviewer (dev identity) listing their tenant's open items sees it and does not see expired items.
  2. A reviewer can claim an item (concurrent claims give exactly one winner) and release only their own claim. Only the claimant can decide. A decision without `decided_by` or with a blank rationale is rejected. Resubmitting the same decision is a no-op, and a conflicting decision is rejected.
  3. An approval by the proposal's `on_behalf_of` user, or with a missing or ambiguous oid/tid mapping, is refused. A valid approve or reject updates the queue item and proposal state in one transaction and records the auth source (`stub`).
  4. Approving an expired proposal returns a distinct `proposal_expired` error after the expiry transition is committed, and its queue item moves to `withdrawn` with reason `proposal_expired`. Every claim, release, decision and withdrawal appears in the item's `queue_item_event` history.
  5. A scripted beat 4 demo shows an out-of-purpose proposal refused, its refusal traceable, and the proposal never appearing in the queue.

**Plans**: TBD
**Research flag**: Run `/gsd-plan-phase 5 --research-phase`. Topics: how action_approval maps onto IR-1 states and decisions (Q10), and the `queue_item_event` grants.
**Notes**: Rulings Q7 (claimant-only release), Q9 (`queue_item_event` INSERT-only, five tables), Q11 (lazy `withdrawn`/`proposal_expired`) and Q13 (server-side group-to-tenant map) are settled and shape migration 0002. Q8, Q10 and Q14 working defaults apply. Migration 0002 must follow the Phase 1 RLS/grant pattern and extend the privilege-matrix test. The decision-time role check (APR-03) arrives with the real IdP in Phase 7.

### Phase 6: Simulated Execution and Audit Chain

**Goal**: An approved proposal executes exactly once in simulation, and the whole core-value chain can be queried from the execution record.
**Mode:** mvp
**Depends on**: Phase 5 (can run in parallel with Phases 7 and 8)
**Requirements**: EXE-01, EXE-02, EXE-03, EXE-04, EXE-05, EXE-06, EVD-02
**Success Criteria** (what must be TRUE):

  1. After an approval commits, the simulated executor runs it outside the approval transaction and writes an `execution_mode = "simulated"` ledger row and JSONL output, with no network write. A restart sweep runs any approved proposal not yet executed.
  2. Duplicate or concurrent execution attempts for one proposal produce one ledger row and one target write, and every attempt returns the first result. `execution_ledger` is INSERT-only for `olive_app` in the privilege-matrix test.
  3. A proposal whose catalog entry is no longer valid, or which has expired by execution time, is not executed. An executor failure, including a fault injected between the ledger write and the state update, ends in terminal `execution_failed`.
  4. One query against a `security_invoker` view, starting from an execution record, returns the proposal, trace verification, policy decision, named approver with rationale, and execution result.
  5. A scripted beat 2 demo submits a fixture-traced proposal, approves it, executes it in simulation, and prints the chain from the execution record.

**Carry-over from Phase 3 (IDN-08)**: extend the sentinel-key search to the simulated executor JSONL output (reuse `SentinelScan.AddSource`); Phase 3 covered logs, responses and stored rows only.
**Plans**: TBD
**Research flag**: Run `/gsd-plan-phase 6 --research-phase`. Topics: the execution trigger, holding a per-proposal lock across the executor, and `execution_failed` semantics.
**Notes**: Working defaults Q5 (in-process after commit plus startup sweep), Q6 (terminal failure), Q16 (re-check catalog and expiry, not entitlements) and Q18 (view, no new API endpoint) apply.

### Phase 7: Reviewer UI and Entra OIDC

**Goal**: A reviewer signs in through OIDC and works the queue in a deliberately minimal web UI. The approver role is verified at the moment of decision.
**Mode:** mvp
**Depends on**: Phase 5 (can run in parallel with Phases 6 and 8)
**Requirements**: UI-01, UI-02, UI-03, UI-04, UI-05, UI-06, UI-07, APR-03
**Success Criteria** (what must be TRUE):

  1. A reviewer can sign in through the OpenIdConnect handler, or locally and in CI through the DevReviewer IdP, which is refused in production. Their tenant comes only from the server-side group-to-tenant map (Q13).
  2. A signed-in reviewer can see their tenant's open items, claim and release an item, and approve or reject it with a required rationale. Every POST uses antiforgery and post-redirect-get.
  3. A reviewer whose `role:case_supervisor` was removed after claiming is refused at decision time.
  4. The audit page shows the item history, the four hashed fields, `body_sha256` and `proposal_hash`, and a banner when the trace was verified by fixture. The UI has no dashboards, bulk actions, SLA views or assignment rules.
  5. A CI test runs the real OIDC handler against `mock-oauth2-server`, trying every credential type against every route group. Only the intended pairing is admitted.

**Plans**: TBD
**UI hint**: yes
**Research flag**: Run `/gsd-plan-phase 7 --research-phase`. Topics: Entra app role plus `max_age` versus Graph, group overage, the oid mapping, SameSite and HTTPS.
**Notes**: Q14 and Q15 working defaults apply. Live approvals stay blocked until the `role:case_supervisor` Entra group is named. That is an external dependency and does not block phase completion against the stub and mock-oauth2-server.

### Phase 8: Identity Review Queue Kind

**Goal**: The shared queue serves a second consumer. bluto (stub traffic only) can file identity_review items under the IR-1 contract, reviewers can decide them, and bluto can read the outcomes back over a service-only path.
**Mode:** mvp
**Depends on**: Phase 5 (can run in parallel with Phases 6 and 7)
**Requirements**: IRQ-01, IRQ-02, IRQ-03, IRQ-04, IDN-03
**Success Criteria** (what must be TRUE):

  1. A stubbed bluto service token can create an `identity_review` / `candidate_pair` item. A body containing `tenant_id` or `producer` is rejected, and so is `link_challenge`.
  2. A reviewer can accept, reject or defer an identity_review item, and the decision appears in its history.
  3. bluto can poll decisions through the IR-1 mechanism and withdraw its own items.
  4. Agent and user credentials are refused on the service-caller path, and the proposal endpoint refuses service credentials. No live bluto traffic is enabled.

**Plans**: TBD
**Notes**: Uses the Q7 claimant-only release (identity items never expire). IR-1 contract changes need agreement from both the olive and bluto owners.

### Phase 9: Contract and Evidence Pack

**Goal**: The milestone is provable. Every ratified test case has a named passing test, and the contract package and credential evidence are ready for the D§3 joint review.
**Mode:** mvp
**Depends on**: Phase 6, Phase 7, Phase 8
**Requirements**: EVD-01, EVD-04, EVD-05, EVD-06, EVD-07
**Success Criteria** (what must be TRUE):

  1. Every case in the D§16.8 and D§17.4 tables, plus TC-ACT-01..06 and TC-QUE-01..04, maps to a named test that passes in CI.
  2. A hand-authored `contracts/proposal/v1/openapi.yaml` (OpenAPI 3.1) `$ref`s the v1 schemas, and a drift test fails if it diverges from the live routes.
  3. `contracts/proposal/v1/COMPATIBILITY.md` maps S-OL-1 to v1 field by field, including the 422 behaviour and the Q1-Q3 outcomes. Liam fixtures sit alongside it.
  4. A credential audit across repos shows zero write-capable credentials, Olive included. `docs/STATUS.md` reflects reality, Decision v0.1 is marked superseded, and migrations live only under `sql/migrations/`.

**Plans**: TBD
**Notes**: D§4 stop rule: the Liam specs in `~/liam/specs/` must be present to author fixtures and the compatibility map.

## Parallelization

Phases 6, 7 and 8 each depend only on Phase 5 and can run in parallel. Phase 9 waits for all three.

```
1 → 2 → 3 → 4 → 5 ─┬→ 6 ─┐
                   ├→ 7 ─┼→ 9
                   └→ 8 ─┘
```

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → (6 | 7 | 8) → 9

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation and Walking Skeleton | 13/13 | Complete    | 2026-10-22 |
| 2. Contract Integrity and Idempotent Submission | 7/7 | Complete    | 2026-10-22 |
| 3. Trace Verification | 6/6 | Complete    | 2026-10-22 |
| 4. Policy, Expiry and Refusal | 4/7 | In Progress|  |
| 5. Generic Review Queue and Action Approval | 0/TBD | Not started | - |
| 6. Simulated Execution and Audit Chain | 0/TBD | Not started | - |
| 7. Reviewer UI and Entra OIDC | 0/TBD | Not started | - |
| 8. Identity Review Queue Kind | 0/TBD | Not started | - |
| 9. Contract and Evidence Pack | 0/TBD | Not started | - |
