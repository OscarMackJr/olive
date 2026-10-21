# Feature Research

**Domain:** A governed AI-action broker with a minimal human-in-the-loop review queue (maker-checker for agent-proposed writes)
**Researched:** 2026-10-21
**Confidence:** MEDIUM overall. The ratified specs are HIGH-confidence ground truth for what Olive must do. The comparisons with other systems are MEDIUM: they come from official docs for GitHub Environments, Azure Pipelines approvals and the Camunda task lifecycle, and from vendor and community sources for LangGraph `interrupt`, Temporal HITL, maker-checker products and Camunda claim timeouts.

**How to read this file.** The ratified records (Charter v0.4, Decision v0.5.2 §1–§17, IR-1 v0.1) are binding. Where common practice goes further than the specs, this file **flags a gap**. It does not change a decision. Every gap is marked **[GAP]** and needs one of two outcomes: a ruling from the ratifier, or an implementation choice that stays inside the ratified text. The charter's "deliberately minimal" list (no dashboards, no bulk operations, no SLA tracking, no assignment rules) is binding and appears under Anti-Features.

---

## How comparable systems work (summary)

| Pattern | Common practice | Olive's spec position |
|---|---|---|
| **HITL gate for agent tool calls** (LangGraph `interrupt`, Temporal HITL, Connic and similar approval products) | The agent pauses at a checkpoint and persists its state. A human approves, rejects or **edits** the call. A timeout resolves to reject or fail. Every decision is logged. | Olive is stricter. The agent has already exited (Liam makes one request and exits). The proposal is a durable record, not a paused graph. **Editing is not allowed** because `proposal_hash` binds the content (§17.2). |
| **Maker-checker / four-eyes** (banking ops, KYC "4-eyes review", GitHub "prevent self-review", the Azure Pipelines "approvers can't approve own runs" setting) | The initiator can never approve, and the server enforces it (often with a 403). The change is staged and only applied after a checker signs off. A rejection carries a reason. Only one pending change per item. | Matches. No self-approval (approver ≠ `on_behalf_of`), approval per proposal, rationale required, one proposal per `(agent_id, run_id)`. |
| **Policy before review** (Azure checks run alongside approvals; policy-as-code gates) | Automated checks remove whatever would be refused anyway, so humans only see items that can actually be approved. | Core design rule (Charter §3.2.1, REQ-ACT-02). Olive is stricter than most systems: refused items are never queued at all. |
| **Approval timeout** (Azure Pipelines: the stage is skipped when the approval times out; Temporal sample: auto-timeout result; GitHub wait timers capped at 30 days) | A pending approval expires to a terminal non-execute state. Retrying it means a new run. | 8 h default, can only be shortened, capped at the key's `expires_at`, and returns a distinct error (§9, REQ-ACT-05). Identity items **never expire** (IR-1 §4). |
| **Human task claim** (Camunda and Flowable tasklists) | Claim assigns the task and **unclaim** returns it to the candidate pool. Neither engine has a built-in claim lease: teams add timers or jobs that auto-unclaim. | IR-1 states are `open → claimed → decided` or `withdrawn`. **There is no unclaim or claim timeout** (see GAP-1). |
| **Idempotent command execution** (Stripe-style idempotency keys, transactional outbox, ledger with a unique key) | A unique constraint on the command key. The first result is stored and replayed. Intent to execute is written in the same transaction as the state change. | Submission is idempotent on `(agent_id, run_id)` and execution on `proposal_id` (§6, REQ-ACT-03). The INSERT-only ledger and `proposal_event` hold the audit trail (§13). The execution trigger is unspecified (see GAP-6). |

---

## Feature Landscape

### Table Stakes (Must Have)

These features are required by the specs or by the core value. If one is missing, the audit chain cannot be proven.

