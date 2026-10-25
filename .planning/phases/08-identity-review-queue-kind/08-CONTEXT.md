# Phase 8: Identity Review Queue Kind - Context

**Gathered:** 2026-10-24
**Status:** Ready for planning

<domain>
## Phase Boundary

The shared review queue gets a second producer. Stub bluto, calling with a stubbed popeye service token, can:

- create `identity_review` / `candidate_pair` items under the IR-1 create-body rules;
- withdraw its own undecided items;
- poll decided items back through IR-1 §6.

Reviewers who hold `role:identity_steward` can accept, reject or defer those items through the existing reviewer JSON API and the Phase 7 Razor pages. Every decision, including a defer, is recorded in the item history.

Agent and user credentials are refused on the service-caller path, and service credentials are refused on `/v1/proposals` (IDN-03). No live bluto traffic is enabled. popeye's bluto registry entry and the named steward group stay external gates (IR-1 §8).

Requirements: IRQ-01, IRQ-02, IRQ-03, IRQ-04, IDN-03.

Not in this phase:
- `link_challenge` is reserved and stays rejected (TC-IRQ-11).
- IR-1 §7 open items.
- Resolving bluto evidence.
- Live popeye tokens.

</domain>

<decisions>
## Implementation Decisions

Carried forward, not re-decided:
- **IR-1 v0.1 (ratified 2026-10-21) fixes the wire contract.** That covers the routes, the six-field create body, `400 server_assigned_field` (naming no value), `400 invalid_request` for unknown fields, idempotency on `(producer, tenant_id, producer_ref)` over the RFC 8785 canonical body (`200` replay / `409 idempotency_conflict`), the withdraw reason codes `superseded_by_rule | source_changed | ruleset_retired`, no expiry, and no policy check before queueing.
- **Phase 5 rulings.** Only the claimant can release (Q7) or decide (Q8). Callers are hard-separated (D-02). The tenant comes only from the server-side map (D-08). The role is checked at decide time from the kind registry's `required_role` (D-09). Problem+json reason codes never echo values (D-04). Missing and cross-tenant items get indistinguishable 404s.
- **Phase 7 rulings.** The UI stays minimal: no dashboards, bulk actions, SLA views or assignment rules. Antiforgery and PRG on every POST. Flash messages carry static codes only.
- **Any change to IR-1 needs both owners' agreement and re-ratification.** Nothing here changes IR-1 except as flagged in D-11.

### Shared `/v1/queue/items` path
- **D-01: Dispatch by credential and keep IR-1's paths exactly.**
  - A policy or forwarding authentication scheme on `/v1/queue/items` chooses the handler from the credential presented.
  - A service Bearer token reaches the producer endpoints: `POST /v1/queue/items`, `POST /v1/queue/items/{id}/withdraw`, and the producer `GET /v1/queue/items` poll.
  - A reviewer credential (DevReviewer header, or the reviewer cookie where Phase 7 admits it) reaches the existing reviewer endpoints.
  - Create and withdraw are service-only. On `GET /v1/queue/items` the credential decides which view is returned.
  - No reviewer route is moved, so the Phase 5 and 7 reviewer contracts and tests stay valid.
- **D-02: The producer GET is narrow.**
  - It accepts only `kind=identity_review&state=decided` plus an optional `decided_after`. Any other filter combination gets `400 invalid_request`.
  - It returns only items where `producer` equals the caller's producer, in the token's tenant: an application predicate on top of forced RLS.
- **D-03: Reviewers see one shared queue.**
  - The reviewer list (JSON API and the `/review` page) shows both kinds together, oldest first, with a kind badge.
  - The existing `kind` and `state` filters keep working.
  - Items whose `required_role` the reviewer lacks are still listed; the role gates deciding and the payload view, not visibility.
- **D-04: Extend the Phase 7 `CredentialRouteMatrixTests`.**
  - Add a service-token row, and columns for the producer routes: create, withdraw and poll.
  - Only the intended pairings are admitted. This is IDN-03's evidence.
  - The rows must show: the service token refused on `/v1/proposals`, `/review`, `/dev` and every reviewer action; agent keys refused on producer routes; reviewer and user credentials refused on create and withdraw.

### Service-token authentication and producer registry
- **D-05: A new inbound verifier port checks the service token.**
  - bluto sends `Authorization: Bearer <token>`.
  - A new port `IServiceTokenVerifier` returns `{active, principal_type=application, client_id, tenant_id, exp}`, with no `delegated_agent_id`. A token that carries a delegated agent is refused.
  - The stub is fixture-backed (same pattern as `StubKeyIntrospector`), marked `INonProductionOnly` and barred in production by FND-05.
  - It is kept separate from the agent-key `IKeyIntrospector`, so session keys and service tokens never share a code path.
  - When the verifier is unavailable or returns malformed data, the request fails closed with 503 (the Phase 3 alarm pattern).
  - The token is never logged or stored, and the IDN-08 sentinel scans cover it.
