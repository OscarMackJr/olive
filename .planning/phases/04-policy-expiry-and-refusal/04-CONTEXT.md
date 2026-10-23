# Phase 4: Policy, Expiry and Refusal - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning

<domain>
## Phase Boundary

Policy decides before any human sees a proposal. Proposals that are unknown-action, out-of-purpose, unentitled or rate-limited are stored as `refused_policy` with a reason and are never eligible for queueing. Every policy evaluation appends a `policy_evaluated` event. Every lifecycle transition is a compare-and-set paired with a `proposal_event` row, and illegal transitions are rejected. `expires_at` is computed once and stored as an absolute time. A test asserts the full submission order (introspect → principal → idempotency → schema/catalog → trace → policy → persist), and the first failure wins.

Requirements: POL-01..POL-07 and API-08. Out of this phase: queueing itself (Phase 5), expiry enforcement on approve/execute (POL-08, Phase 5), and execution (Phase 6).

</domain>

<decisions>
## Implementation Decisions

Carried forward, not re-decided: Phase 3 D-01..D-05 (a new refusal returns 201 with `{proposal_id, state, refusal_reason}`, a replay returns 200 with no re-evaluation, and the stored-refusal path is insert then CAS plus events in one tenant transaction). These satisfy POL-05 directly. Q3 also carries forward: an `expires_in_seconds` that is ≤0 or above the maximum gets 400 and is never clamped, and an expired key gets 401.

### Entitlement source (Q4 / POL-02)
- **D-01: Q4 ruling. Proceed; do not stop under D§4.** POPEYE_v0.4.2 §2.5 and §5 assign resolving `on_behalf_of` to directory groups to Olive ("the calling service"), and vocabulary §3.2 defines the intersection rule. Together they define the entitlement source. Record this reasoning in the phase artifacts, and close the STATE.md "Q4 narrowed" coordination item with it.
- **D-02:** Add a new read-only port, `IDirectoryGroupResolver`, in `Olive.Application`, marked `[OutboundPort]`. It takes `(tenant_id, on_behalf_of)` and returns the user's directory groups, or a typed outcome for an unknown user or unavailability. Implement it as a fixture-backed stub, `INonProductionOnly` and barred in production by the existing startup guards. There is no live Entra/Graph call and no credential, so REQ-ACT-04 holds.
- **D-03: Entitlement model.** Add an optional `required_entitlement` (a group-style name, e.g. `ent:crm.case_note.write`) to each catalog entry. Authorization passes only if the user's directory groups contain it **and** the agent's own entitlements contain it. That is the intersection; it never falls back to a union. Give `crm.case_note@1` a value. The catalog v1 schema change is additive.
- **D-04: Agent entitlements** come from the source-controlled agent allowlist YAML: each agent entry (`liam`) gains an `entitlements:` list. Parsing stays strict, and there is no runtime registration.
- **D-05: Fail closed, split the way trace verification does.** An unknown user, empty groups or a missing required entitlement is a stored refusal, `entitlement_missing`. Resolver unavailability or a timeout returns 503 with nothing stored, `retryable: true` and `Retry-After`, mirroring Phase 3 D-06/D-09 (one public reason code, with the cause kept in logs only). A rejection of Olive's own credential or registration raises an `IOperationalAlarm` alarm with its own stable code. If an action has no `required_entitlement`, decide in planning whether that fails closed. Default: entries must declare one (null grants must not fail open, per CONCERNS.md).
- **D-06:** `on_behalf_of` is passed to the port verbatim, as an opaque string. Fixtures key on the existing UPN-style placeholders. The Q14 (UPN vs oid+tid) ruling affects only the future live adapter and stays open.
- **D-07:** Do not cache directory lookups in Phase 4.

### Rate limit (POL-03)
- **D-08:** Use a sliding window counted from proposal rows. Under `pg_advisory_xact_lock` keyed on a hash of `(tenant_id, agent_id, action_id)`, count proposals with `created_at > now − window_seconds`. There is no counter table. Add a supporting index on `(tenant_id, agent_id, action_id, created_at)`. Limits come from the catalog's `max_rate` (`limit`, `window_seconds`).
- **D-09:** A proposal counts as accepted if it is in any state except `refused_policy`: awaiting_approval, approved, rejected, executed, execution_failed or expired. Refusals never consume the limit, which fixes the CONCERNS.md defect.
- **D-10:** The scope is per tenant, `(tenant_id, agent_id, action_id)`, and the count runs inside the RLS tenant transaction.
- **D-11:** A rate-limited proposal is a stored `refused_policy` with reason `rate_limited`, returning 201, and a replay returns the same refusal. It is not a 429. Liam must use a new `run_id` to retry.
- **D-12:** Rate is the last policy check. The lock is taken only when purpose and entitlement have passed, and it is held through the accept CAS in the same transaction, so concurrent submissions cannot overshoot. A concurrency test proves that N parallel submissions above the limit accept exactly `limit`.