#### A. Proposal submission API

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| `POST /v1/proposals` with authenticated agent principal only | The maker must be an identified principal. Users and applications get `403 principal_not_permitted`. | MEDIUM | Introspection stub behind an interface; allowlist `liam`. Introspection always runs first, including on retries (§15.2.1). |
| Submission idempotency on `(agent_id, run_id)` with JCS `body_sha256` | Retried agent calls are normal. A duplicate must not create a second queue item (a known scaffold defect: duplicate enqueue on resubmit). | MEDIUM | Same hash returns `200` with the **current** state. A different hash returns `409`. A unique constraint on `(tenant_id, agent_id, run_id)`; the loser of a race re-reads. |
| Non-mutating schema validation; `400` is not stored | Required so that the hash covers exactly what was sent (§17.2.4). | MEDIUM | .NET JSON Schema validators and `System.Text.Json` both coerce or strip by default in some modes. Validate the raw `JsonElement`. No defaults, no stripping, and `"5"` is not an integer. |
| Ordered pipeline: introspect → principal → idempotency → schema/catalog → trace → policy → persist+enqueue | The order defines the semantics: what gets stored, what is retryable and what calls the resolver. | MEDIUM | Each step has a distinct, testable outcome code. Write the pipeline as explicit steps, not middleware magic. |
| Distinct error vocabulary | Callers and auditors need to tell refusal reasons apart. | LOW | `key_inactive`, `principal_not_permitted`, `idempotency_conflict`, `trace_not_found` (422, `retryable: true`), `trace_unresolved`, `trace_tenant_mismatch`, `trace_agent_mismatch`, `trace_proposal_mismatch`, `not_in_catalog`, policy reasons, `expired`. Use an RFC 9457 problem-details body (MEDIUM confidence that this is the idiomatic choice in ASP.NET Core). |
| `GET /v1/proposals/{id}` (tenant-scoped, current state) | The submitter or an operator needs to observe the outcome after Liam exits. | LOW | Return state, refusal reason, `expires_at` and decision summary. Never return another tenant's proposal: a cross-tenant ID returns `404`, not `403`. |
| `GET /v1/catalog/{version}` | Liam validates against the catalog's parameter schemas. | LOW | Read-only and file-backed. No write verb exists (REQ-ACT-06). |

#### B. Policy refusal before queueing

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Purpose check against `required_purpose` | Beat 4: an out-of-purpose proposal never reaches a human. | LOW | Uses `purpose` from introspection, never from the body. |
| Entitlement intersection (agent ∩ `on_behalf_of`) | Charter §5, vocabulary §3.2. | MEDIUM | **Fail closed on null or empty grants.** Fail-open on null grants is a known scaffold defect. |
| Per-agent rate limit | Charter §3.1 `max_rate`. | MEDIUM | Count only **accepted** proposals. Refusals consuming the budget is a known scaffold defect. The count must be DB-backed and concurrency-safe (count inside the persist transaction, or use an advisory lock per `(tenant, agent, action)`). |
| Unknown or retired catalog action → stored `refused_policy` / `not_in_catalog` | Today the scaffold throws instead of refusing (known defect). | LOW | Stored under the idempotency key per §15.2. |
| Policy decision persisted as an auditable record | REQ-ACT-01 says every execution traces to "a policy decision". A state flag alone does not prove what was checked. | LOW–MEDIUM | Record the evaluated inputs and outcome (purpose, action, entitlement result, rate count, catalog version, `trace_verification`, `trace_schema_version`) as a `proposal_event`. **[GAP-9]** The specs require the decision to exist but do not define its shape. |
| Refused proposals are never visible in the queue | TC-ACT-02. | LOW | Enforce this structurally: create the queue item only on the `awaiting_approval` path. Add a negative test on the list endpoint. |

#### C. Review queue (generic, IR-1 model)

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Item shape with `subject_ref` and `payload_ref` only; inline bodies rejected | REQ-QUE-01, TC-QUE-01, TC-IRQ-01. | LOW | Validate the reference format per kind. Reject any unknown field (`400 invalid_request`). |
| Kind registry (source-controlled): `action_approval` and `identity_review/candidate_pair` | One queue serves three consumers. `link_challenge` is rejected (TC-IRQ-11). | MEDIUM | Each kind declares its allowed decisions, required role, whether it expires, its create path and its pre-queue gate. **Do not add a runtime kind registration API.** That follows the same reasoning as REQ-ACT-06. |
| **List** (tenant-scoped, filtered by kind and state, shows item age) | Reviewers need to find work. IR-1 §4 requires age to be shown. | LOW | Plain ordering (oldest first). No saved views and no assignment. |
| **Claim** (open → claimed, records `claimed_by`) | Stops two reviewers working the same item. | MEDIUM | Use a conditional update (`WHERE state='open'`). Report the loser as "already claimed". |
| **Decide** (records `decided_by`, `decided_at`, `decision`, `rationale`) | REQ-QUE-03. A decision without who or why is rejected. | MEDIUM | `decided_by` comes from the authenticated identity, never from the form body. Each kind has its own decision values: action approval is approve or reject; identity review is accept, reject or defer (defer returns the item to `open`). |
| **Audit** (per-item decision history including defers) | TC-IRQ-06; every approval is an audit record (Decision §11). | LOW–MEDIUM | Append-only history rows covering create, claim, defer, decide and withdraw. **[GAP-2]** IR-1 says olive stores "decision history", but §13 grants INSERT-only to `proposal_event` and `execution_ledger` only. `queue_item` history needs an equivalent INSERT-only table, or must use `proposal_event` for the action kind. The identity kind has no proposal. |
| Withdraw (identity kind, undecided only, with reason code) | IR-1 §4, TC-IRQ-09. | LOW | Uses the reason codes `superseded_by_rule`, `source_changed` and `ruleset_retired`. A decided item cannot be withdrawn. |
| Create endpoint for the identity kind with server-assigned fields forbidden | IR-1 §4, TC-IRQ-03. | MEDIUM | `tenant_id` and `producer` must come from the token. A body containing them gets `400 server_assigned_field`. Idempotency key is `(producer, tenant_id, producer_ref)`. Only a stub caller exists in Phase 1. |
| Decision polling with a `(decided_at, item_id)` cursor | IR-1 §6. bluto pulls, and olive never calls out. | LOW–MEDIUM | Stable keyset pagination with tie-break ordering. Tenant-scoped per token. |
| Two-layer tenant isolation on every queue query | REQ-QUE-02, TC-QUE-02 (layer-removed negative test). | MEDIUM | See **[GAP-5]** for where a *reviewer's* tenant comes from. |

