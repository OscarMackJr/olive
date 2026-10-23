# Phase 5: Generic Review Queue and Action Approval - Context

**Gathered:** 2026-10-23
**Status:** Ready for planning

<domain>
## Phase Boundary

A proposal that passes policy is enqueued, in the same transaction that persists it, as an `action_approval` item on a generic, reference-only IR-1 review queue. A named, authenticated reviewer (stub identity in this phase) who is not the requester can list, claim, release and decide (approve or reject, with a rationale). The decision updates the queue item and the proposal state in one transaction. Expired proposals are withdrawn lazily, and approving one returns a distinct `proposal_expired` error. Every claim, release, decision and withdrawal is recorded in `queue_item_event`. A scripted beat 4 demo shows that an out-of-purpose proposal is refused and never appears in the queue.

Requirements: QUE-01..QUE-10, APR-01, APR-02, APR-04, APR-05, POL-08, EVD-03.

Not in this phase: execution after approval (Phase 6), the Razor UI and real Entra OIDC (Phase 7), the APR-03 decision-time role re-validation evidence (Phase 7), and the `identity_review` kind and the IR-1 producer create and withdraw endpoints (Phase 8).

</domain>

<decisions>
## Implementation Decisions

Carried forward, not re-decided: the settled rulings Q7 (only the claimant can release; no lease timers or reassignment), Q8 (only the claimant decides), Q9 (`queue_item_event` is INSERT-only under RLS, and `olive_app` gets only INSERT and SELECT on it), Q10 (action_approval decisions are `approve` / `reject`), Q11 (an expired item becomes `withdrawn`/`proposal_expired`, committed lazily, and list queries filter on `expires_at`), Q12 (action proposals can't be withdrawn) and Q13 (a server-side group→tenant map gives one tenant per session, never from the request). Phase 4 left behind the guarded `TransitionAsync` compare-and-set and the transition table in `ProposalStates`; Phase 5 adds the callers for approve, reject and expire.

### Reviewer surface
- **D-01:** Phase 5 adds an **internal reviewer JSON API** under `/v1/queue/items`: list, claim, release, decide and per-item audit history. It sits behind a **reviewer authentication scheme that is separate from agent-key auth**. The endpoints call a queue application service, which the Phase 7 Razor pages will also call. The UI must not get its own logic path.
- **D-02:** **Callers are hard-separated.** An agent key on any reviewer path gets `403 principal_not_permitted`, and so does a reviewer identity on `/v1/proposals`. Tests prove both directions, mirroring D§7.
- **D-03:** **The IR-1 producer endpoints (`POST /v1/queue/items` create and `/withdraw`) are not built in Phase 5.** They come with `identity_review` in Phase 8. action_approval items are created only internally, inside the proposal-persist transaction (QUE-03).
- **D-04:** **Conflict signalling** uses problem+json with stable reason codes, and never echoes the rationale or parameter values:
  - a lost claim race → `409 already_claimed`;
  - a non-claimant release or decide → `403 not_claimant`;
  - a conflicting decision → `409 decision_conflict`;
  - the same reviewer resubmitting the same decision → `200` with the recorded decision (QUE-08 no-op);
  - an expired proposal at claim or decide → `409 proposal_expired`;
  - a missing or cross-tenant item → `404`, which cannot be told apart.
- **D-05:** **The reviewer reads the proposal through a reviewer-path detail endpoint,** `GET /v1/queue/items/{item_id}/payload`. It uses the reviewer scheme and a tenant predicate, and returns what a reviewer needs to decide: `action_type`, `parameters`, `justification`, `on_behalf_of`, `expires_at` and `trace_ref`. Queue items themselves stay references only. The endpoint resolves `payload_ref`, and agents can never reach it. `GET /v1/proposals/{id}` stays agent-only.

### Dev reviewer identity and Q14
- **D-06: Q14 ruling.** On the wire, `on_behalf_of` stays an opaque UPN-style string (NONO §5 and the current fixtures). A new read-only port, **`IUserIdentityResolver`**, resolves `(tenant_id, on_behalf_of)` to **exactly one** Entra `(oid, tid)`. It is marked `[OutboundPort]`, backed by fixtures, and `INonProductionOnly`. The self-approval check (APR-02) compares the resolved oid+tid with the reviewer's oid+tid. If the resolver finds zero matches, finds several, or is unavailable, the approval is **refused** (fail closed, with a stable reason such as `approval_identity_unresolved`). UPN strings are never compared directly. Record this ruling and close the Q14 coordination item in STATE.md. The live adapter is still deferred.
- **D-07:** **The stub reviewer identity** comes from a **DevReviewer authentication scheme**. It is `INonProductionOnly` and barred in production by the existing startup guards (Q17). It reads a reviewer id from a request header, looks it up in a **source-controlled reviewer fixture** (oid, tid, groups), and emits claims that carry `auth_source = stub`. Phase 7 replaces it with Entra OIDC, which emits the same claims. Decisions record the auth source (APR-05).
- **D-08:** **The Q13 group/app-role → tenant map** is a source-controlled YAML file (for example under `config/`). It is strict-parsed like `config/agents.yaml` and validated at startup. A session that maps to zero tenants or to several gets 403. The tenant never comes from a header, form or query string. Phase 7 uses the same file for Entra groups.
- **D-09:** **The role gate is wired now.** Each kind in the kind registry (QUE-02) declares a `required_role`. At **decide time**, Phase 5 checks the reviewer's groups against it, using the code path Phase 7 will keep. APR-03 in Phase 7 then only swaps the identity source and adds the `max_age`/cookie re-validation and its evidence.

### IR-1 mapping for action_approval
- **D-10:** **References.** `subject_ref` and `payload_ref` are both `olive:proposal/<proposal_id>`, `producer = olive` and `producer_ref = proposal_id`. A database CHECK and the kind registry allow only the reference schemes registered for each kind (`olive:` for action_approval; `bluto:` is registered with identity_review in Phase 8, or stubbed in the registry now, at the planner's choice). An inline payload is rejected (QUE-01, TC-QUE-01).
- **D-11:** **The lazy expiry commit point is claim and decide.** When `expires_at` has passed, claim or decide commits proposal → `expired`, item → `withdrawn` with reason `proposal_expired`, and the matching `proposal_event` and `queue_item_event` rows, all in one transaction. **Only then** does it return `409 proposal_expired` (POL-08). List only filters on `expires_at` and never writes. There is no sweep in Phase 5.
- **D-12:** **Expiry wins over a claim.** A claimed item whose proposal expires follows the same path: the claimant's decide gets `proposal_expired`, and the claim stays in the event history.
- **D-13:** **The contract becomes IR-1 v1.** Add `contracts/queue-item/v1/` with the IR-1 item shape: `additionalProperties: false`, reference-only patterns, and kind-specific fields such as `ruleset_version` optional. Add paired valid and invalid examples, including an invalid one with an inline payload. Add the source-controlled kind registry (a YAML file, strict-parsed, with no runtime registration). **Delete `contracts/queue-item/v0/`**, the placeholder Phase 2 D-15 labelled. Update the CLAUDE.md contract-path references if they mention v0.
- **D-14:** **The list response** carries only IR-1 item fields: refs, kind, subtype, state, claimed_by, created_at, age, and `expires_at` for action items. It is filterable by `kind` and `state`, ordered oldest first, and tenant-predicated on top of RLS. It carries no proposal parameters, justification or on_behalf_of; the reviewer follows `payload_ref` (D-05).
- **D-15:** An approve or reject updates the queue item (`decided`, `decision`, `decided_by`, `decided_at`, `rationale`, `auth_source`) **and** moves the proposal from `awaiting_approval` to `approved` or `rejected`, with events, in **one tenant transaction** (APR-04), using the guarded compare-and-set.

### Beat 4 demo (EVD-03)
- **D-16:** **The demo is an xUnit integration test** tagged `[Trait("Demo","beat4")]`. It runs over HTTP against Testcontainers Postgres and writes a markdown transcript (to a gitignored or TestResults path). A one-line runner script under `build/` runs only that trait. It runs in CI.
- **D-17:** **"Refusal traceable"** means the transcript and assertions show:
  - the 201 submit response with `refused_policy` / `purpose_not_permitted`;
  - the stored proposal row (state, refusal_reason);
  - its `proposal_event` rows, including `policy_evaluated` with its checks;
  - a `queue_item` count of 0 for that proposal;
  - the reviewer list (dev identity) not containing it.

  The transcript contains no parameter values.

### Claude's Discretion
- Exact route shapes (for example `POST /v1/queue/items/{id}/claim|release|decision`, `GET /v1/queue/items/{id}/events`) and DTO names in `Olive.Contracts`.
- The header name for the DevReviewer scheme, and the reviewer fixture and tenant-map file paths and schemas. Each fixture gets a schema with valid and invalid examples, following `contracts/directory/`.
- Table design for `queue_item` and `queue_item_event`. Use the next forward-only migration numbers after `0007_policy_expiry_rate.sql`, follow the Phase 1 RLS and grant pattern (ENABLE + FORCE RLS, `current_setting('olive.tenant_id')` without missing_ok, column-level grants, state kept out of the INSERT grant where CAS applies), and extend `PrivilegeMatrixTests`, `RlsPatternTests`, `MigrationRunnerTests` and the tenant-isolation ablation tests to the new tables. Decide whether the item has a unique constraint on `(tenant_id, producer, producer_ref)` now; recommended yes, since it also backs IR-1 idempotency in Phase 8.
- Stable reason-code names beyond those listed in D-04 and D-06.
- Whether the concurrent-claim proof uses N parallel HTTP calls or store-level calls. Exactly one winner must be asserted either way.
- Where the queue service and kind registry live, within the FND-01 inward dependency graph (for example, registry parsing in `Olive.Catalog`, the service in `Olive.Application`, the store in `Olive.Persistence`).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Queue model and contracts
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`: the IR-1 item shape (§2), decisions (§3), create and withdraw rules (§4, for Phase 8 but shaping the generic table) and the decision-polling cursor (§6). Ratified 2026-10-21.
- `contracts/queue-item/v0/`: the placeholder schema to be replaced (D-13).
- `specs/OLIVE_CHARTER_v0.4.md` §4 (Review Queue, REQ-QUE-01..03, TC-QUE-01..04) and §3 (lifecycle, REQ-ACT-05 expiry).

### Ratified decision record
- `specs/DECISION_olive-poc2-phase1_v0.5.md`: §7 (introspection and separate caller paths), §8 (approvers), §9 (expiry), §10 (persistence), §11 (queue frontend), §13 (Postgres roles, RLS and migrations; amended to five tables by Q9).

### Requirements and rulings
- `.planning/REQUIREMENTS.md`: the rulings Q7–Q14 and Q17 and the requirements QUE-01..10, APR-01..05, POL-08 and EVD-03.
- `.planning/ROADMAP.md`: the Phase 5 success criteria and notes.
- `.planning/STATE.md`: the open coordination items. Close Q14 with D-06.

### Prior phase context
- `.planning/phases/04-policy-expiry-and-refusal/04-CONTEXT.md`: the transition table and guarded CAS, `expires_at` semantics (D-13..D-16), and the `policy_evaluated` payload (D-20).
- `.planning/phases/03-trace-verification/03-CONTEXT.md`: the stored-refusal path, problem shapes, the `[OutboundPort]` and `INonProductionOnly` patterns, and the fixture conventions.
- `.planning/phases/02-contract-integrity-and-idempotent-submission/02-CONTEXT.md`: contract layout (D-15), problem+json conventions and JCS for idempotency.

### Codebase maps
- `.planning/codebase/ARCHITECTURE.md`, `.planning/codebase/CONCERNS.md`: the scaffold queue defects (no approval state guard, duplicate enqueue on resubmit). Check them against the current code.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/Olive.Domain/ProposalStates.cs`: the full transition table already includes `awaiting_approval → approved | rejected | expired`.
- `src/Olive.Persistence/PostgresProposalStore.cs`, `TenantUnitOfWork.cs`, `ProposalSql.cs`: the tenant transaction, the guarded `TransitionAsync` CAS and the event inserts. The enqueue must join the existing accept transaction (QUE-03).
- `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs`, `OliveClaims.cs`: the auth-handler pattern to mirror for the DevReviewer scheme.
- `src/Olive.Host/Startup/StartupGuards.cs` and `src/Olive.Application/INonProductionOnly.cs`: the production bar for stubs (Q17).
- `src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs` and `contracts/directory/`: the fixture-port pattern for `IUserIdentityResolver` and the reviewer fixtures.
- `src/Olive.Catalog/AgentAllowlist.cs` and `config/agents.yaml`: strict YAML parsing for the kind registry and the tenant map.
- `src/Olive.Host/Problems/ProblemResults.cs`, `src/Olive.Contracts/ProblemReasons.cs`: stable problem+json reason codes.

### Established Patterns
- Forward-only, checksum-pinned migrations in `sql/migrations/` (next is 0008). Table-driven RLS and privilege tests (`PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`).
- Ports in `Olive.Application` with `[OutboundPort]`, stubs in `Olive.Integrations.*`, and faulting test doubles in the test project only (`FaultingDoubles.cs`, `RecordingPorts.cs`).
- Time comes from the injected `TimeProvider`; tests use `ManualTimeProvider`, which drives the expiry cases.
- `OliveApiFactory` over HTTP with Testcontainers `PostgresFixture`. Test names cite requirement IDs.

### Integration Points
- The proposal accept path (the `awaiting_approval` CAS) is where the action_approval item insert goes, in the same transaction.
- The Host endpoint registration (`src/Olive.Host/Endpoints/`) gains a `QueueEndpoints.cs` under the reviewer scheme.
- The startup guards and production bar extend to the DevReviewer scheme, `IUserIdentityResolver` and the reviewer fixture.

</code_context>

<specifics>
## Specific Ideas

- The reviewer path must keep REQ-QUE-01 airtight: refs only in items and lists. Proposal content is shown only through the reviewer-scoped payload endpoint.
- Phase 7 should be mostly a swap: the same claims, the same app service and the same role check, with only the identity source and the re-validation changing.

</specifics>

<deferred>
## Deferred Ideas

- A sweep at startup or on a timer for expired items. Not chosen; withdrawal is lazy at claim and decide (D-11).
- A live `IUserIdentityResolver` (Entra/Graph) adapter. It goes with the live integration work after Phase 7.
- The IR-1 producer create and withdraw endpoints and the `identity_review` kind are Phase 8.

</deferred>

---

*Phase: 05-generic-review-queue-and-action-approval*
*Context gathered: 2026-10-23*
