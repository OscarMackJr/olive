# Decision Record — Olive POC-2 Phase 1 (pre-implementation questions)

Version: 0.5.2
Status: §1–§17 **ratified 2026-10-20**. §17 amends §16.1 and §16.2, which are marked inline with "Amended by §17"; where they differ, **§17 governs**. hometown has agreed to §17.5 in full. popeye has acknowledged the §17.1 dependency as **proposed on popeye's side, not ratified or approved for implementation there**. The live resolver stays blocked on it. The 0.5.2 principal-type correction (§7, §17.1) was confirmed by the ratifier on 2026-10-20.

**Changes in 0.5:** new §17, from the review of `HOMETOWN_v0.4` §2.5–§2.6. Two defects in the ratified response contract: (1) olive serves every tenant, so a resolution call whose tenant comes "only from the caller's credential" cannot work with olive's untenanted managed identity. This was an error in this record's §14.5, carried into §16.1. (2) The trace does not bind the proposal, so a submitted proposal could be altered and still present a valid `trace_ref`. §17 fixes both, adds a popeye dependency, and extends the check order.

**Changes in 0.5.2:** records hometown's agreement to §17.5 (2026-10-20) and popeye's acknowledgement of the §17.1 dependency (2026-10-20, proposed on popeye's side). Corrects the principal type: the shared vocabulary allows only `user`, `application` and `agent`, so olive's credentials are `application`, not the `service` value this record had used (§7, §17.1); **confirmed by Oscar Mack, 2026-10-20**. Renumbers §17.5's hometown list. Annotates §16.3, whose reasoning predates the invariant 4 exception. Records hometown's stronger enablement rule (the server refuses to start without Postgres and forced RLS).

**Changes in 0.5.1:** §17.1 adopts reviewer amendments: delegated agent binding (popeye derives tenant and agent from the presented session key and issues a token carrying an immutable `delegated_agent_id`); the session key goes only to popeye; non-mutating validation before hashing. The token cache is keyed on the session key's `key_id` too, and the token's lifetime is capped at the key's expiry, so a token cannot outlive the session that justified it. Check 4 now states its reference value explicitly.

**Changes in 0.4.3 (editorial; no decision changed):** records the stated location of the two decision records the Liam specification depends on. Every §4 document now has a recorded location.

**Changes in 0.4.2 (editorial; no decision changed):** records the CTO (Kamal Shah) and CSO (Chris Planell) as informed parties, and the stated location of the cross-component execution specification.

**Changes in 0.4.1 (editorial; no decision changed):** names the §3 independent reviewer, James O'Meara (jomeara@twgglobal.com), and records the stated location of the popeye and nono specifications in the §4 table.

**Changes in 0.4:** the hometown owner acknowledged the §14.5 request on 2026-10-20 (relayed by Oscar Mack) with five changes to the response shape. New §16 adopts four of them as written and adjusts the fifth (`trace_verification`). It also fixes a defect in ratified §15.2: a trace that is not found *yet* produced a stored, permanent refusal. Finally it records two requests on hometown (trace committed before an agent answer returns; endpoint only on the Postgres store) and the open dependencies for the ATE v0.4 joint review.

**Changes in 0.3:** §13 corrected: the shared POC Postgres is the external `deploy-postgres-1` container, not a hometown Compose service. Runbook v0.3 §3.1 was wrong, and the correction is taken from hometown's own `specs/STATE.md` and `cube/README.md`. The §13 grant table gains `proposal_event`. New §15 settles four implementer questions: agent mismatch on trace, the order of the idempotency check, body canonicalization, and the connection variables.

**Changes in 0.2:** new §13 (Phase 1 Postgres) and §14 (trace verification while hometown's lookup contract is open).
Repository: `fiskroad/specs/decisions/` (coordination repo), linked from `olive/specs/`.
Answers: the twelve questions raised by the olive implementer against `PROMPT_OLIVE.md`.
Primary post-read action: ratify or amend §1–§4. Phase 1 may start in **simulation-only** mode once §1 is ratified and the §4 artifacts are placed. Nothing that holds a write credential starts under this record.

---

## Summary

| # | Question | Status today | Proposed decision |
|---|---|---|---|
| 1 | Olive existence and §2 boundary approved? | **No.** No approval record exists | Ratify §6.1–6.3 of the program charter and olive §2 in this record |
| 2 | Write agreement and credential available? | **No.** Entry criterion 4 not started | Phase 1 is **simulation-only** |
| 3 | Liam as submitter approved? Liam owner? | Ratified on Liam's side (D6); olive-side confirmation open (Liam O6) | Amend the olive charter §2 wording; joint review: Oscar plus one independent reviewer |
| 4 | Where are the referenced specs? | Liam specs exist; the others are **not in any artifact available here** | Place them in `fiskroad/specs/` before the prompt runs |
| 5 | Target system and action | Open (charter §6 item 3) | `crm.case_note@1` on `nexus_crm` |
| 6 | Versioning and idempotency | Unspecified | `/v1/proposals`; idempotent on `(agent_id, run_id)`; changed content returns 409 |
| 7 | Introspection claims | Defined in `POPEYE_v0.4.2` (not present) | Use only the claims that spec names; minimum set below |
| 8 | Approvers and `role:case_supervisor` | Approver population not named (entry criterion 3) | Entra group, authenticated reviewer, no self-approval, rationale required |
| 9 | Expiry default | "Measured in hours" | 8 h default; submitter may shorten, never lengthen |
| 10 | Persistence | In-memory scaffold plus JSONL | Postgres with `tenant_id` and RLS from the start; in-memory for unit tests only |
| 11 | Queue frontend | Open (charter §6 item 4) | Minimal web UI |
| 12 | Contract source of truth | Unspecified | JSON Schema for payloads; OpenAPI 3.1 referencing those schemas; versioned directory |

---

## 1. Olive's existence and boundary

**Status.** `OLIVE_CHARTER_v0.4` is a draft that "requires approval per `PROGRAM_POC2_CHARTER_v0.4` §6.1", and the program charter is itself a draft awaiting approval. No ratification record exists for either. The implementer cannot approve its own boundary, and the prompt's objective "Approve Olive's component boundary" should be read as "obtain approval", not "grant it".

**Proposed decision.**
- Ratify `PROGRAM_POC2_CHARTER_v0.4` §6.1 (charter a new component), §6.3 (shared review queue lives in olive) and `OLIVE_CHARTER_v0.4` §2 (owns / refuses to own), unchanged except for the §3 amendment below.
- §6.2 codename: keep `olive`.
- **Ratifier:** Oscar Mack, consistent with the Liam v0.4 package ("architecture and risk decisions"). Because olive holds the program's only write credential, inform the CTO and CSO at ratification; their sign-off becomes **required** before Phase 2 (§2).
- **Entry criteria note.** Program charter §5 says no v0.4 work starts until four criteria hold, and 2, 3 and 4 are not met. Ratifying this record grants a **documented exception** for simulation-only olive work (contract, policy, queue, simulated executor). It does **not** waive the criteria for Phase 2.

## 2. Write agreement, credential, and the phase boundary

**Status.** Entry criterion 4 (one signed divisional write agreement, one system, one action type) is recorded as "Not started". No scoped target-system credential exists, and none should be created under this record.

**Proposed decision: Phase 1 is simulation-only.**
- The executor is a simulated executor behind the same interface the real one will implement. It writes an execution-ledger row with `execution_mode = "simulated"` and performs no network write.
- The credential path is built as an interface (vault reference resolved by managed identity) with **no secret behind it**. REQ-ACT-04 / REQ-PGM-01 run anyway: the credential audit must show **zero** write-capable credentials anywhere, olive included.
- **Phase 2 trigger:** signed write agreement, named credential owner and rotation procedure (charter §3.3), and CTO/CSO sign-off. Phase 2 is a new decision record, not a configuration change.

## 3. Who submits proposals

**Status.** These are not actually in conflict, but the wording invites confusion:
- Olive charter §2: "hometown proposes; olive validates, routes and executes."
- Liam design v0.4 D6 (ratified 2026-10-19): hometown "proposes" *in the charter sense*, meaning it produces the suggestion inside a traced answer. Liam, as the agent principal olive authorizes, carries it to olive with hometown's `trace_id` as `trace_ref`. Liam lists the olive-side confirmation as open item O6, explicitly **not ratified** in the Liam package.

**Proposed decision.**
- Amend olive charter §2 "Refuses to own" bullet 1 to: *"Deciding what to propose. hometown generates proposals inside traced answers; the acting agent principal submits them. Olive refuses any proposal whose `trace_ref` does not resolve to a hometown ATE for the same `run_id`. Olive never generates proposals." How olive performs that check in Phase 1 is set out in §14.*
- This closes Liam O6 and keeps the invariant intact: the AI suggestion is traced at hometown, the submitter is an identified agent, and olive still decides nothing about content.
- **Liam owner:** Oscar Mack (design and ratification). The Liam implementing agent supplies fixtures.
- **Joint compatibility review:** Oscar plus **one reviewer who did not write either side**. The hometown owner is suggested, since hometown's ATE is the join target. A review where one person approves both sides of the contract is weak evidence. **Independent reviewer: James O'Meara (jomeara@twgglobal.com)**, named by Oscar Mack on 2026-10-20 as having written neither side.

## 4. Referenced specifications: locations

| Document | Where it is | Action |
|---|---|---|
| `OLIVE_CHARTER_v0.4.md` | `v04-poc2-specs.zip` → `olive/specs/` | Present |
| `PROGRAM_POC2_CHARTER_v0.4.md`, `PROGRAM_SHARED_VOCABULARY_v0.4.md`, `vocabulary.yaml` (v0.4, SHA-256 `60a44f00…e8e7`) | `v04-poc2-specs.zip`, `olive-agent-inputs.zip` | Vendor into `olive/contracts/vendored/` |
| `LIAM_DESIGN_SPEC_v0.4.md`, `LIAM_TEST_SPEC_v0.4.md` | Exist (ratified 2026-10-19); not yet in a repository | Commit to `liam/specs/`; copy or link into `fiskroad/specs/` |
| `POPEYE_v0.4.2.md` (and v0.4.1, v0.4.3) | `~/fiskroad/specs/` (stated by Oscar Mack, 2026-10-20; not yet verified by the implementer) | Implementer confirms each file is present at the start of the run; a missing one still triggers the stop rule |
| `NONO_v0.4.2.md`, `NONO_v0.4.3.md` | `~/fiskroad/specs/` (stated by Oscar Mack, 2026-10-20; not yet verified by the implementer) | As above |
| `DECISION_agent-key-service-scope.md`, `DECISION_desktop-agent-nono-integration.md` | `~/liam/specs/` and `~/fiskroad/specs/` (stated by Oscar Mack, 2026-10-20; not yet verified by the implementer) | Implementer confirms both copies are present and **identical** (same SHA-256) at the start of the run; if they differ, it stops and reports rather than choosing one |
| "Cross-component execution specification" | `~/fiskroad/specs/` (stated by Oscar Mack, 2026-10-20; exact filename not yet recorded, not yet verified by the implementer) | Implementer records the exact filename at the start of the run; a missing file still triggers the stop rule |

**Rule for the prompt run:** if a referenced document is absent, the implementer stops and reports it. It does not reconstruct the contents. In particular it must not infer popeye's introspection interface (§7) from the Liam spec's description of it.

---

## 5. Target system and action

**Proposed decision:** `crm.case_note@1`, target `nexus_crm`, reversible, as charter §3.1 already sketches. It is low blast radius, obviously useful, and fully reversible, so the dual-approval path for irreversible actions stays out of scope.

Note for the ratifier: Nexus is our own application with synthetic data. That makes simulation-to-real a smaller step than a divisional system, but it does **not** satisfy entry criterion 4, whose purpose is a *divisional* agreement. Decide whether POC-2 beat 2 runs against Nexus (an engineering proof) or waits for a divisional system (the political proof). Recommendation: engineering proof on Nexus in Phase 1 (simulated); keep the divisional agreement as the Phase 2 gate.

## 6. API versioning and idempotency

**Proposed decision.**
- **Endpoint:** `POST /v1/proposals`, plus `GET /v1/proposals/{proposal_id}` and `GET /v1/catalog/{version}`. Major version in the path; within `v1`, only additive optional fields.
- **Catalog pinning:** the request carries a versioned `action_type` (`crm.case_note@1`). An unknown or retired version returns `refused_policy` with reason `not_in_catalog`.
- **Submission idempotency:** key = `(agent_id from introspection, run_id from body)`. This matches Liam's rule of one proposal per run, so S-OL-1 needs no new header.
  - Same key, byte-identical canonical body → `200` with the original `proposal_id` and its **current** status.
  - Same key, different content → `409 idempotency_conflict`; nothing stored, and the refusal logged.
  - A second distinct proposal for the same `run_id` is the same case: `409`, which enforces "at most one per run" server-side as well.
- **Execution idempotency:** separate, keyed on `proposal_id` (charter REQ-ACT-03). A retried execution returns the first result.

## 7. Introspection: what is authoritative

**Status.** `POPEYE_v0.4.2` defines the introspection interface and is not available here (§4). Nothing below may contradict it. Where it is silent, raise a popeye change, don't improvise.

**Proposed minimum claims** olive requires from introspection:

| Claim | Use in olive |
|---|---|
| `active` | Inactive → `401 key_inactive` |
| `key_id` | Joins to nono's agent-session record; recorded on the proposal |
| `principal_type` | Must be `agent` on `POST /v1/proposals`. `user` and `application` → `403 principal_not_permitted` *(0.5.2: was `service`, a value the vocabulary does not have; correction confirmed 2026-10-20)* |
| `agent_id` | Must be in olive's agent allowlist (`liam` for POC-2) |
| `on_behalf_of` | Entitlement intersection (vocabulary §3.2); excluded from self-approval (§8) |
| `purpose` | Checked against the action's `required_purpose` before queueing |
| `tenant_id` | Proposal and queue-item tenant; must match the party's tenant |
| `expires_at` | Proposal expiry may not exceed it |

**Caller classes are separate paths.** Olive calls introspection as a registered caller (`principal_type = application` in the vocabulary) using its own managed identity. Agents and users can never reach olive's service path, and olive never accepts a service credential on the proposal endpoint (this is the prompt's "cannot use the Popeye introspection path as a service caller" test).

**nono session context:** authoritative via `key_id` → agent-session record. A `nono_session_id` in the request body is **advisory** and recorded, never trusted for authorization.

## 8. Approvers

**Proposed decision.**
- `role:case_supervisor` resolves to one Entra ID group, membership checked **at decision time**, not at claim time. The group and its members are entry criterion 3; name them here: ________.
- Reviewers authenticate to the queue UI through Entra ID (OIDC). An approval without an authenticated reviewer identity is rejected.
- **No self-approval:** the approver may not be the proposal's `on_behalf_of` user.
- `rationale` is required on approve and reject (REQ-QUE-03).
- Retention: decisions, rationale and execution ledger rows are kept for the life of the POC, with synthetic data only. Production retention is a Phase 2 item aligned with the program retention decision.

## 9. Expiry

**Proposed decision.** Default 8 hours (one business day), configurable per action type in the catalog with a catalog-defined maximum. The submitter may request a shorter expiry using an optional additive field, `expires_in_seconds`, but never a longer one. Expiry is also capped by the session key's `expires_at`. Expired proposals cannot be approved or executed and return a distinct error (REQ-ACT-05).

## 10. Persistence

**Proposed decision.** Use the shared POC Postgres from Phase 1, with three tables (`proposal`, `queue_item`, `execution_ledger`), each carrying `tenant_id` with both isolation layers: an application predicate and database RLS (REQ-QUE-02 requires both, and a test that removes one layer). Append-only grants on the execution ledger. In-memory stores are for unit tests only. Environment and roles: §13. The JSONL output becomes the simulated executor's *target*, standing in for the CRM, not olive's ledger.

## 11. Queue frontend

**Proposed decision.** A minimal web UI (list, claim, decide, audit), as the charter recommends: every approval is an audit record, and Teams flows are harder to audit. Teams notifications that link to the web UI are acceptable later; approval inside Teams is not.

## 12. Contract publication

**Proposed decision.**
- **Payload source of truth:** JSON Schema (2020-12) under `olive/contracts/proposal/v1/`. Catalog parameter schemas are already JSON Schema, and Liam validates against them.
- **Endpoint contract:** OpenAPI 3.1 at `olive/contracts/proposal/v1/openapi.yaml`, `$ref`-ing those schemas rather than restating them.
- **Compatibility:** a table mapping S-OL-1 to v1, field by field, in `olive/contracts/proposal/v1/COMPATIBILITY.md`. It goes to the §3 joint review before Liam fixtures are regenerated.

---

## Ratification

| Field | Value |
|---|---|
| Decision | **Ratified**: §1–§12 as proposed, without amendment |
| Ratified by | Oscar Mack (architecture and risk decisions) |
| Informed | CTO Kamal Shah, CSO Chris Planell (informing them is required, their sign-off is not, for Phase 1; their sign-off is required for Phase 2) |
| Independent reviewer (§3) | James O'Meara (jomeara@twgglobal.com), named 2026-10-20 |
| Approver group (§8) | `role:case_supervisor` → Entra group ________ (open; blocks live approvals, not the Phase 1 build) |
| Date | 2026-10-20 |
| Scope ratified | The whole document, including the documented exception to program charter §5 entry criteria for simulation-only work (§1), the olive charter §2 amendment (§3, closes Liam O6), and the stop-if-absent rule for referenced specifications (§4) |
| Not ratified here | Phase 2 (any real write, any credential); the contents of `POPEYE_v0.4.2` and the other documents listed as missing in §4, which this record defers to and does not replace |
| Effect | Olive Phase 1 starts **simulation-only** once the §4 artifacts are placed. Phase 2 (real write) requires a new record with CTO/CSO sign-off |
| Change control | Changes to §1–§3 require re-ratification; other changes require a new revision of this record |


---

# Amendments added in v0.2–v0.3 (ratified 2026-10-20)

## 13. Phase 1 Postgres: environment, migrations and roles

**Gap.** §10 requires Postgres from the start but names no instance.

**Proposed decision.**

**Instance.** Use the **shared local POC Postgres**: the Docker container `deploy-postgres-1` on the external Docker network `deploy_default`. hometown does **not** start it; hometown's Cube profiles join that network as a client. Per hometown `specs/STATE.md` ("Local Shared Postgres Notes"), this instance is already shared by Nexus CRM, Intrepid loan_engine, sit-cip and hometown, and **`nexus` is the local admin and database owner**. Synthetic data only. **Not** the Azure PostgreSQL Flexible Server: that is popeye's ledger, behind a private network path, and changed only through the pipeline. Olive gets no grants there in Phase 1.

- **Started by Nexus CRM's `deploy/` Compose project** (confirmed by Oscar Mack, 2026-10-20), which publishes port 5432 to the host. Runbook v0.3 §3.1 must be corrected to say so: it currently says to run `docker compose up -d` from the hometown repository, which has no root Compose file.
- Olive gets its **own database, `olive`**, created by `nexus` on that instance, not a schema inside another application's database. One instance keeps the dev loop simple; a separate database keeps olive's tables out of other applications' grants and makes the later move to a dedicated server a connection-string change.
- **CI** uses a throwaway Postgres container per run (Testcontainers, the same pattern bluto uses). CI never depends on a developer's running stack.
- **Connection variables:** §15.4.

**Migrations.** Plain versioned SQL files in `olive/sql/migrations/` (`0001_init.sql`, …), applied in order by a small runner that records applied versions in a `schema_migrations` table. This follows hometown's `sql/` convention and keeps the DDL reviewable as SQL whatever language olive is written in. Migrations are forward-only; a correction is a new migration.

**Role model.** The `olive` database is created by the instance admin (`nexus`) and owned by `olive_owner`.

| Role | Login | Privileges | Purpose |
|---|---|---|---|
| `olive_owner` | No | Owns all objects | Ownership only; nothing connects as it |
| `olive_migrator` | Yes | Member of `olive_owner` | Runs migrations; used only by the migration runner and CI |
| `olive_app` | Yes | `SELECT, INSERT, UPDATE` on `proposal`, `queue_item`; **`INSERT` only** on `execution_ledger` and `proposal_event` (plus `SELECT` where the service must read its own history, through RLS); no `UPDATE` or `DELETE` on either; no DDL; `NOBYPASSRLS` | The running service |
| `olive_reader` | Yes | `SELECT` on all four tables, through RLS | Read-only reporting (a later Cube model), and the audit queries |

**Row-level security**, the second isolation layer (REQ-QUE-02):

- Every table has `tenant_id uuid NOT NULL`, and uses vocabulary UUIDs only, never display labels.
- `ENABLE` **and** `FORCE ROW LEVEL SECURITY` on all four tables (`proposal`, `proposal_event`, `queue_item`, `execution_ledger`), so the owner role is bound too.
- One policy per table: `USING (tenant_id = current_setting('olive.tenant_id')::uuid)` with a matching `WITH CHECK`.
- `olive_app` sets `olive.tenant_id` with `SET LOCAL` at the start of **every** transaction, from the tenant on the introspected key. Use `current_setting('olive.tenant_id')` **without** `missing_ok`, so an unset tenant raises an error rather than returning nothing.
- The application-level tenant predicate stays in every query as well. The required negative test removes each layer in turn and shows the other still holds.
- `olive_reader` sees one tenant per session by the same setting. Any cross-tenant audit role is a Phase 2 decision.

**Immutability.** `execution_ledger` is append-only by grant (INSERT only, as hometown's trace store is). Proposal state changes are `UPDATE`s on `proposal`, each paired with an appended row in a `proposal_event` table (INSERT only), so the history cannot be rewritten even though the current-state row changes.

## 14. Verifying `trace_ref` while hometown's trace-lookup contract is open

**Gap.** §3 commits olive to refusing a proposal whose `trace_ref` does not resolve to a hometown ATE for the same `run_id`. No stable hometown mechanism exists for olive to perform that check:
- hometown publishes no trace-lookup endpoint, and the cross-component spec still lists the ATE endpoint contract as open;
- the ATE store schema is in flux (the repository is at `ate/0.2`, while Liam relies on the v0.4 `run.runId` field).

**Options considered.**

| Option | Verdict |
|---|---|
| A. Olive reads hometown's `answer_trace` table through a read-only grant | **Rejected.** It couples olive to another component's storage schema while that schema is changing, and puts a second reader on a governed store without hometown's agreement |
| B. hometown publishes a narrow trace-resolution endpoint | **Target.** hometown owns the trace store, so hometown answers questions about it |
| C. Olive skips the check until B exists | **Rejected.** The rule would exist only on paper, and Phase 1 would build nothing that enforces it |

**Proposed decision.**

1. **Olive implements the check now, behind an interface**, for example `TraceResolver.resolve(trace_ref) → {found, run_id, tenant_id, agent_id} | not_found | unavailable`. The proposal path calls it **before** policy and before queueing.
2. **Phase 1 ships one implementation: a fixture-backed resolver**, loaded from contract fixtures in `olive/contracts/trace-resolution/`. All refusal tests run against it: unknown `trace_ref`, `run_id` mismatch, tenant mismatch, resolver unavailable.
3. **Refusal semantics are fixed now**, independent of the transport:
   - not found or `run_id` mismatch → `refused_policy`, reason `trace_unresolved`; never queued; *(Amended by §16.2: not-found becomes `trace_not_found`, which is not stored and is retryable)*
   - tenant mismatch → `refused_policy`, reason `trace_tenant_mismatch`;
   - resolver unavailable → `503`, **fail closed**. No proposal is accepted without a resolved trace. This is an action path, so the "observation never blocks" rule does not apply.
4. **Every proposal records `trace_verification`**: `fixture` in Phase 1, `hometown` once B is live. *(Amended by §16.5: allowed values are fixed, and fixtures are barred outside non-production.)* A demo or report must say which applied. Phase 1 results claim the refusal **logic** is verified, not the cross-component **join**.
5. **The request to hometown (option B).** Ask the hometown owner, who is also the §3 reviewer, for the smallest contract that answers the question:
   - `GET /v1/traces/{trace_id}/resolution`, authenticated as a registered service (olive's managed identity, introspected by popeye as a service principal);
   - the response contains **identifiers only**: `trace_id`, `run_id`, `tenant_id`, `agent_id`, `created_at`, and no evidence, query or answer content;
   - `404` when absent. It is tenant-scoped, so it cannot be used to discover another tenant's traces.

   This adds one row to the hometown ATE v0.4 joint review (gap G6 is already on that agenda). *(Amended by §16.1: the response shape now includes `schema_version` and nullable `run_id`/`agent_id`; tenant comes from the caller's credential only.)*
6. **Exit criterion:** the olive POC exit criteria gain "the trace resolver runs against hometown's published endpoint, and `trace_verification = hometown` on every demo proposal." Until then the beat 2 demo states plainly that trace verification used fixtures.


## 15. Implementer clarifications (added in v0.3)

### 15.1 Agent mismatch on a resolved trace

A resolved trace whose `agent_id` differs from the introspected key's `agent_id` → `refused_policy`, reason **`trace_agent_mismatch`**, before policy and before queueing; never queued. Rationale: an agent must not be able to carry another agent's traced suggestion to olive. That is the same impersonation the run and tenant checks prevent, from a different direction.

Order of the trace checks, with the first failure winning: `trace_unresolved` (not found) → `trace_unresolved` (`run_id` mismatch) → `trace_tenant_mismatch` → `trace_agent_mismatch`. *(Amended by §16.2: new first reason `trace_not_found`, and null `run_id` / `agent_id` handling.)*

### 15.2 Order of operations on submission

1. **Introspect the key.** Inactive or invalid → `401`. This always runs first; a retry never skips authentication.
2. **Principal checks** (§7): `principal_type = agent`, agent allowlisted.
3. **Idempotency lookup** on `(agent_id, run_id)`, within the key's tenant:
   - match with an identical canonical body hash (§15.3) → `200` with the existing `proposal_id` and **current** state. **The trace resolver is not called**, so an identical retry succeeds even if the resolver has since become unavailable. The original submission already passed the check;
   - match with a different hash → `409 idempotency_conflict`. The resolver is not called, nothing is stored, and the refusal is logged;
   - no match → continue.
4. **Schema and catalog validation.**
5. **Trace resolution** (§14, §15.1). Unavailable → `503`, fail closed. *(Amended by §16.3: not-found is a non-stored `422`.)*
6. **Policy** (purpose, entitlements, rate). Refused → `refused_policy`, stored and never queued.
7. **Persist the proposal and enqueue it.**

Concurrency: a unique constraint on `(tenant_id, agent_id, run_id)` makes two simultaneous first submissions resolve to one proposal; the loser re-enters step 3.

A malformed body (schema failure in step 4) is a `400` and is **not** stored, so a corrected body can be resubmitted under the same `run_id`. Policy refusals (unknown catalog action in step 4, any refusal in steps 5–6) are stored as proposals in state `refused_policy` under the same idempotency key, so retrying a refused submission returns the same refusal rather than re-evaluating it. A caller that wants re-evaluation needs a new run.

### 15.3 Canonical body

The idempotency comparison uses **RFC 8785 JSON Canonicalization Scheme (JCS)** over the v1 request body as received, after schema validation:
- body only. Headers, including anything nono's proxy injects, are excluded;
- olive stores `body_sha256` = SHA-256 of the JCS bytes on the proposal and compares hashes, not bodies;
- an absent field and a field present as `null` are different bodies. The v1 schema forbids `null` for optional fields, so this case is a `400` rather than an ambiguity.

This matches the program's recorded canonicalization target for the ATE (RFC 8785, per the zt-infra alignment note), so the program has one canonical form, not two.

### 15.4 Local connection variables

Follow hometown's per-system convention (`<SYSTEM>_POSTGRES_URL`, with split host/port/db/user/password variables beside it, in a gitignored local `.env` with a committed `.env.example`):

```env
OLIVE_POSTGRES_URL=postgresql://olive_app:<local-only>@<host>:5432/olive
OLIVE_POSTGRES_HOST=
OLIVE_POSTGRES_PORT=5432
OLIVE_POSTGRES_DB=olive
OLIVE_POSTGRES_USER=olive_app
OLIVE_POSTGRES_PASSWORD=
OLIVE_MIGRATOR_URL=postgresql://olive_migrator:<local-only>@<host>:5432/olive
```

- Host is **`deploy-postgres-1`** when olive runs in a container on `deploy_default`, the same rule hometown's README gives for Cube: never `localhost` inside a container. From the host machine it is `localhost:5432`, which Nexus CRM's `deploy/` project publishes.
- The service reads only `OLIVE_POSTGRES_URL`; the split variables exist for tooling parity with hometown. `OLIVE_MIGRATOR_URL` is read only by the migration runner.

---

## Ratification (v0.2–v0.3 amendments)

| Field | Value |
|---|---|
| Decision | **Ratified**: §13, §14 and §15 as written, without amendment |
| Ratified by | Oscar Mack (architecture and risk decisions) |
| Hometown owner acknowledgement of §14.5 request | **Received 2026-10-20** with five changes (relayed by Oscar Mack). See §16 |
| Date | 2026-10-20 |
| `deploy` Compose project (§13, §15.4) | Nexus CRM `deploy/`, port 5432 published (confirmed by Oscar Mack, 2026-10-20) |
| Effect | Unblocks olive's persistence layer (§13), the trace check with fixtures (§14) and the submission path (§15). The live hometown resolver stays blocked on the hometown contract |


---

# Amendments added in v0.4 (ratified 2026-10-20)

## 16. Trace resolution: hometown owner's response

**Source.** The hometown owner acknowledged the §14.5 request on 2026-10-20. `GET /v1/traces/{trace_id}/resolution` goes on the ATE v0.4 joint review agenda next to the open endpoint gap, and fixtures with `trace_verification: fixture` are the agreed fallback until it is live. The owner proposed five changes, after checking the trace contract (`src/core/trace.ts`) and the v0.4 draft (`specs/HOMETOWN_v0.4.md` §2.3), and offered to record the endpoint as proposed `HOMETOWN_v0.4` §2.5 on the `contract/ate-v0.4` branch.

| # | hometown owner's change | Olive's position |
|---|---|---|
| 1 | `agent_id` does not exist in any trace. Add `principal.agentId` in ATE v0.4: required when `principalType = agent`, absent otherwise; it holds the agent identity bound to the session key when nono mints it | **Adopted** |
| 2 | `run_id` and `agent_id` are returned as `null`, not omitted; `schema_version` is included so "predates runs" can be told apart from "has no run" | **Adopted.** Olive treats null as a mismatch (§16.2) |
| 3 | Tenant comes from the caller's credential only; a request tenant parameter is ignored. Another tenant's trace, or one with a null `tenant_id`, gets the same `404` as a missing trace. `tenant_id` is still returned | **Adopted.** Olive still compares the returned tenant with the key's tenant as a second check |
| 4 | hometown returns facts, not a verdict; olive decides. A `200` is cacheable indefinitely (traces are append-only); a `404` is not | **Adopted** (§16.4) |
| 5 | `trace_verification` as `fixture \| verified \| mismatch`; fixture proposals barred outside non-production | **Adjusted.** The production bar is adopted; the value set is not (§16.5) |

### 16.1 Resolution response contract (owned by hometown, proposed as `HOMETOWN_v0.4` §2.5) *(Amended by §17.1 and §17.2)*

```json
{ "trace_id": "…", "schema_version": "ate/0.4", "run_id": "…|null",
  "tenant_id": "…", "agent_id": "…|null", "created_at": "…" }
```

- **`200`**: the trace exists in the caller's tenant.
- **`404`**: absent, in another tenant, has a null `tenant_id`, or the ID is malformed. There is no `400`, so a malformed ID reveals nothing more than a missing one.
- **`401` / `403`**: the caller is not a registered service.
- Tenant comes from the caller's credential. Any request tenant parameter is ignored.
- Identifiers only. No evidence, query or answer content.

This supersedes the §14.5 bullets. Olive builds its fixtures to this shape now, so the switch to hometown changes transport only.

### 16.2 How olive reads the response *(Amended by §17.3: new check 5)*

The resolver interface returns hometown's facts unchanged: `found(response) | not_found | unavailable | caller_rejected`. **Olive** applies the checks, in this order, with the first failure winning:

| # | Condition | Result | Stored? |
|---|---|---|---|
| 1 | `404` | `trace_not_found` | **No**, retryable (§16.3) |
| 2 | `run_id` is null, or differs from the proposal's `run_id` | `refused_policy`, `trace_unresolved` | Yes |
| 3 | `tenant_id` differs from the key's tenant | `refused_policy`, `trace_tenant_mismatch` | Yes |
| 4 | `agent_id` is null, or differs from the key's `agent_id` | `refused_policy`, `trace_agent_mismatch` | Yes |
| — | hometown unavailable or timed out | `503`, fail closed | No |
| — | hometown returns `401`/`403` to olive | `503`, fail closed, and an operational alarm: this is olive's own credential or registration failing, not a fault in the proposal | No |

- A null `run_id` is a mismatch whatever the cause: a v0.3 trace that predates runs, or a v0.4 trace with no run. Olive records the returned `schema_version` on the proposal (`trace_schema_version`) so a report can tell the two apart.
- Row 3 should never fire, because hometown already scopes by the credential's tenant. It stays as a second check, the same two-layer pattern as row-level security.
- This supersedes §14.3's not-found rule and §15.1's order.

### 16.3 Not-found refusals are not stored (fixes ratified §15.2)

**Defect.** Under §15.2, policy refusals are stored under the `(agent_id, run_id)` idempotency key, so a retry returns the same refusal. A `404` may only mean the trace is not written *yet* (hometown's trace write raises an alarm but never blocks the answer). *(0.5.2 note: superseded for agent answers by the invariant 4 exception, `HOMETOWN_v0.4` §2.5 rule 4: an agent answer is not returned until its trace is saved. The fix below stays as a second guard; it now covers a narrower case.)* Liam could then receive a `trace_id`, submit before the trace exists, and be refused permanently for that run.

**Fix.**
- `trace_not_found` returns **`422`** with reason `trace_not_found` and `retryable: true`. **Nothing is stored** under the idempotency key; a content-free service log entry is written. A retry for the same `run_id` is evaluated afresh, the same treatment §15.2 gives a malformed body.
- Refusals on a **resolved** trace (rows 2–4 of §16.2) stay stored. Traces are append-only, so those facts cannot change and re-evaluating them would only cost a hometown call.
- **Liam compatibility.** Liam v0.4 makes one olive request and exits `22` on any olive failure, so a `422` ends the run as an olive failure. That is acceptable for the POC and goes into the S-OL-1 → v1 compatibility table (§12), so the §3 review sees it.
- The real remedy is on hometown's side (§16.6, item 2). This fix only stops a timing gap from turning into a permanent refusal.

§15.2 step 5 now reads: "Trace resolution (§14, §16). Unavailable → `503`, fail closed. Not found → `422 trace_not_found`, not stored."

### 16.4 Caching

- A `200` response is cached **indefinitely**, keyed by `(tenant_id, trace_id)`. Traces are append-only, and the tenant is in the key because resolution is tenant-scoped.
- A `404` is **never cached**, because a trace can be written after olive first asks.
- `503`, `401` and `403` are never cached.
- The cache is an optimisation only. Losing it changes latency, never a decision.

### 16.5 `trace_verification`

- **Allowed values: `fixture | hometown`.** The field records **where** the check came from. **How it turned out** is already recorded as the refusal reason (`trace_not_found`, `trace_unresolved`, `trace_tenant_mismatch`, `trace_agent_mismatch`), and a mismatch is never accepted, so a `mismatch` value would only ever appear on refused proposals and would duplicate the reason. Keeping source and outcome separate means a report can ask "how many proposals were checked against real traces?" without decoding refusals. Any other value is rejected by the schema.
- **Fixtures are barred outside non-production** (adopted from the hometown owner). A new setting, `OLIVE_ENVIRONMENT ∈ {local, ci, nonprod, production}`, is required with no default. With `production`, olive **refuses to start** if the fixture resolver is configured. A proposal with `trace_verification = fixture` can never be approved for real execution. Phase 1 is simulation-only anyway, so this holds by construction until Phase 2, and the startup check makes it explicit.

### 16.6 Requests on hometown, for `HOMETOWN_v0.4` §2.5

Agreed by the hometown owner on 2026-10-20 (relayed by Oscar Mack), to be recorded alongside the endpoint in `HOMETOWN_v0.4` §2.5:

1. **`principal.agentId`** in ATE v0.4 (§16 row 1). It must be the same identity popeye's introspection returns as `agent_id` for the session key, so olive compares like with like.
2. **For the agent answer endpoint, the trace is committed before the response returns a `trace_id`.** Returning an ID for a trace that does not exist yet is a broken promise. "Observation never blocks" still holds for ordinary answers, but an answer that can lead to an action is the exception, and §2.5 should say so explicitly rather than leave it to be inferred. If the trace cannot be written, the agent answer endpoint fails rather than returning an untraced proposal.
3. **The endpoint runs only on the Postgres trace store with row-level security on `answer_trace`**, using the same tenant-scoped read path as the trace viewer, and refuses to start against the file-backed store. There is then one route to trace data.
4. **It ships with the agent answer endpoint** (Liam open item A1, stub contract S-HT-1). Both need hometown's first HTTP server, so it is one piece of work, not two.

### 16.7 Open dependencies for the ATE v0.4 joint review

| Dependency | Raised by | Proposed owner |
|---|---|---|
| hometown has no HTTP server | hometown owner | hometown, shared with S-HT-1 (§16.6, item 4) |
| The default trace store is file-backed; Postgres is opt-in | hometown owner | hometown, Postgres required for the endpoint (§16.6, item 3) |
| Tenant-scoped trace reads are future work in `specs/STATE.md` | hometown owner | hometown, the same read path as the trace viewer |
| No registry of services | hometown owner | **popeye** proposed: it already introspects every key, and identifying a service caller is the same kind of check. Confirm against `POPEYE_v0.4.2`, which olive's prompt assumes covers service callers and which is still missing from `fiskroad/specs/` (§4) |
| The identity `principal.agentId` holds | hometown owner | Joint review; olive's requirement is §16.6, item 1 |

### 16.8 Test additions

Added to olive's suite, all against the fixture resolver in Phase 1:

| Case | Expected |
|---|---|
| `404` | `422 trace_not_found`; nothing stored; an immediate retry with the same `run_id` that now resolves is accepted |
| `run_id: null` with `schema_version: ate/0.3` | `trace_unresolved`, stored; `trace_schema_version = ate/0.3` recorded |
| `run_id` differs | `trace_unresolved`, stored |
| `agent_id: null` | `trace_agent_mismatch`, stored |
| `agent_id` differs | `trace_agent_mismatch`, stored |
| Returned tenant differs from the key's tenant (fixture forces it) | `trace_tenant_mismatch`, stored |
| hometown `403` to olive | `503`; alarm raised; nothing stored |
| Second resolution of the same trace | Served from cache; no resolver call |
| `404` followed by `200` | The second call reaches the resolver (no negative caching) |
| `OLIVE_ENVIRONMENT=production` with the fixture resolver | Service refuses to start |
| `OLIVE_ENVIRONMENT` unset | Service refuses to start |
| `trace_verification` value outside `fixture \| hometown` | Rejected by schema |

---

## Ratification (v0.4 amendments)

| Field | Value |
|---|---|
| Decision | **Ratified**: §16 as written, without amendment |
| Ratified by | Oscar Mack (architecture and risk decisions) |
| Amends ratified text | §14.3 (not-found rule), §14.4 (`trace_verification`), §14.5 (response shape), §15.1 (check order), §15.2 step 5 (not-found storage) |
| hometown owner | Acknowledged §14.5 with changes, 2026-10-20. **Agreed to §16 in full, including the §16.6 requests**, 2026-10-20 (relayed by Oscar Mack) |
| Date | 2026-10-20 |
| Effect | Olive builds its resolver, fixtures and tests to §16, superseding the amended §14/§15 passages. The live hometown resolver stays blocked on `HOMETOWN_v0.4` §2.5 and the §16.7 dependencies |
| Change control | Changes to §16.1 (the response shape) or §16.6 (the hometown requests) need hometown's agreement and re-ratification; other changes need a new revision |


---

# Amendments added in v0.5 (ratified 2026-10-20)

## 17. Resolution contract: tenant for multi-tenant callers, and proposal binding

**Source.** The review of `HOMETOWN_v0.4` §2.5–§2.6 on 2026-10-20. hometown implemented §16 faithfully; both defects below trace back to this record's §14.5.

### 17.1 Tenant for a multi-tenant service caller (amends §16.1)

**Defect.** §16.1 (and `HOMETOWN_v0.4` §2.5 rules 2–3) take the tenant only from the caller's credential, and give a registered service whose credential carries no tenant a `403`. olive serves every tenant, and its managed identity carries none. As written, every olive resolution call returns `403`.

**Options.**

| Option | Verdict |
|---|---|
| (a) popeye issues olive short-lived, **tenant-scoped service tokens**. olive authenticates with its managed identity and requests a token for tenant T; popeye checks that olive is registered for T | **Adopted.** Rule 3 stands unchanged: the tenant still comes only from a credential. It also gives the open service-registry question a natural owner |
| (b) A registered multi-tenant service states the tenant in a header; hometown honours it for such services and audits every call | **Rejected.** It weakens rule 3 for every service that is ever registered, and brings back the probing risk rule 3 exists to stop |
| (c) olive forwards the agent's session key to hometown | **Rejected.** A credential issued to Liam would be reused by a different component; olive's prompt forbids forwarding the session key |

**Decision.**
1. **Delegated agent binding.** When olive requests a service token, it presents the agent session key from the proposal to popeye's token-issuance interface. popeye derives **both** `tenant_id` and `agent_id` from introspection of that key. The issued token is olive's own principal (`principal_type = application`, olive's registered identity), but carries an immutable **`delegated_agent_id`** claim, plus `tenant_id`, the source `key_id`, and an expiry. popeye issues it only if olive is registered for that tenant **and** permitted to act for that agent in that tenant. olive cannot request a token for a tenant or agent on its own say-so; it needs a live agent key that was presented to it.
2. **Where the session key may go.** Only to popeye: introspection (§7) and token issuance. **Never to hometown**, and never to any target system. Every outbound call olive makes is checked for this (§17.4).
3. **Call.** olive presents the service token on `GET /v1/traces/{trace_id}/resolution`. hometown introspects it at popeye and scopes the read to its `tenant_id`. `HOMETOWN_v0.4` §2.5 rules 2–3 apply unchanged: the tenant still comes only from a credential.
4. **Lifetime and caching.** The token's expiry is never later than the source session key's `expires_at`. olive caches tokens keyed by **`(tenant_id, delegated_agent_id, key_id)`**, until shortly before the earlier of the two expiries, and never past either. Keying on `key_id` too means a token is never reused for a different session, even for the same agent and tenant. A cache keyed only on tenant and agent would let a token outlive the session key that justified it. The token is never logged, stored, or sent anywhere but hometown.
5. **Registration.** olive's registration lists the tenants and agents it may act for. For POC-2: the three POC tenants in the vendored vocabulary and the agent `liam`. Anything else is refused by popeye.
6. **Until popeye implements this,** olive stays on the fixture resolver (§14, §16.5), and the token client is built against a popeye stub. Nothing in Phase 1 depends on it.

**Principal type (0.5.2 correction, confirmed by Oscar Mack 2026-10-20).** The shared vocabulary allows exactly three principal types: `user`, `application`, `agent`. There is no `service`, and popeye's own introspection callers already authenticate as `application`. olive's tokens therefore use `application`. What sets them apart is olive's registered identity and the `delegated_agent_id` claim, not a fourth type. "Service token" in this record is a description, not a vocabulary value. Adding `service` to the vocabulary would be a program-level contract change (program charter §6.4) with no benefit here, so it is not proposed. `HOMETOWN_v0.4` §2.5 names no principal type, so hometown's text is unaffected.

**Dependency.** This is a popeye change: the service registry (an open item in §16.7) plus token issuance and introspection for olive's tokens. Proposed owner: popeye. It goes on the ATE v0.4 joint review agenda as a dependency of §2.5.

**popeye acknowledgement, 2026-10-20 (relayed by Oscar Mack).** popeye acknowledges this as a **proposed, not-yet-ratified dependency** with this scope:
- maintain a service registry authorizing olive by tenant and agent;
- accept the agent session key only at popeye's introspection and token-issuance boundary;
- issue short-lived olive tokens bound to `tenant_id`, `delegated_agent_id` and the source `key_id`, capped by the source session key's expiry;
- provide token introspection for hometown;
- refuse out-of-scope tenant or agent requests;
- keep Phase 1 on fixtures and a popeye stub until this is implemented and approved.

The acknowledgement is **not** popeye ratification, implementation approval, or authorization to change popeye. olive's ratification of §17 binds olive's side only: it fixes what olive will ask for and how it will check the answer. popeye's side needs its own specification and ratification in the popeye repository before any of it is built. Until then the live resolver stays blocked.

### 17.2 Proposal binding (amends §16.1)

**Defect.** The trace records `answer.hash` but not the proposal. An agent, or anything that has compromised its endpoint, could alter `parameters` or `justification` and still present a valid `trace_ref`. The approver would see something that looks traced to an AI answer but isn't, which defeats REQ-PGM-02 and REQ-ACT-01.

**Decision.**
1. **Trace.** hometown adds `proposalHash` to the ATE for agent answers: lowercase hex SHA-256 over the RFC 8785 (JCS) canonical form of the proposal object with **exactly these four keys**: `action_type`, `party_ref`, `parameters`, `justification`. `null` when the answer carries no proposal, and when hometown dropped one on shape (`proposal_shape` `deny`).
2. **Resolution response.** A seventh field, `proposal_hash` (`string | null`).
3. **olive.** Computes the same hash over the four fields as submitted, after schema validation and before policy, and compares (check 5 in §17.3).
4. **No mutation before hashing, on either side.** Both hashes must be over the proposal exactly as transmitted:
   - **olive's schema validation is non-mutating.** No type coercion (`"5"` stays a string, never becomes `5`), no injected defaults, no stripping of unknown keys, no trimming or Unicode normalisation. A value that only validates after coercion is a `400`, not a coerced pass;
   - **hometown hashes the proposal object it actually returns**, after its own shape check and any drop, not the model's raw output;
   - neither side applies policy-driven changes (masking, redaction) to the proposal between producing or receiving it and hashing it. Masking happens before context assembly (`HOMETOWN_v0.4` §4.2), so it never needs to touch the proposal afterwards.

   Otherwise the two hashes could legitimately differ, and check 5 would refuse honest proposals.
5. **One canonical form.** The same RFC 8785 rule as §15.3 and the program's ATE signing target, so there is one canonicalization in the program, not three.
6. **Liam.** No change: Liam already forwards the four fields unchanged (`LIAM_DESIGN_SPEC_v0.4` §7.2). Any client-side reformatting that changes values would now be caught, which is the point.

### 17.3 Revised resolution contract and check order (supersedes §16.1 and §16.2's table)

```json
{ "trace_id": "…", "schema_version": "ate/0.4", "run_id": "…|null",
  "tenant_id": "…", "agent_id": "…|null", "proposal_hash": "…|null",
  "created_at": "…" }
```

Status codes and every other §16.1 rule are unchanged. The caller's credential is now a tenant-scoped service token (§17.1).

| # | Condition | Result | Stored? |
|---|---|---|---|
| 1 | `404` | `422 trace_not_found` | No, retryable (§16.3) |
| 2 | `run_id` null, or differs from the proposal's | `refused_policy`, `trace_unresolved` | Yes |
| 3 | `tenant_id` differs from the key's tenant | `refused_policy`, `trace_tenant_mismatch` | Yes |
| 4 | `agent_id` null, or differs from the service token's immutable `delegated_agent_id` | `refused_policy`, `trace_agent_mismatch` | Yes |
| 5 | `proposal_hash` null, or differs from olive's hash of the submitted proposal | `refused_policy`, **`trace_proposal_mismatch`** | Yes |
| — | hometown unavailable or timed out | `503`, fail closed | No |
| — | hometown `401`/`403` to olive, or popeye refuses the service token | `503`, fail closed, operational alarm | No |

Check 5 is stored for the same reason as 2–4: the trace is append-only, so the fact cannot change.

**Check 4's reference value.** `delegated_agent_id` is derived by popeye from the same session key olive introspected for the proposal (§7), so the two always agree. olive asserts this when it receives the token: if `delegated_agent_id` or the token's `tenant_id` differs from olive's own introspection of the key, the token is discarded and the request fails closed with `503` and an alarm. That case means popeye or olive is broken, not the proposal.

### 17.4 Test additions

| Case | Expected |
|---|---|
| Fixture `proposal_hash` equals the submitted proposal's JCS hash | Passes check 5 |
| One character changed in `justification` | `trace_proposal_mismatch`, stored |
| `parameters` with the same content but a different key order or whitespace | Passes (JCS makes them identical) |
| `proposal_hash: null` with a proposal submitted | `trace_proposal_mismatch`, stored |
| Service token requested for a tenant olive isn't registered for (popeye stub) | `503`, alarm, nothing stored |
| Service token requested for an agent olive may not act for (popeye stub) | `503`, alarm, nothing stored |
| Token's `delegated_agent_id` or `tenant_id` differs from olive's introspection of the key (popeye stub) | Token discarded; `503`, alarm |
| Trace `agent_id` differs from the token's `delegated_agent_id` | `trace_agent_mismatch`, stored |
| Same agent and tenant, new session key | A new token is requested; the cached one is not reused |
| Token whose expiry is after the session key's | Rejected by olive; never cached |
| Capture of every outbound olive call | The session key appears only on calls to popeye |
| `parameters` value `"5"` against a schema expecting an integer | `400`; no coercion, nothing hashed |
| Unknown key inside `parameters` | Hashed as submitted, never stripped; if hometown's object lacked it, `trace_proposal_mismatch` |
| Service token reused after expiry | Not sent; a fresh token is requested first |
| Token absent from every log line and stored row | Grep finds none |

### 17.5 Requests

**On hometown**, for `HOMETOWN_v0.4` §2.5–§2.6:
1. `proposalHash` in the ATE and `proposal_hash` in the resolution response (§17.2). This closes §2.6's "Proposal binding" open item.
2. Rules 2–3 accept popeye's tenant-scoped service tokens as the credential for registered multi-tenant services (§17.1). hometown reads only `tenant_id` from them; `delegated_agent_id` is for olive.
3. `proposalHash` is computed over the proposal object hometown returns, after its shape check (§17.2 item 4).
4. Update the "Interim (olive)" paragraph to olive's ratified `trace_verification` values, `fixture | hometown` (§16.5).

**hometown agreement, 2026-10-20 (relayed by Oscar Mack): all four agreed**, and reflected in `HOMETOWN_v0.4` §2.5–§2.6 on `contract/ate-v0.4`:
1. `answer.proposalHash` in the trace and `proposal_hash` as the seventh resolution field; null when there is no proposal or hometown dropped one, absent on non-agent answers. REQ/TC-PROV-27.
2. §2.5 rule 2 rewritten: hometown checks olive's token with popeye and takes the tenant from its `tenant_id`; no tenant → `403`, bad or expired token → `401`; agent session keys are not accepted on this endpoint; hometown ignores `delegated_agent_id`. The registry owner is named as popeye. TC-PROV-21 rewritten against a popeye stub.
3. The proposal is hashed after hometown's shape check, which now also requires exactly four keys; nothing changes it between shape check, hash and response. REQ/TC-AGA-15.
4. The "Interim (olive)" paragraph uses `fixture | hometown` and the production bar.

**Also recorded:** hometown strengthened its enablement rule to match §16.6 item 3. The server serving both endpoints now refuses to start without the Postgres store and forced row-level security on `answer_trace`; CLI and CI runs on the file store are unaffected (TC-PROV-24, TC-AGA-10).

**On popeye:** the service registry with tenant **and** agent scope; a token-issuance interface that takes the agent session key and derives `tenant_id` and `delegated_agent_id` from it; token expiry capped at the key's; and introspection of those tokens for hometown (§17.1). The note to the popeye owner accompanies this record.

---

## Ratification (v0.5 amendments)

| Field | Value |
|---|---|
| Decision | **Ratified**: §17 as written in v0.5.1, including the reviewer's delegated agent binding and the token cache keyed on `key_id` |
| Ratified by | Oscar Mack (architecture and risk decisions) |
| Amends ratified text | §16.1 (credential and response shape), §16.2 (check order) |
| hometown owner agreement (§17.5 hometown requests) | **Agreed to §17.5 in full, 2026-10-20** (relayed by Oscar Mack) |
| popeye owner acknowledgement (§17.1 dependency) | **Acknowledged 2026-10-20** as a proposed dependency; not popeye ratification or implementation approval (§17.1) |
| 0.5.2 principal-type correction (`application`, not `service`) | **Confirmed** by Oscar Mack, 2026-10-20 |
| Date | 2026-10-20 |
| Effect | olive builds check 5 and the token client against fixtures and a popeye stub. The live resolver now also waits on popeye's service tokens |
| Change control | As §16: changes to the response shape need hometown's agreement and re-ratification |