#### D. Approval rules

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Authenticated reviewer only (Entra OIDC, with a dev stub) | Decision §8. An approval without an authenticated identity is rejected. | MEDIUM | The stub IdP must be impossible to enable in `production` (mirror the §16.5 startup refusal). **[GAP-10]** The specs bar fixtures in production but say nothing about the dev IdP stub. Treat it the same way. |
| No self-approval: approver ≠ `on_behalf_of` | The universal maker-checker rule, enforced server-side. | LOW–MEDIUM | **Compare like with like.** `on_behalf_of` comes from popeye introspection, and `decided_by` from the Entra token (`oid`? UPN?). If the identifier formats differ, the check passes vacuously. **[GAP-3]** No spec names the canonical user identifier the two sides share. Also, agent principals can never decide (enforced by principal type). |
| Role checked at **decision time** | Decision §8 and IR-1 §3. Membership may have been revoked since the claim. | MEDIUM | Re-evaluate group membership from a fresh token or a Graph lookup when the decision is made. A role cached at sign-in is stale for up to the token lifetime (MEDIUM confidence: Entra group claims in tokens are only refreshed when a new token is issued). Per kind: `role:case_supervisor` or `role:identity_steward`. |
| Rationale required on approve **and** reject | REQ-QUE-03. | LOW | Reject empty or whitespace-only rationale. The UI warns not to include record values (IR-1 §2). Cap the length. |
| Approval per proposal; no standing approvals | Charter §3.2.2. | LOW | Enforced by design: no rule engine. |
| Expired proposals cannot be approved or executed (distinct error) | REQ-ACT-05, TC-ACT-05. | LOW–MEDIUM | Check `expires_at` against the DB clock inside the decide and execute transactions. Lazy evaluation is enough for correctness. See **[GAP-4]** for how expired items leave the queue. |

#### E. Expiry

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| 8 h default, per-action catalog maximum, `expires_in_seconds` can only shorten, capped at the key's `expires_at` | Decision §9. | LOW | Compute once at persist time and store as an absolute `expires_at`. Never recompute. |
| Expired proposals end in `expired` (terminal) | Lifecycle (Charter §3.2). | LOW–MEDIUM | Transition on access (decide, execute, read). An optional sweeper can tidy up but must not be needed for correctness. |
| Identity items never expire | IR-1 §4. | LOW | The kind registry declares `expires: false`. |

#### F. Idempotent execution and ledger

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Execution keyed on `proposal_id`; a retry returns the first result | REQ-ACT-03, TC-ACT-03 (one write; the second call returns the first result). | MEDIUM | A unique constraint on `execution_ledger(proposal_id)`. Claim execution with a conditional state transition `approved → executing`, or rely on the insert-first ledger row. Never use read-then-write. |
| Simulated executor behind the real executor interface, `execution_mode = "simulated"` | Decision §2. No network write and no HTTP client. | LOW | Already exists in the scaffold. The JSONL file is the *target* (the stand-in CRM), not the ledger. |
| Ledger is INSERT-only by grant; state changes paired with `proposal_event` | Decision §13 immutability. | MEDIUM | Test that `olive_app` cannot UPDATE or DELETE either table. |
| Every credential use logged with `execution_id` | Charter §3.3. | LOW | In Phase 1 this is the credential-path interface with no secret behind it. The log line is still written. |

#### G. Audit chain queries

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| Starting from the execution record, query: proposal → trace ref + `trace_verification` → policy decision → queue decision (who, when, why) → execution result | REQ-ACT-01, TC-ACT-01 (beat 2 demo). This is the core value. | MEDIUM | A read-only query (or view) run as `olive_reader`. It should be one call that returns the whole chain, not a manual join done during the demo. **[GAP-8]** No endpoint or view is specified. The UI audit view or a SQL view both satisfy "queryable". Choose one deliberately. |
| Proposal event history (all transitions, with actor) | Lets reviewers and auditors see how the state was reached. | LOW | Read `proposal_event` ordered by time. |
| Refusal is traceable (beat 4) | TC-ACT-02 says "refusal traced". | LOW | The stored `refused_policy` proposal and its policy event make the refusal queryable even though it never appeared in the queue. |

