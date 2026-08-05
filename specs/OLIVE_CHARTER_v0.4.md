# Olive — Action Broker And Review Queue: Charter v0.4

Version: 0.4
Status: **Draft charter for a new component** — requires approval per `PROGRAM_POC2_CHARTER_v0.4` §6.1
Repository: to be created. Codename `olive` is a placeholder; renaming is a find-and-replace (§6.2 of the program charter).
Owner of gaps: G1 (governed actions), G4 (review queue), G2's tool-governance third.
Primary post-read action: approve the component's existence and its boundary (§2) before any code. The boundary is the reason it exists.

---

## 1. Why A New Component

Actions cannot live in hometown: giving the semantic layer write access to divisional systems destroys the read-only boundary that makes divisions cooperate, which is the political foundation of the program.

They cannot live in popeye: executing an action requires knowing what it means, and the gateway is semantics-blind by invariant.

They cannot live in bluto: identity resolution has nothing to do with writing to a CRM, and bluto's small scope is a feature worth protecting.

**Olive exists to hold the one write credential the program has, in the smallest component that can hold it.** Every other placement violates an invariant. That is the whole argument.

---

## 2. Boundary

### Owns
- The **action catalog**: versioned action type definitions.
- **Proposal records**: what an AI suggested, with its trace reference.
- **Authorization**: purpose and entitlement checks against the requester and the action's requirements.
- **The review queue**: shared across olive, bluto and evaluation (G4).
- **Execution**: the narrow write credential, idempotent execution, and the execution ledger.
- **Tool policy** for tools it exposes (G2's third).

### Refuses to own
- **Deciding what to propose.** hometown proposes; olive validates, routes and executes. If olive starts generating proposals it has become an AI feature, and we do not build AI features.
- **Model calls.** Olive never calls a model. If it needs one, it goes through popeye like everything else.
- **Identity resolution.** Party identifiers arrive from bluto by reference.
- **Business rules of the target system.** The CRM's own validation is the CRM's; olive does not reimplement it.
- **Any read path for AI context.** Olive is not a data source.

---

## 3. Actions (G1)

### 3.1 Action type definition

```
action_id:          "crm.case_note@1"      // versioned; never mutated in place
target_system:      "nexus_crm"
parameters:         <JSON Schema>
required_purpose:   ["servicing", "collections"]
required_approval:  "role:case_supervisor"
reversible:         true
max_rate:           <per principal, per window>
```

Action types are source-controlled and reviewed. **Adding an action type is a change-managed event, not configuration** — this is the mechanism by which the blast radius of AI agency is deliberately bounded.

### 3.2 Lifecycle

`proposed` → `refused_policy` | `awaiting_approval` → `approved` | `rejected` → `executed` | `execution_failed` | `expired`

**Design rules:**

1. **Policy check precedes human review.** A proposal the system would refuse never reaches a queue — otherwise approvers learn to rubber-stamp, and the queue becomes a formality. This ordering is what makes beat 4 meaningful.
2. **Approval is per proposal, never per action type.** No standing approvals in this revision.
3. **Proposals expire.** An approved-but-unexecuted proposal from last Tuesday may no longer be safe; default expiry measured in hours.
4. **Irreversible actions require a stricter path** — dual approval, and out of scope for POC-2's single action type, which is deliberately reversible.
5. **Execution is idempotent**, keyed on `proposal_id`. A retry never produces two case notes.

### 3.3 The write credential

One credential, narrowest possible scope on the target system, held only by olive, in a vault, accessed by managed identity. Every use is logged with the `execution_id`. Rotation is a documented procedure with a named owner from day one — the failure this prevents is the credential outliving everyone who understood it.

| REQ | Requirement | TC | Test criteria | Method |
|---|---|---|---|---|
| REQ-ACT-01 | Every execution traces to a proposal, a policy decision and a named approver | TC-ACT-01 | Beat 2: full chain queryable from the execution record | DEMO |
| REQ-ACT-02 | Policy refusal occurs before queueing | TC-ACT-02 | Beat 4: out-of-purpose proposal never appears in the queue; refusal traced | DEMO + CI |
| REQ-ACT-03 | Execution is idempotent on `proposal_id` | TC-ACT-03 | Duplicate execution attempt produces one write, second returns the first result | CI |
| REQ-ACT-04 | No component other than olive holds the write credential | TC-ACT-04 | Credential audit across repos | AUDIT |
| REQ-ACT-05 | Expired proposals cannot be executed | TC-ACT-05 | Negative test past expiry returns a distinct error | CI |
| REQ-ACT-06 | Adding an action type requires review | TC-ACT-06 | Catalog is source-controlled; runtime-added types rejected | CI |

---

## 4. The Review Queue (G4)

Built once, three consumers: olive's approvals, bluto's identity review cases, evaluation flags.

**Item shape** — deliberately content-free:

```
item_id, kind, tenant_id, purpose?, subject_ref, payload_ref,
state, claimed_by, decided_by, decision, decided_at, rationale
```

**`payload_ref` is a reference, never a body.** The queue does not become a second copy of anything sensitive; a reviewer's client dereferences it under their own entitlements. This is the join-never-copy invariant applied to a workflow store, and it is also what stops the queue becoming an attractive target.

**Deliberately minimal:** list, claim, decide, audit. No dashboards, no bulk operations, no SLA tracking, no assignment rules. This is the program's second and final UI exception and it stays as narrow as the trace viewer.

| REQ | Requirement | TC | Test criteria | Method |
|---|---|---|---|---|
| REQ-QUE-01 | Queue items carry references, never record bodies | TC-QUE-01 | Schema test; negative test rejects an inline payload | CI |
| REQ-QUE-02 | Queue items are tenant-scoped with the same two layers as elsewhere | TC-QUE-02 | Layer-removed negative test, as for other stores | CI |
| REQ-QUE-03 | Every decision records who, when and why | TC-QUE-03 | Decision without `decided_by` or `rationale` rejected | CI |
| REQ-QUE-04 | bluto's review cases route through this queue | TC-QUE-04 | An ambiguous match appears as a queue item | DEMO |

---

## 5. Tool Policy (G2, third)

For POC-2 the only governed tool is the action broker itself. The general contract is written now and implemented later:

- An agent's tool grants are declared per agent principal.
- Effective grants are the intersection with `on_behalf_of` (vocabulary §3.2).
- Every tool invocation records `run_id`, agent, tool, outcome — never arguments containing record values.
- Rate limits are per agent, per tool, per window.

**Deliberately not built in POC-2:** a general tool registry, third-party tool integration, or Model Context Protocol server governance. Named so the boundary is visible when someone proposes it — which they will.

---

## 6. Open Decisions

1. **Codename** — `olive` is a placeholder.
2. **Language and stack** — bluto is C#/.NET, hometown TypeScript. Olive is closer to bluto in shape (transactional, small, credential-holding); recommendation is .NET for reviewer familiarity, but this is the team's call.
3. **Target system for POC-2** — recommendation: a CRM case note. Reversible, low blast radius, obviously useful, and the divisional conversation is tractable.
4. **Queue front end** — minimal web UI versus a Teams-based approval flow. Teams is faster to adopt and harder to audit; the web UI is the safer default given every approval is an audit record.
5. **Where olive's own audit lands** — its execution ledger is a governed store; whether it also anchors into zt-infra's append-only log is a v0.5 question.