### Expiry (POL-07)
- **D-13:** Add a new **required** catalog field, `max_expiry_seconds`, validated at startup (>0). `crm.case_note@1` gets `28800`. The catalog v1 schema change is additive.
- **D-14:** `expires_at = now + min(expires_in_seconds ?? 8h, max_expiry_seconds)`, then capped at the key's `expires_at`. If `expires_in_seconds` is omitted, the default is `min(8h, max_expiry_seconds)` and it never errors. `now` comes from the injected `TimeProvider`.
- **D-15:** The cap at the key's `expires_at` is applied silently: no 400, because the agent doesn't control key lifetime. Q3's 400 applies only when `expires_in_seconds` is ≤0 or greater than `max_expiry_seconds`.
- **D-16:** `expires_in_seconds` is an additive optional integer (minimum 1) in `contracts/proposal/v1`, with valid and invalid examples. It is part of the JCS body, so it feeds `body_sha256` and idempotency. It is **not** part of the four-key `proposal_hash` (Phase 2 D-08), so hometown's hash agreement is unaffected. `GET /v1/proposals/{id}` exposes `expires_at`.
- The 400 for an out-of-range `expires_in_seconds` happens at the schema/catalog step, because it needs the action's maximum. Nothing is stored.

### Refusal reasons and events (POL-01, POL-04)
- **D-17:** The policy reason codes, in evaluation order, are `not_in_catalog`, `purpose_not_permitted`, `entitlement_missing` and `rate_limited`. They are snake_case and stored in `refusal_reason` alongside the `trace_*` codes, so extend the check constraint in a new forward-only migration. Drop the legacy scaffold rule IDs (PURPOSE-001 and the rest).
- **D-18:** The first failure wins, and `refusal_reason` holds a single code. Later checks don't run, so the rate lock is never taken for a proposal that is already refused.
- **D-19:** `not_in_catalog` is stored at the **schema/catalog step** (API-08 step 4). An unknown `action_type` that passes the envelope schema is immediately stored as `refused_policy` / `not_in_catalog`, with no trace resolution and no further policy, and it still gets a `policy_evaluated` event. This fixes the CONCERNS.md defect where an unknown action throws.
- **D-20:** The `policy_evaluated` event payload is `{outcome: "allowed"|"refused", reason, checks: [{name, result}]}`. Check names are static (`catalog`, `purpose`, `entitlement`, `rate`), and checks that didn't run are omitted or marked as not evaluated. The payload never contains parameter values or group lists. It is emitted for allowed proposals too.