#### H. Minimal reviewer UI (Razor Pages)

| Feature | Why Expected | Complexity | Notes |
|---|---|---|---|
| List, claim, decide and audit pages only | Charter §4 and Decision §11. | MEDIUM | Server-rendered with no JS build. Use anti-forgery tokens on every POST (ASP.NET Core does this by default for Razor Pages forms; keep it on). |
| Reviewer sees enough to decide (action, parameters, justification, party ref, trace ref, `trace_verification`, policy outcome, `expires_at`) | A reviewer who can't see what they are approving will rubber-stamp it. | MEDIUM | For the action kind, olive dereferences its own proposal under the reviewer's tenant. For the identity kind, the reviewer's client resolves `payload_ref` at bluto. Olive never fetches it (IR-1 §5). In Phase 1 that is a link or placeholder. Show a clear "trace verified by FIXTURE" banner (§14.4: demos must say which source applied). |
| Show expiry and age; disable decide on expired items | Prevents a wasted rationale and a confusing error. | LOW | The server still enforces it. |
| Explicit confirmation of the decision value | Approve and reject sit next to each other on the form. | LOW | Use separate submit buttons with a required rationale. No one-click approve from the list page. |

---

### Differentiators (Valuable, Not Strictly Required)

These line up with the core value: a provable chain and approvals that are not rubber stamps.

| Feature | Value Proposition | Complexity | Notes |
|---|---|---|---|
| **Re-check policy at decision time and again at execution time** (purpose, entitlements of `on_behalf_of`, key still within `expires_at`, catalog version not retired) | Closes the TOCTOU window. The policy passed at submission, but up to 8 h may have passed. Mature approval systems re-run their checks when the approval lands (Azure re-evaluates checks; MEDIUM confidence). | MEDIUM | **[GAP-7]** The specs check the *reviewer's* role at decision time but say nothing about re-checking the *proposal's* policy. Recommend at least re-checking catalog validity and expiry at execute time, both of which fall inside existing rules. Re-running the entitlement check needs a ruling because the introspection result may no longer be available after the key expires. |
| **Decision-time snapshot shown to the reviewer = hashed content** | The reviewer sees exactly the fields covered by `proposal_hash`, so what was approved is what was traced. | LOW | Show `body_sha256` and the proposal hash on the audit page. This is cheap and strong evidence. |
| **Single "chain" view for an execution** (UI audit page backed by the chain query) | Turns REQ-ACT-01 into a one-click demo for beat 2. | LOW–MEDIUM | It is the audit view the charter already allows, so it adds no new UI surface. |
| **Explicit claim release by the claimant** (`claimed → open`, recorded in history) | Matches Camunda and Flowable unclaim. It stops a claim from blocking a proposal until it expires, and stops a claim from blocking an identity item forever. | LOW | See **[GAP-1]**. It is a fifth verb, so it needs a ruling on whether it counts as part of "claim" or is new scope. |
| **Optimistic concurrency token on decide** (row `version` or `xmin` compare-and-set) | Exactly one decision wins even with double-submits or two tabs. The conditional update also gives a clean "already decided" response. | LOW | See **[GAP-6b]**. Implementing it as a conditional UPDATE stays inside "state guards on every transition", so it needs no ruling. |
| **Operational alarms as structured events** (resolver 401/403, token claim mismatch, fixture in a wrong environment) | The spec requires alarms (§16.2, §17.3). Making them structured and greppable makes the evidence for them cheap to produce. | LOW | Log events only. No alerting product. |
| **Outbound-call capture harness** (session key only reaches popeye; no token in logs) | Required evidence (§17.4) that can be reused as a regression test. | MEDIUM | A `DelegatingHandler` that records each host and header name. Run it in CI. |

---

### Anti-Features (Deliberately NOT Built)

The first block is **binding** (Charter §4 "deliberately minimal", §5, Decision §2/§11, AGENTS.md). The rest are common HITL features that would break an Olive invariant.