- **D-06: Producers come from a source-controlled `config/producers.yaml`.**
  - It maps a token `client_id` to a `producer` (`bluto`) and that producer's allowed `kinds` and `subtypes` (`identity_review` / `candidate_pair`).
  - It is strict-parsed and validated at startup against `config/queue-kinds.yaml`: a kind or subtype that isn't registered fails startup, and so does a ref scheme that doesn't match.
  - Like `config/agents.yaml`, it cannot be overridden by environment variable or appsettings.
  - An unknown `client_id` gets 403. The producer is never taken from the body.
- **D-07: The wrong caller type gets `403 principal_not_permitted`.**
  - This applies to an agent key, a user credential or a reviewer credential on a producer-only route, and to a service token on `/v1/proposals`.
  - It reuses the Phase 5 D-02 reason code. 401 is kept for missing, inactive or invalid credentials.
- **D-08: An unknown tenant fails closed.**
  - A token that verifies but whose `tenant_id` is not a known vocabulary tenant UUID gets 403 with a static reason code, for example `tenant_not_permitted` (name at Claude's discretion).
  - No item is created and nothing is echoed.

### Identity items for reviewers
- **D-09: A defer clears the claim and returns the item to `open`.**
  - It works like a release that also records a decision. `state=open`, and `claimed_by` is cleared.
  - A `deferred` event is written with `decided_by` and `rationale`, but none of the item's `decision`, `decided_by`, `decided_at` or `rationale` columns are set. The `queue_item_state_shape` CHECK requires them to be null while the item is open.
  - Any steward in the tenant can claim it next. No soft hold, consistent with Q7.
  - `accept` and `reject` move the item to `decided`.
  - identity_review decisions change no proposal, because there isn't one.
- **D-10: The item page for an identity item shows references only.**
  - Kind, subtype, `subject_ref`, `payload_ref`, `producer_ref`, `ruleset_version`, age, claim state and history, all as encoded monospace text.
  - A static note says the evidence comes from bluto's evidence endpoint under the reviewer's own credential.
  - No clickable link, no absolute URL, and no outbound call, so CSP and `UiSurfaceTests` stay unchanged.
  - Olive never resolves `bluto:review-evidence/...` (IR-1 §5).
- **D-11: The decision form is driven by the kind.**
  - The decision buttons come from the kind registry: `accept / reject / defer` for identity_review, `approve / reject` for action_approval.
  - For identity items the rationale help text reads, in substance: "Do not include names, dates, identifiers or other record values." IR-1 §2 requires the UI to say this.
  - A rationale is required for all three decisions, including defer.
  - Self-approval (D-06/D-18 of Phase 5) and post-approval execution apply only to action_approval.
- **D-12: Add steward fixtures.**
  - New fixtures in `contracts/reviewer/`, for example `alpha-steward-1`, `alpha-steward-2`, `beta-steward`, plus a supervisor-only identity without steward, so TC-IRQ-04 can prove the refusal.
  - Add `identity_steward` to the `EntraClaimsMapper` role allowlist. The allowlist is registry-derived, so check whether it already appears.
  - The Entra group stays unnamed, so live decisions remain gated (IR-1 §8).

### Polling and withdraw
- **D-13: The cursor is an opaque `decided_after` token.**
  - `decided_after` carries an opaque base64url string that encodes `(decided_at, item_id)`.
  - Results are ordered `(decided_at, item_id)` ascending, so ties are deterministic.
  - The first poll omits `decided_after`. Each response returns a `next_cursor`.
  - A malformed cursor gets `400 invalid_request`.
  - This fits IR-1 §6's single parameter. **Flag for planning:** IR-1 doesn't specify the response envelope, so `next_cursor` should be named as an additive response field when this is shared with the bluto owner.
- **D-14: Each poll item carries the decision fields but no rationale.**
  - Fields: `item_id`, `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref`, `ruleset_version`, `state`, `decision`, `decided_by`, `decided_at`.
  - bluto needs `decided_by` for `asserted_by` (IR-1 §3).
  - The rationale stays in Olive's audit only (least disclosure).
- **D-15: The poll returns only accept and reject decisions.** Only items in `state=decided` are returned. Defer events stay in Olive's history, because bluto does nothing on a defer (IR-1 §3).
- **D-16: The page size is fixed at 100.** It matches `QueueService.DefaultLimit`, and there is no client-chosen limit. bluto keeps following `next_cursor` until it gets a short page.
- **D-17: bluto can withdraw any undecided item, including a claimed one.**
  - Withdraw clears any claim, sets `state=withdrawn` with the reason code, and writes a `queue_item_event`, all in one tenant transaction.
  - The claimant's next decide or release gets 409 with a static code, for example `item_withdrawn`.
  - Withdrawing a decided item gets 409 (TC-IRQ-09).
  - A missing item, another tenant's item or another producer's item gets an indistinguishable 404.
  - An unknown reason code gets `400 invalid_request`.
- **D-18: Ship a scripted demo, run in CI.**
  - It is an xUnit integration test tagged `[Trait("Demo", ...)]`, over HTTP against Testcontainers Postgres, with a one-line runner script under `build/` (same pattern as Phase 5 D-16 and the Beat 2 demo).
  - Stub bluto creates an item, gets an idempotent replay, and gets `server_assigned_field` for `tenant_id`. A steward claims it, defers it, re-claims it and accepts it. bluto polls the decision with the cursor and withdraws a second item.
  - It writes a markdown transcript containing no record values or rationale text.

### Claude's Discretion
- How the dispatch mechanism is built: a forwarding policy scheme, endpoint filters, or separate route handlers on the same pattern. It must keep the Phase 7 rule that schemes are named only per route group, and D-01 must hold.
- The `IServiceTokenVerifier` fixture format and location (for example `contracts/service-token/` with a schema and valid/invalid examples, following `contracts/introspection/`), and the `producers.yaml` schema.
- Reason-code names not fixed by IR-1 or Phase 5: `tenant_not_permitted`, `item_withdrawn`, unknown producer, and token-with-delegated-agent.
- Storage for create idempotency. `queue_item` already has `producer`, `producer_ref`, `ruleset_version`, the bluto ref CHECK and `unique (tenant_id, producer, producer_ref)` (migration `0008_review_queue.sql`). It has no `body_sha256`, so expect a forward-only `0010_*` migration (column, grants, maybe the `deferred` event type) following the Phase 1 RLS and grant pattern.
- How the `deferred` event is represented in `queue_item_event`, and how a defer appears on the audit page.
- What the reviewer `GET /v1/queue/items/{id}/payload` returns for an identity item, for example refs only, or 404 or `no_payload`. It must never resolve bluto evidence.
- Whether the same reviewer may re-claim right after their own defer. Default to allowed, since Q7 has no cool-down.
- Whether the poll cursor is HMAC-protected or plain base64url. Opaque is required; integrity protection is optional because it only encodes public ordering keys.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### IR-1 contract (authoritative wire shape)
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`: the whole contract. §2 item shape, §3 decisions, §4 create, withdraw and idempotency, §5 evidence (never resolved by Olive), §6 polling, §7 open items (out of scope), §8 gates, §9 TC-IRQ-01..11 (test list), and the change-control rule.

### Queue foundation (Phase 5)
- `.planning/phases/05-generic-review-queue-and-action-approval/05-CONTEXT.md`: D-01..D-21. The reviewer API, hard caller separation, reason codes, D-03 deferring the producer endpoints to Phase 8, ref schemes, and the list shape.
- `config/queue-kinds.yaml`: the identity_review kind is already registered (decisions, `required_role: role:identity_steward`, `ref_schemes: [bluto]`, `expires: false`).
- `contracts/queue-item/v1/`: the IR-1 item JSON Schema with valid and invalid examples. Extend it for create-body, poll and withdraw shapes as needed, keeping the valid/invalid pairs.
- `sql/migrations/0008_review_queue.sql`: the existing queue tables, CHECKs, unique key and grants.

### Reviewer UI and auth (Phase 7)
- `.planning/phases/07-reviewer-ui-and-entra-oidc/07-CONTEXT.md`: route-group isolation, the minimal UI, flash and PRG rules.
- `.planning/phases/07-reviewer-ui-and-entra-oidc/07-UI-SPEC.md`: the list, item and audit page contract that identity items extend.
- `.planning/phases/07-reviewer-ui-and-entra-oidc/07-REVIEW.md`: the open review findings (WR-02..07) that touch the same files.
- `tests/Olive.Tests/CredentialRouteMatrixTests.cs`: the matrix D-04 extends.

### Identity and popeye stubs (Phase 1 and 3)
- `contracts/introspection/` and `src/Olive.Integrations.Popeye/StubKeyIntrospector.cs`: the fixture-stub pattern for the new verifier.
- `config/agents.yaml`, `src/Olive.Catalog/AgentAllowlist.cs`, `src/Olive.Catalog/QueueKindRegistry.cs`: the strict YAML registry pattern for `producers.yaml`.

### Project-level
- `.planning/REQUIREMENTS.md`: IRQ-01..04 and IDN-03.
- `.planning/PROJECT.md`: core value, constraints (IR-1 change control, fixtures barred in production, references only).
- `specs/DECISION_olive-poc2-phase1_v0.5.md` §10–§13 and §15: persistence, isolation, idempotency and canonical form, which IR-1 cites.
- `specs/OLIVE_CHARTER_v0.4.md` §4: the shared queue and REQ-QUE-01..04.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `QueueService` (`src/Olive.Application/QueueService.cs`): list, claim, release, decide, with tenant predicates and role checks. Extend it with create-by-producer, withdraw, poll and the defer transition.
- `IQueueKindRegistry` / `QueueKindRegistry`: per-kind decisions, `required_role`, ref schemes and expiry. These drive the decision form and validation.
- `PostgresQueueStore` and `QueueSql` (`src/Olive.Persistence/`): the guarded compare-and-set transitions and the INSERT-only `queue_item_event`.
- The JCS canonical hashing used for proposal idempotency (Phase 2, `Jcs`): reuse it for the `body_sha256` of the create body.
- `StubKeyIntrospector`, plus the `contracts/introspection/` fixtures: the template for `IServiceTokenVerifier`.
- `ApprovalExecution.RunAsync` runs only for action_approval approvals; identity decisions must not reach it.
- Phase 7 pages `Pages/Review/Index`, `Item` and `Audit`, `FlashMessages` and `Display`: extend them for the kind badge, kind-driven decision buttons, the refs-only identity view and the defer history.
- Test helpers: `OliveApiFactory` (with the `mockOidc` option), `OidcFlowDriver`, the sentinel scan in `SentinelUiTests`, and `CredentialRouteMatrixTests`.

### Established Patterns
- **Route groups.** `QueueEndpoints.cs:14` binds the `/v1/queue/items` group to the DevReviewer scheme. D-01 has to rework this without loosening other groups. `AgentKeyAuthenticationHandler` returns NoResult outside `/v1/proposals` and `/v1/catalog`, and the new bearer handler should be scoped the same way.
- **Fixture stubs** are `INonProductionOnly` and refused in production by FND-05 (`StartupGuards`).
- **Config registries** are strict-parsed YAML, validated at startup, not overridable, and cross-validated (for example D-21 catalog against kind registry).
- **Forward-only migrations** as `olive_owner`, with ENABLE + FORCE RLS, `current_setting('olive.tenant_id')`, column-level grants, and state kept out of the INSERT grant where compare-and-set applies.
- **Problem+json with static reason codes.** Missing and cross-tenant items get the same 404. Values are never echoed.

### Integration Points
- `src/Olive.Host/Program.cs`: register the bearer scheme, the verifier stub (non-production), the producers registry and its startup cross-check.
- `src/Olive.Host/Endpoints/QueueEndpoints.cs`: add the producer handlers and credential dispatch.
- `sql/migrations/0010_*.sql`: likely `body_sha256` on `queue_item`, any new event types, and grants.
- `tests/Olive.Tests/`: TC-IRQ-01..11, the extended matrix, the D-18 demo, and steward fixtures under `contracts/reviewer/`.

</code_context>

<specifics>
## Specific Ideas

- Follow IR-1 §9's test list one for one (TC-IRQ-01..06, 09..11 on Olive's side; TC-IRQ-07 and 08 are bluto-side behaviours, so the demo shows only what Olive returns).
- Fixture reviewers should allow a single test to prove that a supervisor without steward is refused on an identity item, while a steward is refused on an action item (each role gates its own kind).
- Keep the IR-1 file byte-identical. bluto pins its SHA-256 (IR-1 §10).

</specifics>

<deferred>
## Deferred Ideas

- `link_challenge` (splits) with `uphold | split | defer`. Reserved until bluto's merge and split semantics are approved (IR-1 §7.1).
- A clickable evidence link to bluto's evidence endpoint. It needs a live endpoint, a configured absolute URL and a CSP or UI-surface change.
- A client-chosen poll page size, and producer listing of non-decided states. Both are outside IR-1 and need bluto's agreement.
- Live popeye service tokens and the bluto registry entry (IR-1 §8 gate).
- Naming the `role:identity_steward` Entra group (bluto business owner).
- IR-1 §7 items 2–4: purpose for evidence reads, untenanted records, cross-tenant candidates.

</deferred>

---

*Phase: 08-identity-review-queue-kind*
*Context gathered: 2026-10-24*