### Claude's Discretion
The planner may refine these without asking:
- **Lifecycle guards (POL-06):** put the full transition table in `Olive.Domain` (extending `ProposalStates`), and make every transition a single `UPDATE … WHERE state = @expected` with a row-count check plus a `proposal_event` insert in the same transaction. Unit-test the illegal-transition matrix completely now. End to end, only `proposed → refused_policy | awaiting_approval` is exercised, with the later edges wired in Phases 5–6. This also covers the "no approval guard" defect from CONCERNS.md at the domain level.
- **The API-08 order test:** use a request crafted with several faults (for example an inactive key plus a bad schema plus an unknown trace plus a wrong purpose) and peel the faults off one at a time, asserting the outcome of the first failing step each time. Recording decorators or a step trace may also be used.
- Port placement, the stub fixture location (e.g. `contracts/directory/` with a README and schema), and the alarm and reason code names for the directory resolver.
- The migration number continues the sequence after `0006_trace_verification.sql`. Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` to cover any new columns or grants.
- Whether the policy engine is a pure domain evaluator, taking (catalog entry, principal, groups, rate count) and returning a verdict, with the DB lock and count orchestrated in the store. Recommended: yes.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Governing decision and requirements
- `specs/DECISION_olive-poc2-phase1_v0.5.md`: §4 stop rule, §7 principals and `on_behalf_of` (entitlement intersection), §13 roles and grants, §15.2 order of operations (step 6 policy: purpose, entitlements, rate)
- `specs/OLIVE_CHARTER_v0.4.md`: §3 lifecycle, REQ-ACT-02 (refusal before queue), REQ-ACT-04 (no write credential)
- `.planning/REQUIREMENTS.md`: POL-01..POL-07, API-08, working defaults Q3/Q4/Q14
- `.planning/ROADMAP.md`: Phase 4 success criteria and notes (the CONCERNS.md defects to fix)
- `.planning/codebase/CONCERNS.md`: an unknown action throwing, null grants failing open, refusals consuming the rate limit, no approval guard

### Upstream specs (outside repo)
- `~/fiskroad/specs/POPEYE_v0.4.2.md` §2.5 and §5: introspection carries no entitlements, and Olive resolves `on_behalf_of` to directory groups (basis for D-01)
- `~/fiskroad/specs/PROGRAM_SHARED_VOCABULARY_v0.4.md` §3.2 (intersection, never union) and §4 (lifecycle outcomes, `refused_policy` vs `rejected`)
- `contracts/vendored/vocabulary.yaml`: purposes and outcomes (never edit)

### Prior phase context
- `.planning/phases/03-trace-verification/03-CONTEXT.md`: D-01..D-05 refusal contract and stored-refusal path, D-06..D-09 fail-closed signalling and alarms, D-16 faulting test doubles
- `.planning/phases/02-contract-integrity-and-idempotent-submission/02-CONTEXT.md`: D-08 four-key `proposal_hash`, idempotency and replay
- `.planning/phases/01-foundation-and-walking-skeleton/01-CONTEXT.md`: RLS/grant pattern, privilege-matrix tests

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `IProposalStore.InsertRefusedAsync` / `PostgresProposalStore` (Phase 3): the stored-refusal path to reuse for the policy refusals and `not_in_catalog`.
- `TraceRefusalReasons` (`src/Olive.Domain/TraceRefusalReasons.cs`) and `ProposalStates`: the pattern for a new `PolicyRefusalReasons` and the transition table.
- `IOperationalAlarm`, `INonProductionOnly`, `[OutboundPort]`, and the startup guards in `src/Olive.Host/Startup/StartupGuards.cs`: reuse them for the directory resolver.
- `AgentAllowlist` (`src/Olive.Catalog/AgentAllowlist.cs`) is a strict YAML parse and gains `entitlements`. `CatalogLoader` gains `required_entitlement` and `max_expiry_seconds`.
- `TenantUnitOfWork`: the transaction for the advisory lock, count, CAS and events.
- `ProblemReasons` / `ProblemResults`: add the directory-unavailable 503.
- The test harness `OliveApiFactory` (auto-fixture mode, faulting doubles), and the hand-written `ManualTimeProvider` for expiry tests.

### Established Patterns
- Ports live in `Olive.Application`, stubs in `Olive.Integrations.*`, and the Host wires DI.
- Expected outcomes use typed result hierarchies rather than exceptions.
- Logging is value-free. Policy detail text is static and never contains parameter values.
- SQL migrations are forward-only and checksum-pinned, with column-level grants. `state` stays out of the INSERT grant.
- Test names carry requirement IDs (`POL_0x_...`, `API_08_...`).

### Integration Points
- `src/Olive.Host/Endpoints/ProposalEndpoints.cs` `PostAsync`: at the schema/catalog step, add `not_in_catalog` storage and the `expires_in_seconds` range check. After trace verification, add the policy step (purpose → entitlement → rate) and then persist and accept.
- `NewProposal` / `ProposalSql`: add `expires_at` computation, the accept CAS to `awaiting_approval`, the `policy_evaluated` event, and the rate count query.
- The GET proposal endpoint exposes `expires_at`.
- `catalog/crm.case_note@1.yaml`, `contracts/catalog/v1` schema, `contracts/proposal/v1` schema and examples, and the agent allowlist YAML.

</code_context>

<specifics>
## Specific Ideas

- Beat 4 (Phase 5 demo) depends on `purpose_not_permitted` producing a stored refusal that never appears in the queue. Keep that reason string stable.
- `expires_in_seconds` deliberately stays out of `proposal_hash`, so there is no hometown coordination for this phase.

</specifics>

<deferred>
## Deferred Ideas

- Directory lookup caching and the live Entra/Graph adapter go with the live integration work, after the Q14 ruling.
- The Q14 UPN vs oid+tid ruling must happen before Phase 5 and Phase 7. It is unchanged here.

</deferred>

---

*Phase: 04-policy-expiry-and-refusal*
*Context gathered: 2026-10-22*