| Feature | Why Requested | Why Problematic | Alternative |
|---|---|---|---|
| **Dashboards / queue metrics UI** | "How's the queue doing?" | Binding exclusion (Charter §4). It also grows the program's second and final UI exception. | Use `olive_reader` SQL, or a later Cube model (Decision §13). |
| **Bulk approve or reject** | Clearing a backlog quickly. | Binding exclusion. It is also the main way approvals turn into rubber stamps, which defeats Charter §3.2.1. | One item at a time with a rationale for each. |
| **SLA tracking, escalation, reminders** | Items sitting too long. | Binding exclusion. | Expiry is the time bound. The list shows item age. |
| **Assignment or routing rules** (round-robin, auto-assign, skills) | Spreading work across reviewers. | Binding exclusion. | Reviewers self-claim. |
| **Approving inside Teams or Slack** (actionable cards) | Faster adoption. | Decision §11: harder to audit. | Later: a notification that only links to the web UI. |
| **Reviewer edits the proposal ("approve with changes")** | LangGraph and similar HITL tools allow editing tool arguments before resuming. | Breaks `proposal_hash` binding (§17.2). What runs would no longer trace to a hometown answer, so REQ-ACT-01 fails. IR-1 §3 also forbids inline edits. | Reject with a rationale. The agent submits a new run. |
| **Standing or blanket approvals, auto-approve "low risk"** | Less reviewer load. | Charter §3.2.2 requires approval per proposal and allows no standing approvals. | None in this revision. |
| **Dual approval or quorum** | Higher assurance. | Out of scope: the only action is reversible (Charter §3.2.4). | Add it with the first irreversible action type, under a new record. |
| **Runtime action-type or queue-kind registration API** | Flexibility. | REQ-ACT-06 and AGENTS.md: adding types is change-managed. The same reasoning applies to kinds. | Source-controlled catalog and kind registry. |
| **Inline payloads / copying record bodies into the queue** | Convenience for reviewers. | REQ-QUE-01, join-never-copy. It would also make the queue an attractive target. | `payload_ref` dereferenced under the reviewer's own credential. |
| **Callbacks or webhooks to producers** (olive → bluto, olive → Liam) | Push is "real-time". | IR-1 §6: olive never calls bluto. It adds an outbound credential path and retry semantics. | Producers poll with a cursor. |
| **Proposal generation, model calls, AI summaries of items or "suggested rationale"** | Reviewer convenience. | Charter §2 refuses to own these: olive does not build AI features. An AI-written rationale defeats "named reviewer with a rationale". | None. |
| **Comment threads, reopen or un-decide** | Collaboration and correcting mistakes. | They mutate the audit record. A decided item is terminal (IR-1). | A new proposal or candidate. A reversible action is undone in the target system. |
| **Delegation or "approve on behalf of"** | Reviewer absence. | Weakens the named-approver guarantee and can get around the self-approval check. | Any member of the group can claim. |
| **Real executor, connector, HTTP client or write credential** | "Just wire it to Nexus". | Decision §2 and AGENTS.md: Phase 2 needs a signed agreement and CTO/CSO sign-off. | Simulated executor behind the same interface. |
| **General tool registry or MCP governance** | Natural extension. | Charter §5 names it as not built. | None. |
| **Cross-tenant reviewer or audit views** | Central ops team. | Decision §13: this is a Phase 2 decision. RLS sees one tenant per session. | Per-tenant sessions. |

---

## Gaps Between Common Practice and the Ratified Specs

These are flagged, not overridden. Each gap lists whether a ruling is needed (the change touches ratified text or adds scope) or whether the implementer can resolve it within the existing rules.

