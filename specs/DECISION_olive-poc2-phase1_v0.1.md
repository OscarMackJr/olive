# Decision Record — Olive POC-2 Phase 1 (pre-implementation questions)

Version: 0.1
Status: **Proposed — not ratified.** Every "Proposed decision" below is a recommendation until the ratification block at the end is signed.
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
- Amend olive charter §2 "Refuses to own" bullet 1 to: *"Deciding what to propose. hometown generates proposals inside traced answers; the acting agent principal submits them. Olive refuses any proposal whose `trace_ref` does not resolve to a hometown ATE for the same `run_id`. Olive never generates proposals."*
- This closes Liam O6 and keeps the invariant intact: the AI suggestion is traced at hometown, the submitter is an identified agent, and olive still decides nothing about content.
- **Liam owner:** Oscar Mack (design and ratification). The Liam implementing agent supplies fixtures.
- **Joint compatibility review:** Oscar plus **one reviewer who did not write either side**. The hometown owner is suggested, since hometown's ATE is the join target. A review where one person approves both sides of the contract is weak evidence. Name the second reviewer here: ________.

## 4. Referenced specifications: locations

| Document | Where it is | Action |
|---|---|---|
| `OLIVE_CHARTER_v0.4.md` | `v04-poc2-specs.zip` → `olive/specs/` | Present |
| `PROGRAM_POC2_CHARTER_v0.4.md`, `PROGRAM_SHARED_VOCABULARY_v0.4.md`, `vocabulary.yaml` (v0.4, SHA-256 `60a44f00…e8e7`) | `v04-poc2-specs.zip`, `olive-agent-inputs.zip` | Vendor into `olive/contracts/vendored/` |
| `LIAM_DESIGN_SPEC_v0.4.md`, `LIAM_TEST_SPEC_v0.4.md` | Exist (ratified 2026-10-19); not yet in a repository | Commit to `liam/specs/`; copy or link into `fiskroad/specs/` |
| `POPEYE_v0.4.2.md` (and v0.4.1, v0.4.3) | **Not in any artifact available here** | Owner places in `popeye/specs/` and `fiskroad/specs/` |
| `NONO_v0.4.2.md`, `NONO_v0.4.3.md` | **Not available here** | Owner places in `nono/specs/` and `fiskroad/specs/` |
| `DECISION_agent-key-service-scope.md`, `DECISION_desktop-agent-nono-integration.md` | **Not available here** | Place in `fiskroad/specs/decisions/` |
| "Cross-component execution specification" | **Unknown.** No document by this name appears in any artifact here | Identify it by filename, or remove the reference from the prompt |

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
| `principal_type` | Must be `agent` on `POST /v1/proposals`. `user` and `service` → `403 principal_not_permitted` |
| `agent_id` | Must be in olive's agent allowlist (`liam` for POC-2) |
| `on_behalf_of` | Entitlement intersection (vocabulary §3.2); excluded from self-approval (§8) |
| `purpose` | Checked against the action's `required_purpose` before queueing |
| `tenant_id` | Proposal and queue-item tenant; must match the party's tenant |
| `expires_at` | Proposal expiry may not exceed it |

**Caller classes are separate paths.** Olive calls introspection as a registered **service** using its own managed identity. Agents and users can never reach olive's service path, and olive never accepts a service credential on the proposal endpoint (this is the prompt's "cannot use the Popeye introspection path as a service caller" test).

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

**Proposed decision.** Use the shared POC Postgres from Phase 1, with three tables (`proposal`, `queue_item`, `execution_ledger`), each carrying `tenant_id` with both isolation layers: an application predicate and database RLS (REQ-QUE-02 requires both, and a test that removes one layer). Append-only grants on the execution ledger. In-memory stores are for unit tests only. The JSONL output becomes the simulated executor's *target*, standing in for the CRM, not olive's ledger.

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
| Decision | §1–§12 as proposed / amended: ________ |
| Ratified by | ________ (architecture and risk decisions) |
| Informed | CTO ________, CSO ________ |
| Independent reviewer (§3) | ________ |
| Date | ________ |
| Effect | Olive Phase 1 starts **simulation-only** once the §4 artifacts are placed. Phase 2 (real write) requires a new record |