| ID | Gap | Common practice | Risk if ignored | Suggested handling |
|---|---|---|---|---|
| **GAP-1** | **No claim release or claim timeout.** IR-1 states are `open → claimed → decided` plus `withdrawn`, and the only route back to `open` is a `defer` decision. | Camunda and Flowable have an explicit unclaim. Teams commonly add timer-based auto-unclaim because neither engine has a lease. | An absent claimant blocks an action proposal until it expires (≤8 h, which fails the proposal silently). For identity items, which never expire, the item is **blocked forever**. | **Needs ruling.** The minimal option is claimant self-release (`claimed → open`, recorded). Do not add lease timers or admin reassignment: they edge into "assignment rules". Interim within the ratified text: the claimant can `defer` (identity kind only). The action kind has no defer. |
| **GAP-2** | **Queue history storage is not in the §13 grant table.** IR-1 makes olive the system of record for "decision history", but only `proposal_event` and `execution_ledger` are INSERT-only. | Append-only event table per aggregate. | Identity-kind history (claims, defers, withdrawals) would live in UPDATEd `queue_item` rows, where it can be rewritten. | **Needs ruling or a new revision** (Decision §13 lists four tables). Suggest a `queue_item_event` table that is INSERT-only for `olive_app`, using the same RLS pattern. |
| **GAP-3** | **Identifier comparability for self-approval.** `on_behalf_of` comes from popeye introspection and `decided_by` from Entra. The specs don't say these use the same identifier space. | Compare immutable directory object IDs, not display names or UPNs. | The self-approval check passes vacuously because the IDs are in different formats. That is a silent control failure. | Confirm against `POPEYE_v0.4.2` and the vocabulary (stop rule if absent). Add a test where the same person appears in both forms. If formats differ, **fail closed** (refuse the decision). |
| **GAP-4** | **Expired action proposals versus queue state.** The IR-1 item states have no `expired`, and the spec doesn't say how an expired proposal's item leaves the queue. | Timeout resolves to a terminal skipped or rejected state (Azure, Temporal). | Expired items clutter the list, or a reviewer writes a rationale and then gets an error. | Within the ratified text: hide expired items from the default list and show them in audit. The item's terminal representation needs a decision. Options are `decided` with a system decision of `expired` (needs a ruling, because it adds a decision value) or `withdrawn` with reason `expired` (also a new reason code). Flag for a ruling. A lazy filter is enough for Phase 1. |
| **GAP-5** | **Reviewer tenant context.** RLS requires `olive.tenant_id` on every transaction, taken from "the introspected key" (§13). Reviewers come in through Entra, not popeye keys. | The tenant is derived from the reviewer's directory membership or an explicit tenant selection checked against their groups. | The UI either cannot query anything, or someone sets the tenant from a request parameter, which is a probing risk. | **Needs ruling.** The tenant must come from a credential claim or group mapping, never from a free parameter. One session sees one tenant (cross-tenant is a Phase 2 decision). |
| **GAP-6** | **Execution trigger and failure semantics.** The specs don't say who triggers execution after approval (synchronous on decide, or a worker), or whether `execution_failed` can be retried. "A retry returns the first result" would make a transient failure permanent. | A transactional outbox (intent written with the approval) and a worker. Transient and terminal failures are told apart. A ledger row only records terminal outcomes, or records attempts separately. | A crash between approval and execution leaves the proposal `approved` and never executed until it expires. Alternatively, a retry semantic that contradicts REQ-ACT-03. | Phase 1 (simulated) can execute synchronously in the decide transaction's follow-up, with a startup or periodic sweep of `approved` items that have no ledger row. Treat `execution_failed` as terminal for Phase 1 and **flag the retry semantics for the Phase 2 record**. |
| **GAP-6b** | **Optimistic concurrency on decide and claim.** The specs say "state guards on every transition" but not how concurrent decides are handled. | A compare-and-set (row version or a conditional UPDATE on state and claimant). | Two reviewers, or a double POST, could record two decisions. | Implementer choice within the ratified text: a conditional UPDATE with `WHERE state = 'claimed' AND claimed_by = @me`. Zero rows means `409`. No ruling needed. |
| **GAP-6c** | **Must the decider be the claimant?** The specs don't say. | Usually yes: claim gives exclusive right to complete. | Claim becomes advisory only. | Implementer choice. Require `claimed_by = decided_by` (stricter, matches common practice). Note this in the requirements. |
| **GAP-7** | **No proposal policy re-check at decide or execute time.** Only the reviewer's role is re-checked. | Checks are re-evaluated when the approval lands. | An entitlement revoked during the 8 h window is still honoured. | Within the ratified text: re-check catalog validity and expiry at execute time. Full entitlement re-evaluation **needs a ruling** (it may need introspection data after the key has expired). |
| **GAP-8** | **No named audit-chain query surface.** | A dedicated audit endpoint or view. | The beat 2 demo relies on an ad-hoc SQL join. | Implementer choice: a SQL view for `olive_reader` plus the UI audit page. A new API endpoint is additive within v1, but it changes the OpenAPI contract that goes to the §3 review, so mention it there. |
| **GAP-9** | **Policy decision record shape not specified.** | Persist the policy inputs, the outcome and the policy version. | "Traces to a policy decision" ends up resting on a state flag. | Implementer choice: a `proposal_event` row of type `policy_evaluated` with the inputs and outcome. Include the catalog version and git SHA. |
| **GAP-10** | **Dev IdP stub has no production bar.** §16.5 bars the fixture resolver in production. Nothing bars the stub IdP or the popeye stub. | Refuse to start with dev auth in production. | A misconfiguration lets anyone approve. | Implementer choice that extends the spirit of §16.5: refuse to start in `production` with any stub (IdP, popeye introspection, popeye token). |
| **GAP-11** | **No withdraw or cancel for action proposals.** IR-1 has withdraw for identity items. The action lifecycle has no `withdrawn` or `cancelled`. | The requester can cancel a pending request. | A wrong proposal (or a compromised agent) sits in the queue until a reviewer rejects it or it expires. | **Do not add without a ruling** (it would change the lifecycle in Charter §3.2). Mitigation within the ratified text: a reviewer rejects it with a rationale, and expiry bounds the exposure. Flag for Phase 2. |
| **GAP-12** | **Decide retry idempotency.** | Repeating the same decision returns the original result. | A double-click returns an error page and confuses the audit. | Implementer choice: if the item is already decided by the same user with the same decision, return the existing decision. Otherwise return `409`. |

---

## Feature Dependencies

```
Identity: popeye introspection stub (agent)      Entra OIDC + dev IdP stub (reviewer)
        │                                              │
        └──requires──> Postgres + roles + forced RLS + tenant session setting <──requires── Reviewer tenant derivation [GAP-5]
                              │
            ┌─────────────────┼────────────────────────────┐
            v                 v                            v
  Proposal submission   Generic queue core            Execution ledger (INSERT-only)
  (idempotency, JCS,    (kind registry, item,              │
   non-mutating schema) list/claim/decide/audit,           │
            │            history table [GAP-2])            │
            v                 │                            │
  Catalog + trace resolver    │                            │
  (fixture) + token stub      │                            │
            v                 │                            │
  Policy (purpose, entitlement│                            │
   intersection, rate)        │                            │
            │                 │                            │
            └──persist+enqueue──> action_approval kind      │
                                  │                        │
                     Approval rules (authn reviewer,       │
                     no self-approval [GAP-3],             │
                     decision-time role, rationale,        │
                     expiry guard [GAP-4])                 │
                                  │                        │
                                  └──approved──> Idempotent simulated execution [GAP-6]
                                                           │
                                                           v
                                        Audit chain query / view [GAP-8]
                                                           │
                                                           v
                                           Reviewer UI audit page; beat 2 / beat 4 demo

identity_review kind ──requires──> Generic queue core + create endpoint (server-assigned fields
                                     forbidden) + withdraw + decided-items cursor polling
Reviewer UI ──requires──> Queue core + approval rules + Entra stub
Claim release [GAP-1] ──enhances──> Queue core (ruling needed)
Reviewer edit-before-approve ──conflicts──> proposal_hash binding (§17.2)
Bulk approve ──conflicts──> Charter §3.2.1 / §4 (binding)
```

### Dependency Notes

- **The queue core comes before both kinds.** IR-1 and the action approval share the state machine and history. Build it generic first so `identity_review` is a registration, not a fork.
- **Persistence and RLS come before everything that stores data.** Every queue, proposal and ledger test depends on the tenant session setting. The reviewer tenant derivation (GAP-5) blocks the UI, not the API.
- **Policy requires the trace resolver.** Trace checks 2–5 run before policy (§15.2). Policy refusals and trace refusals share the stored `refused_policy` path.
- **Execution requires the approval rules.** Execution must assert `approved`, not expired and not already executed. The chain query requires every earlier link to be persisted as an event.
- **The UI comes last.** It only adds presentation and OIDC on top of domain operations that are already tested. The domain enforces every rule; the UI enforces none on its own.

---

## MVP Definition (Phase 1, simulation-only)

### Launch With

- [ ] Submission API with idempotency, non-mutating validation and the ordered pipeline. This is the entry to the chain.
- [ ] Trace checks 1–5 against fixtures, with the production bar.
- [ ] Policy before queueing (purpose, entitlement intersection fail-closed, rate counted on accepted proposals only) with a persisted policy event.
- [ ] Lifecycle with state guards, expiry and a distinct error.
- [ ] Generic queue: list, claim, decide and audit; append-only history; two kinds.
- [ ] Approval rules: authenticated reviewer, no self-approval with comparable IDs, role checked at decision time, rationale required.
- [ ] Idempotent simulated execution with a unique ledger key, and the chain query.
- [ ] Minimal Razor UI covering the four verbs, with a fixture-verification banner.
- [ ] Evidence: TC suites, credential audit, outbound-call capture.

### Add After Validation (needs ruling first)

- [ ] Claimant self-release (GAP-1). Trigger: the ratifier accepts it as part of "claim".
- [ ] Policy re-evaluation at decide time (GAP-7).
- [ ] Withdraw or cancel for action proposals (GAP-11).
- [ ] Teams notifications linking to the UI (Decision §11 allows these later).

### Future Consideration (Phase 2+)

- [ ] Real executor and credential (signed agreement, CTO/CSO sign-off).
- [ ] Execution retry semantics for transient failures (GAP-6).
- [ ] Dual approval for irreversible actions.
- [ ] Cross-tenant audit role; ledger anchoring to zt-infra.
- [ ] `link_challenge` kind.

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---|---|---|---|
| Submission pipeline + idempotency | HIGH | MEDIUM | P1 |
| Trace checks (fixture) | HIGH | MEDIUM | P1 |
| Policy before queue + policy event | HIGH | MEDIUM | P1 |
| Generic queue core + history | HIGH | MEDIUM | P1 |
| Approval rules (self-approval, decision-time role) | HIGH | MEDIUM | P1 |
| Expiry guard | HIGH | LOW | P1 |
| Idempotent simulated execution | HIGH | MEDIUM | P1 |
| Audit chain query | HIGH | LOW–MEDIUM | P1 |
| Reviewer UI (4 verbs) | HIGH | MEDIUM | P1 |
| identity_review kind (stub traffic) | MEDIUM | MEDIUM | P1 (ratified scope) |
| Conditional-update concurrency on claim/decide | HIGH | LOW | P1 (implementation detail) |
| Stub production bars (IdP, popeye) | MEDIUM | LOW | P1 |
| Claim release | MEDIUM | LOW | P2 (ruling) |
| Decide-time policy re-check | MEDIUM | MEDIUM | P2 (ruling) |
| Action-proposal withdraw | LOW–MEDIUM | LOW | P3 (ruling) |

## Competitor / Comparable Feature Analysis

| Feature | LangGraph / Temporal HITL | GitHub Environments / Azure Pipelines approvals | Banking maker-checker / KYC 4-eyes | Olive approach |
|---|---|---|---|---|
| Pending action state | A checkpoint persists the paused graph or workflow | A pending deployment job or stage | A staged change | A durable proposal and a queue item holding references only |
| Self-approval | Not built in; left to the app | "Prevent self-review" option and "approvers can't approve own runs" setting | Server-enforced, usually a 403 | Always on: approver ≠ `on_behalf_of` |
| Edit before approve | Supported (edit tool arguments) | No | Return to the maker for correction | **Forbidden** (hash binding) |
| Timeout | Configurable, resolves to reject or fail | Azure: stage skipped on timeout | Varies | 8 h default, can only be shortened, distinct `expired` error |
| Claim / unclaim | N/A | N/A (any listed reviewer approves) | Queue pick-up | Claim only; unclaim is a gap (GAP-1) |
| Pre-review automated checks | App-defined | Checks run alongside approvals | Validation before submit | Policy and trace checks run **before** queueing; refusals are never seen by a reviewer |
| Audit | App logging | Deployment history | Audit trail per change | Append-only events plus ledger; the chain can be queried from the execution record |

## Sources

- Ratified specs (HIGH): `specs/OLIVE_CHARTER_v0.4.md`, `specs/DECISION_olive-poc2-phase1_v0.5.md` (v0.5.2), `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`, `.planning/PROJECT.md`
- [GitHub Docs: Deployments and environments (required reviewers, prevent self-review, wait timer ≤ 30 days)](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments): MEDIUM–HIGH
- [GitHub Docs: Managing environments for deployment](https://docs.github.com/actions/managing-workflow-runs-and-deployments/managing-deployments/managing-environments-for-deployment)
- [Microsoft Learn: Azure Pipelines approvals and checks](https://learn.microsoft.com/azure/devops/pipelines/process/approvals) (timeout leads to the stage being skipped; self-approval setting): MEDIUM–HIGH
- [Microsoft Q&A: Azure DevOps approvals and checks](https://learn.microsoft.com/en-us/answers/questions/2277405/azure-devops-approvals-and-check)
- [Camunda 7 Tasklist: Task lifecycle (claim / unclaim)](https://docs.camunda.org/manual/latest/webapps/tasklist/task-lifecycle/): HIGH for claim and unclaim
- [Camunda forum: Claim task timeout](https://forum.camunda.io/t/claim-task-timeout/10268), [Automatic unclaim after N minutes](https://forum.camunda.org/t/automatic-unclaim-after-n-minutes/3189), [Unclaim after certain time](https://forum.camunda.io/t/unclaim-user-task-after-certain-time/24593): LOW–MEDIUM (community sources; they show no native claim lease)
- [Flowable forum: Reassign a task](https://forum.flowable.org/t/reassign-a-task/553): LOW
- [LangChain blog: Human-in-the-loop with interrupt](https://blog.langchain.com/making-it-easier-to-build-human-in-the-loop-agents-with-interrupt): MEDIUM
- [Temporal AI cookbook: Human-in-the-loop agent](https://docs.temporal.io/ai-cookbook/human-in-the-loop-python.md): MEDIUM
- [Connic docs: Approvals](https://connic.co/docs/v1/platform/approvals), [FeatBit: HITL gates without review fatigue](https://www.featbit.co/blogs/human-in-the-loop-gate-for-agents), [Gravitee: HITL approval interfaces](https://www.gravitee.io/corpus/gen-1980/human-computer-interaction/human-in-the-loop-approval-interfaces-for-ai-agent-actions.html): LOW (vendor and blog sources)
- [Wikipedia: Maker-checker](https://en.wikipedia.org/wiki/Maker-checker), [Didit: four-eyes](https://docs.didit.me/console/case-management/four-eyes), [Sumsub: 4-eyes review](https://docs.sumsub.com/docs/4-eyes-review): LOW–MEDIUM

---
*Feature research for: governed AI action broker and minimal review queue (Olive Phase 1)*
*Researched: 2026-10-21*
