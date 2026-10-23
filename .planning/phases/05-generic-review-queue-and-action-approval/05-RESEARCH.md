# Phase 5: Generic Review Queue and Action Approval - Research

**Researched:** 2026-10-23
**Domain:** ASP.NET Core (net10.0) reviewer API, PostgreSQL 17 forced-RLS queue tables, compare-and-set concurrency, dual authentication schemes, fixture-backed ports
**Confidence:** HIGH (codebase read in full for every touched seam; the three Postgres behaviours the design leans on were run against a real postgres:17-alpine container). No new third-party package is needed.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
Carried forward, not re-decided: the settled rulings Q7 (only the claimant can release; no lease timers or reassignment), Q8 (only the claimant decides), Q9 (`queue_item_event` is INSERT-only under RLS, and `olive_app` gets only INSERT and SELECT on it), Q10 (action_approval decisions are `approve` / `reject`), Q11 (an expired item becomes `withdrawn`/`proposal_expired`, committed lazily, and list queries filter on `expires_at`), Q12 (action proposals can't be withdrawn) and Q13 (a server-side group→tenant map gives one tenant per session, never from the request). Phase 4 left behind the guarded `TransitionAsync` compare-and-set and the transition table in `ProposalStates`; Phase 5 adds the callers for approve, reject and expire.

- **D-01:** Phase 5 adds an **internal reviewer JSON API** under `/v1/queue/items`: list, claim, release, decide and per-item audit history. It sits behind a **reviewer authentication scheme that is separate from agent-key auth**. The endpoints call a queue application service, which the Phase 7 Razor pages will also call. The UI must not get its own logic path.
- **D-02:** **Callers are hard-separated.** An agent key on any reviewer path gets `403 principal_not_permitted`, and so does a reviewer identity on `/v1/proposals`. Tests prove both directions, mirroring D§7.
- **D-03:** **The IR-1 producer endpoints (`POST /v1/queue/items` create and `/withdraw`) are not built in Phase 5.** They come with `identity_review` in Phase 8. action_approval items are created only internally, inside the proposal-persist transaction (QUE-03).
- **D-04:** **Conflict signalling** uses problem+json with stable reason codes, and never echoes the rationale or parameter values: a lost claim race → `409 already_claimed`; a non-claimant release or decide → `403 not_claimant`; a conflicting decision → `409 decision_conflict`; the same reviewer resubmitting the same decision → `200` with the recorded decision (QUE-08 no-op); an expired proposal at claim or decide → `409 proposal_expired`; a missing or cross-tenant item → `404`, which cannot be told apart.
- **D-05:** **The reviewer reads the proposal through a reviewer-path detail endpoint,** `GET /v1/queue/items/{item_id}/payload`. It uses the reviewer scheme and a tenant predicate, and returns what a reviewer needs to decide: `action_type`, `parameters`, `justification`, `on_behalf_of`, `expires_at` and `trace_ref`. Queue items themselves stay references only. The endpoint resolves `payload_ref`, and agents can never reach it. `GET /v1/proposals/{id}` stays agent-only.
- **D-06: Q14 ruling.** On the wire, `on_behalf_of` stays an opaque UPN-style string (NONO §5 and the current fixtures). A new read-only port, **`IUserIdentityResolver`**, resolves `(tenant_id, on_behalf_of)` to **exactly one** Entra `(oid, tid)`. It is marked `[OutboundPort]`, backed by fixtures, and `INonProductionOnly`. The self-approval check (APR-02) compares the resolved oid+tid with the reviewer's oid+tid. If the resolver finds zero matches, finds several, or is unavailable, the approval is **refused** (fail closed, with a stable reason such as `approval_identity_unresolved`). UPN strings are never compared directly. Record this ruling and close the Q14 coordination item in STATE.md. The live adapter is still deferred.
- **D-07:** **The stub reviewer identity** comes from a **DevReviewer authentication scheme**. It is `INonProductionOnly` and barred in production by the existing startup guards (Q17). It reads a reviewer id from a request header, looks it up in a **source-controlled reviewer fixture** (oid, tid, groups), and emits claims that carry `auth_source = stub`. Phase 7 replaces it with Entra OIDC, which emits the same claims. Decisions record the auth source (APR-05).
- **D-08:** **The Q13 group/app-role → tenant map** is a source-controlled YAML file (for example under `config/`). It is strict-parsed like `config/agents.yaml` and validated at startup. A session that maps to zero tenants or to several gets 403. The tenant never comes from a header, form or query string. Phase 7 uses the same file for Entra groups.
- **D-09:** **The role gate is wired now.** Each kind in the kind registry (QUE-02) declares a `required_role`. At **decide time**, Phase 5 checks the reviewer's groups against it, using the code path Phase 7 will keep. APR-03 in Phase 7 then only swaps the identity source and adds the `max_age`/cookie re-validation and its evidence.
- **D-10:** `subject_ref` and `payload_ref` are both `olive:proposal/<proposal_id>`, `producer = olive` and `producer_ref = proposal_id`. A database CHECK and the kind registry allow only the reference schemes registered for each kind (`olive:` for action_approval; `bluto:` is registered with identity_review in Phase 8, or stubbed in the registry now, at the planner's choice). An inline payload is rejected (QUE-01, TC-QUE-01).
- **D-11:** The lazy expiry commit point is claim and decide. When `expires_at` has passed, claim or decide commits proposal → `expired`, item → `withdrawn` with reason `proposal_expired`, and the matching `proposal_event` and `queue_item_event` rows, all in one transaction. **Only then** does it return `409 proposal_expired` (POL-08). List only filters on `expires_at` and never writes. There is no sweep in Phase 5.
- **D-12:** **Expiry wins over a claim.** A claimed item whose proposal expires follows the same path: the claimant's decide gets `proposal_expired`, and the claim stays in the event history.
- **D-13:** The contract becomes IR-1 v1. Add `contracts/queue-item/v1/` with the IR-1 item shape: `additionalProperties: false`, reference-only patterns, and kind-specific fields such as `ruleset_version` optional. Add paired valid and invalid examples, including an invalid one with an inline payload. Add the source-controlled kind registry (a YAML file, strict-parsed, with no runtime registration). **Delete `contracts/queue-item/v0/`**, the placeholder Phase 2 D-15 labelled. Update the CLAUDE.md contract-path references if they mention v0.
- **D-14:** The list response carries only IR-1 item fields: refs, kind, subtype, state, claimed_by, created_at, age, and `expires_at` for action items. It is filterable by `kind` and `state`, ordered oldest first, and tenant-predicated on top of RLS. It carries no proposal parameters, justification or on_behalf_of; the reviewer follows `payload_ref` (D-05).
- **D-15:** An approve or reject updates the queue item (`decided`, `decision`, `decided_by`, `decided_at`, `rationale`, `auth_source`) **and** moves the proposal from `awaiting_approval` to `approved` or `rejected`, with events, in **one tenant transaction** (APR-04), using the guarded compare-and-set.
- **D-16:** The demo is an xUnit integration test tagged `[Trait("Demo","beat4")]`. It runs over HTTP against Testcontainers Postgres and writes a markdown transcript (to a gitignored or TestResults path). A one-line runner script under `build/` runs only that trait. It runs in CI.
- **D-17:** "Refusal traceable" means the transcript and assertions show: the 201 submit response with `refused_policy` / `purpose_not_permitted`; the stored proposal row (state, refusal_reason); its `proposal_event` rows, including `policy_evaluated` with its checks; a `queue_item` count of 0 for that proposal; the reviewer list (dev identity) not containing it. The transcript contains no parameter values.

### Claude's Discretion
- Exact route shapes (for example `POST /v1/queue/items/{id}/claim|release|decision`, `GET /v1/queue/items/{id}/events`) and DTO names in `Olive.Contracts`.
- The header name for the DevReviewer scheme, and the reviewer fixture and tenant-map file paths and schemas. Each fixture gets a schema with valid and invalid examples, following `contracts/directory/`.
- Table design for `queue_item` and `queue_item_event`. Use the next forward-only migration numbers after `0007_policy_expiry_rate.sql`, follow the Phase 1 RLS and grant pattern (ENABLE + FORCE RLS, `current_setting('olive.tenant_id')` without missing_ok, column-level grants, state kept out of the INSERT grant where CAS applies), and extend `PrivilegeMatrixTests`, `RlsPatternTests`, `MigrationRunnerTests` and the tenant-isolation ablation tests to the new tables. Decide whether the item has a unique constraint on `(tenant_id, producer, producer_ref)` now; recommended yes, since it also backs IR-1 idempotency in Phase 8.
- Stable reason-code names beyond those listed in D-04 and D-06.
- Whether the concurrent-claim proof uses N parallel HTTP calls or store-level calls. Exactly one winner must be asserted either way.
- Where the queue service and kind registry live, within the FND-01 inward dependency graph (for example, registry parsing in `Olive.Catalog`, the service in `Olive.Application`, the store in `Olive.Persistence`).

### Deferred Ideas (OUT OF SCOPE)
- A sweep at startup or on a timer for expired items. Not chosen; withdrawal is lazy at claim and decide (D-11).
- A live `IUserIdentityResolver` (Entra/Graph) adapter. It goes with the live integration work after Phase 7.
- The IR-1 producer create and withdraw endpoints and the `identity_review` kind are Phase 8.
- Not in this phase: execution after approval (Phase 6), the Razor UI and real Entra OIDC (Phase 7), the APR-03 decision-time role re-validation evidence (Phase 7).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| QUE-01 | IR-1 item model, refs only, CHECK on schemes, inline payload rejected | Migration 0008 CHECKs (kind-scoped ref regex); `QueueKindRegistry.ValidateRefs`; v1 schema + inline-payload invalid example (Patterns 1, 2, 7) |
| QUE-02 | Source-controlled kind registry, `action_approval` + `identity_review`, no runtime registration | `config/queue-kinds.yaml`, parser in `Olive.Catalog`, `IQueueKindRegistry` in `Olive.Application` (Pattern 2) |
| QUE-03 | Enqueue in the accept transaction | Extend `PostgresProposalStore.InsertEvaluatedAsync` accepted branch with `QueueSql.InsertItem` + `created` event (Pattern 3) |
| QUE-04 | List open items for tenant, expired filtered | `ListAsync` with `now` param and `expires_at` predicate, never writes (Pattern 6) |
| QUE-05 | Claim is CAS, one winner | `UPDATE ... WHERE state='open'` rowcount (Pattern 4); verified lock behaviour |
| QUE-06 | Only claimant releases | CAS `WHERE claimed_by=@actor`, classify on miss (Pattern 4) |
| QUE-07 | Only claimant decides, decided_by/decided_at/rationale mandatory | App validation + DB CHECKs (Pattern 5, migration) |
| QUE-08 | Same decision no-op, conflicting rejected | `SELECT ... FOR UPDATE` classification (Pattern 5) |
| QUE-09 | Every claim/release/decision/withdrawal in `queue_item_event` | Event written inside same tx as each CAS; `seq` ordering column (Pitfall 3) |
| QUE-10 | Expired queued proposal → `withdrawn`/`proposal_expired` | Lazy expiry transaction (Pattern 4/5, Pitfall 1) |
| APR-01 | Approval needs authenticated reviewer | DevReviewer scheme, `RequireAuthorization` with explicit scheme (Pattern 8) |
| APR-02 | Approver != on_behalf_of by oid+tid; ambiguous refused | `IUserIdentityResolver` + `SelfApprovalCheck` (Pattern 9) |
| APR-04 | Decision updates item + proposal in one tx | `PostgresQueueStore.DecideAsync` reuses `ProposalSql.UpdateState`/`InsertEvent` on the same `(conn, tx)` (Pattern 5, Pitfall 2) |
| APR-05 | auth source recorded | `auth_source` column + claim `olive:auth_source` (Pattern 8) |
| POL-08 | Expired proposal can't be approved; distinct `proposal_expired` after commit | Result-union return, never throw inside the UoW lambda (Pitfall 1) |
| EVD-03 | Beat 4 scripted demo | `[Trait("Demo","beat4")]` test + `build/demo-beat4.sh` (Pattern 10) |
</phase_requirements>

## Summary

Phase 5 is almost entirely additive on top of seams Phase 4 left ready. The accepted-proposal transaction (`PostgresProposalStore.InsertEvaluatedAsync`) already ends with a guarded `proposed → awaiting_approval` CAS and event inside one `TenantUnitOfWork` transaction; QUE-03 is "add two INSERT statements to that branch". Decisions need the same compare-and-set and event SQL, but joined to a queue-item update on the same connection and transaction, so the queue store must reuse `ProposalSql.UpdateState`/`InsertEvent` directly. It must not call `IProposalStore.TransitionAsync`, which opens its own transaction and would break APR-04.

The one non-obvious design trap is POL-08. `TenantUnitOfWork.ExecuteAsync` commits only when the work lambda returns normally. If the expiry path threw `proposal_expired` from inside the lambda, the expiry transition would roll back, which is exactly what POL-08 forbids. Every store method must therefore return a result union (`Claimed | AlreadyClaimed | ProposalExpired | ...`) and the endpoint maps it to 409 after commit. A second trap is that a repeat approve on an already-lazily-withdrawn item must still return `proposal_expired`, not a generic "closed" error.

Authentication is the most structurally new piece: two schemes in one host with the default staying `AgentKey`. Reviewer routes use an explicit scheme in the authorization policy. Cross-caller 403s (D-02) come from each handler's challenge override, not from principal-type claims. The DevReviewer scheme is a handler, which the `INonProductionOnly` guard cannot see as a service, so the guard attaches to the fixture reviewer-identity source the handler depends on, plus an explicit scheme check.

**Primary recommendation:** One migration `0008_review_queue.sql` (two tables, forced RLS, column-level grants, DB-side state/ref/decision CHECKs). A `PostgresQueueStore` that classifies outcomes with `SELECT … FOR UPDATE` then CAS, never throws inside the unit of work. `QueueService` in `Olive.Application` orchestrating registry, role gate, identity resolver and clock. A reviewer-only `QueueEndpoints.cs`. All new YAML (kinds, tenant map) parsed in `Olive.Catalog` with the `AgentAllowlist` pattern. No new packages and no new projects.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Enqueue at accept | Database / Storage (same tx in `Olive.Persistence`) | API / Backend (endpoint unchanged) | QUE-03 requires the same transaction as the proposal insert |
| Claim / release / decide CAS | Database / Storage | API / Backend (`QueueService`) | Concurrency winner is decided by the row lock, not app code |
| Role gate, self-approval, rationale rules | API / Backend (`Olive.Application`) | Database (CHECK backstops) | Business rules live inward; DB CHECKs are defence in depth |
| Lazy expiry | Database / Storage (one tx) | API / Backend (clock via `TimeProvider`) | Must commit before the 409 is returned |
| Reviewer authentication, tenant mapping | API / Backend (`Olive.Host/Auth`) | Fixtures (`Olive.Integrations.Entra`) | Phase 7 swaps only the identity source |
| Kind registry, tenant map | API / Backend (`Olive.Catalog` parse, `Olive.Application` interface) | — | Source-controlled config, validated at startup |
| Tenant isolation | Database (forced RLS) | API (every query keeps `tenant_id = @tenant_id`) | Both layers are mandatory (CLAUDE.md) |
| Beat 4 demo | Test project | `build/` runner | Runs over HTTP in CI |

## Standard Stack

### Core (all already present; no additions)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| ASP.NET Core minimal APIs / authentication | net10.0 (SDK 10.0.204) | Reviewer endpoints, second auth scheme | Existing host pattern (`AgentKeyAuthenticationHandler`) |
| Npgsql | 10.0.3 | Postgres access | `Directory.Packages.props` [VERIFIED: codebase] |
| Dapper | 2.1.89 | SQL mapping in `Olive.Persistence` | Existing `PostgresProposalStore` pattern [VERIFIED: codebase] |
| YamlDotNet | 18.1.0 | Strict YAML for kind registry + tenant map | `AgentAllowlist` precedent [VERIFIED: codebase] |
| JsonSchema.Net | 8.0.5 | Contract schema tests (`SchemaValidator`) | Existing `ContractSchemaTests` [VERIFIED: codebase] |
| xunit.v3 3.2.2 + Testcontainers.PostgreSql 4.16.0 (postgres:17-alpine) + Mvc.Testing 10.0.13 | as pinned | Tests | Existing harness [VERIFIED: codebase] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `SELECT … FOR UPDATE` then CAS | Pure `UPDATE … WHERE` + re-read on miss | Pure CAS is fine for claim/release; decide has too many outcomes (idempotent, conflict, not-claimant, expired), so lock-then-classify is simpler and exact |
| SERIALIZABLE isolation | READ COMMITTED + row lock | Row lock gives the single-winner guarantee. SERIALIZABLE would add retry loops the codebase does not have |
| New `Olive.Queue` project | Existing projects | `FND_01_exactly_eleven_src_projects` and the reference allowlist would need re-ratification. Do not add a project |

**Installation:** none. **Package Legitimacy Audit:** no external packages are added by this phase, so the slopcheck gate does not apply. `packages.lock.json` files stay unchanged (CI uses `--locked-mode`); `FND_02_direct_packages_match_allowlist_in_lockfiles` must keep passing.

## Architecture Patterns

### System Architecture Diagram

```
Agent (Authorization: Bearer <session key>)            Reviewer (X-Olive-Dev-Reviewer: <id>)  [Phase 7: Entra OIDC, same claims]
        |                                                         |
  POST /v1/proposals                                  /v1/queue/items/*  (explicit scheme DevReviewer)
  AgentKey scheme                                     DevReviewer handler -> IReviewerIdentitySource (fixture, INonProductionOnly)
        |  ...Phase 2-4 pipeline unchanged...                     |  -> claims: oid, tid, groups, auth_source=stub, tenant (via IReviewerTenantMap)
        v                                                         v
 PostgresProposalStore.InsertEvaluatedAsync               QueueEndpoints  (thin: parse, call service, map result -> problem+json)
   one tenant tx:                                                 |
   insert proposal -> events -> CAS proposed->awaiting_approval   v
   + INSERT queue_item (open) + queue_item_event(created)   QueueService (Olive.Application)
        |                                                    - TimeProvider now
        v                                                    - IQueueKindRegistry (decisions, required_role, ref schemes)
   queue_item / queue_item_event / proposal / proposal_event - role gate (reviewer groups vs kind.required_role) at DECIDE
        ^                                                    - IUserIdentityResolver (outside any tx) for approve
        |                                                    v
        +------------ PostgresQueueStore (Olive.Persistence) ------------+
                      one TenantUnitOfWork tx per call:
                      list | get | claim | release | decide | events | payload
                      expiry path: item->withdrawn + proposal CAS ->expired + 2 events, COMMIT, then return ProposalExpired
```

### Recommended Project Structure
```
config/queue-kinds.yaml                 # kind registry (embedded via existing config/*.yaml glob in Olive.Catalog.csproj)
config/reviewer-tenants.yaml            # Q13 group -> tenant map (same glob)
contracts/queue-item/v1/                # replaces v0: queue-item.schema.json + .valid.json + .invalid.json (+ inline-payload invalid, + README)
contracts/reviewer/                     # reviewer fixtures: reviewer-identity.schema.json, index.json, reviewer-*.json, README.md
contracts/directory/ (add)              # user-identity fixtures: see Pattern 9 (or a new contracts/identity/ folder)
sql/migrations/0008_review_queue.sql
src/Olive.Domain/QueueStates.cs         # constants: item states, decisions, reasons, auth sources
src/Olive.Application/IQueueKindRegistry.cs, IReviewerTenantMap.cs, IUserIdentityResolver.cs, IQueueStore.cs, QueueService.cs, ReviewerIdentity.cs
src/Olive.Catalog/QueueKindRegistry.cs, ReviewerTenantMap.cs     # YamlDotNet strict parse, AgentAllowlist style
src/Olive.Persistence/PostgresQueueStore.cs, QueueSql.cs
src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs, FixtureUserIdentityResolver.cs   # + embed new fixture folder(s) in the csproj
src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs, ReviewerClaims.cs
src/Olive.Host/Endpoints/QueueEndpoints.cs
src/Olive.Contracts/QueueDtos.cs; ProblemReasons.cs (+ new reasons)
tests/Olive.Tests/Queue*Tests.cs, Beat4DemoTests.cs; build/demo-beat4.sh
```
`Olive.Application` cannot reference `Olive.Catalog` (ArchitectureTests), so interfaces and records live in Application and YAML parsers in Catalog, exactly as `IAgentAllowlist`/`AgentAllowlist`. No `.csproj` project-reference changes are needed. `Olive.Integrations.Entra.csproj` needs a new `EmbeddedResource` line for the new fixture folder(s) (the existing one only embeds `contracts\directory\*.json`).

### Pattern 1: Migration 0008 (tables, RLS, grants, CHECKs)
**What:** One forward-only file, checksum-pinned once applied. Follows 0001's RLS/policy text exactly, because `RlsPatternTests` asserts `current_setting('olive.tenant_id'::text)` with no `missing_ok`, and column-level grants as in 0002/0003.
**Example (shape, not final):**
```sql
create table queue_item (
    tenant_id uuid not null,
    item_id uuid not null,
    kind text not null check (kind in ('action_approval', 'identity_review')),
    subtype text not null,
    producer text not null check (producer in ('olive', 'bluto')),
    producer_ref uuid not null,
    subject_ref text not null,
    payload_ref text not null,
    proposal_id uuid null,                 -- action_approval only
    ruleset_version text null,             -- identity_review only (IR-1)
    state text not null default 'open' check (state in ('open','claimed','decided','withdrawn')),
    claimed_by text null, claimed_at timestamptz null,
    decision text null, decided_by text null, decided_at timestamptz null,
    rationale text null check (rationale is null or length(rationale) <= 4000),
    auth_source text null check (auth_source in ('entra','stub')),
    withdrawn_reason text null check (withdrawn_reason ~ '^[a-z][a-z_]{0,63}$'),
    expires_at timestamptz null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, item_id),
    constraint queue_item_producer_unique unique (tenant_id, producer, producer_ref),
    foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id),
    -- kind-scoped reference schemes (QUE-01): the DB is the last line against inline bodies
    constraint queue_item_refs_by_kind check (
        (kind = 'action_approval' and producer = 'olive' and proposal_id is not null
            and subject_ref = 'olive:proposal/' || proposal_id::text and payload_ref = subject_ref
            and producer_ref = proposal_id)
     or (kind = 'identity_review' and producer = 'bluto' and proposal_id is null
            and subject_ref ~ '^bluto:candidate/[0-9a-f-]{36}$' and payload_ref ~ '^bluto:review-evidence/[0-9a-f-]{36}$')),
    constraint queue_item_state_shape check (
        (state = 'open'      and claimed_by is null and decision is null and decided_by is null and withdrawn_reason is null)
     or (state = 'claimed'   and claimed_by is not null and decision is null and decided_by is null and withdrawn_reason is null)
     or (state = 'decided'   and claimed_by is not null and decided_by = claimed_by and decision is not null and decided_at is not null
            and auth_source is not null and rationale is not null and rationale !~ '^\s*$' and withdrawn_reason is null)
     or (state = 'withdrawn' and decision is null and withdrawn_reason is not null)),
    constraint queue_item_decision_by_kind check (decision is null
        or (kind = 'action_approval' and decision in ('approve','reject'))
        or (kind = 'identity_review' and decision in ('accept','reject','defer')))
);
create table queue_item_event (
    tenant_id uuid not null, event_id uuid not null,
    seq bigint generated always as identity,
    item_id uuid not null,
    event_type text not null check (event_type in ('created','claimed','released','decided','withdrawn')),
    from_state text null, to_state text not null,
    actor text not null, detail jsonb null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, event_id),
    foreign key (tenant_id, item_id) references queue_item (tenant_id, item_id)
);
-- RLS: copy the four statements from 0001 for BOTH tables (enable, force, create policy tenant_isolation using/with check).
revoke all on queue_item, queue_item_event from public;
grant select on queue_item, queue_item_event to olive_reader;
grant select on queue_item to olive_app;
grant insert (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, proposal_id, ruleset_version, expires_at) on queue_item to olive_app;
grant update (state, claimed_by, claimed_at, decision, decided_by, decided_at, rationale, auth_source, withdrawn_reason) on queue_item to olive_app;
grant select, insert on queue_item_event to olive_app;           -- NO update/delete/truncate (Q9)
create index queue_item_open_idx on queue_item (tenant_id, kind, state, created_at);
create index queue_item_event_item_idx on queue_item_event (tenant_id, item_id, seq);
```
Notes: state is excluded from the INSERT grant (defaults `open`), as 0003 did for proposal. `claimed_by` check `decided_by = claimed_by` encodes Q8 in the database. The `identity_review` branch is registered now so the kind CHECK matches the registry; no code path creates those rows until Phase 8. `proposal_id` plus the composite FK gives referential integrity the generic columns cannot, and the `producer_ref = proposal_id` clause makes the unique constraint also stop a duplicate enqueue for a proposal (closes the scaffold CONCERNS "duplicate enqueue on resubmit" at the DB level). Add a small `Ct` regression test that a second insert for the same proposal raises 23505.

### Pattern 2: Kind registry (QUE-02)
`config/queue-kinds.yaml`, strict-parsed like `AgentAllowlist` (`UnderscoredNamingConvention`, no `IgnoreUnmatchedProperties`, static `Invalid` message, startup failure through a new `StartupGuards.CheckQueueKinds` modelled on `CheckCatalog`):
```yaml
kinds:
  - kind: action_approval
    subtypes: [proposal]
    decisions: [approve, reject]
    required_role: role:case_supervisor
    ref_schemes: [olive]
    expires: true
  - kind: identity_review
    subtypes: [candidate_pair]       # link_challenge deliberately absent (IR-1 §7 item 1, TC-IRQ-11)
    decisions: [accept, reject, defer]
    required_role: role:identity_steward
    ref_schemes: [bluto]
    expires: false
```
Interface in `Olive.Application`: `IQueueKindRegistry { bool TryGet(string kind, out QueueKindDefinition def); IReadOnlyList<string> Kinds }`. There is no `Register`/`Add` member (REQ-ACT-06 style); add a reflection/architecture test like the existing catalog one that the type exposes no mutator. Validation rules at load: kind ids unique and match `^[a-z][a-z_]*$`; decisions non-empty; `required_role` matches `^role:[a-z_]+$`; ref scheme names are lowercase. Add a **drift test**: the kind set and per-kind decision sets equal the DB CHECK (query `pg_get_constraintdef`, or insert-and-expect-23514 per row) so registry and migration cannot diverge.

Subtype for action_approval is not fixed by IR-1 or CONTEXT. Recommend the constant `proposal` [ASSUMED, planner may choose another; it must be in the registry and not derived from request data].

Cross-check pitfall: `catalog/crm.case_note@1.yaml` already carries `required_approval: role:case_supervisor` per action. D-09 puts `required_role` on the kind. Two sources of truth can drift. Add a startup check that every catalog entry's `required_approval` equals the `action_approval` kind's `required_role` (fail with a static message), or the reverse intent is lost silently.

### Pattern 3: Enqueue in the accept transaction (QUE-03)
In `PostgresProposalStore.InsertEvaluatedAsync`, after the `transition` event in the accepted branch (and only there; the refused branches and `InsertRefusedAsync` must not enqueue, which also makes EVD-03 hold structurally):
```csharp
await conn.ExecuteAsync(new CommandDefinition(QueueSql.InsertItem, new {
    tenant_id = tenantId, item_id = Guid.NewGuid(), kind = "action_approval", subtype = "proposal",
    producer = "olive", producer_ref = id, subject_ref = $"olive:proposal/{id}", payload_ref = $"olive:proposal/{id}",
    proposal_id = id, expires_at = row.ExpiresAt?.UtcDateTime }, tx, cancellationToken: ct));
await conn.ExecuteAsync(new CommandDefinition(QueueSql.InsertEvent, new { tenant_id = tenantId, event_id = Guid.NewGuid(),
    item_id = itemId, event_type = "created", from_state = (string?)null, to_state = "open", actor = p.AgentId, detail = (string?)null }, tx, cancellationToken: ct));
```
`InsertEvaluatedAsync` already throws if `ExpiresAt` is null, so the copied `expires_at` is always set for action items. Idempotent replays return before the insert (`on conflict ... do nothing` yields null, handled earlier), so a replay can never enqueue twice. The interface `IProposalStore` signature does not change, so `RecordingProposalStore` and the OliveApiFactory wiring are unaffected. Do **not** add queue methods to `IProposalStore` (it would force edits to every test double). Create a separate `IQueueStore`.

### Pattern 4: Claim, release, lazy expiry (QUE-05, 06, 10, POL-08)
All store methods return a closed result type; none throws for business outcomes. Template (claim):
```csharp
// one TenantUnitOfWork tx
var item = await conn.QuerySingleOrDefaultAsync<ItemRow>(QueueSql.GetForUpdate /* ... where tenant_id=@tenant_id and item_id=@item_id for update */);
if (item is null) return ClaimResult.NotFound;                    // 404, cross-tenant indistinguishable
if (item.State is open or claimed && item.ExpiresAt <= now)        // now = TimeProvider passed in, same boundary as key expiry
{
    await ExpireAsync(conn, tx, item, trigger: "claim", ct);       // item->withdrawn, proposal CAS awaiting_approval->expired, 2 events
    return ClaimResult.ProposalExpired;                            // returned AFTER the lambda completes => UoW commits
}
if (item.State == withdrawn && item.WithdrawnReason == "proposal_expired") return ClaimResult.ProposalExpired;   // repeat call, still distinct
if (item.State == claimed) return ClaimResult.AlreadyClaimed;
if (item.State != open) return ClaimResult.Closed;
// CAS (belt and braces; the row lock already serialises)
var n = await conn.ExecuteAsync(QueueSql.Claim /* update ... set state='claimed', claimed_by=@actor, claimed_at=@now where tenant_id=@tenant_id and item_id=@item_id and state='open' */);
// insert queue_item_event('claimed', open->claimed, actor)
```
Concurrency semantics were verified against postgres:17: `SELECT … FOR UPDATE` works for `olive_app` holding only a column-level UPDATE grant under forced RLS [VERIFIED: ran against postgres:17-alpine in this session]; the PostgreSQL docs state FOR UPDATE needs UPDATE privilege on at least one column [CITED: postgresql.org/docs/current/sql-select.html]. READ COMMITTED is enough: the second contender blocks on the row lock, then re-reads `state='claimed'`. No SERIALIZABLE and no retry loop.

`ExpireAsync` must tolerate partial prior progress: item `UPDATE … WHERE state in ('open','claimed')` and proposal `UpdateState(awaiting_approval → expired)`. If the proposal CAS affects 0 rows because it is already `expired`, continue (the item must still be withdrawn); if the proposal is in any other state, that is an invariant violation, so roll back and surface a 500 (do not guess). Events: `proposal_event` ("transition", awaiting_approval → expired, actor `system`, static detail `{"trigger":"claim|decide"}`) and `queue_item_event` ("withdrawn", from the current state, reason in `detail`). The `claimed` event of an earlier claim stays untouched (D-12).

Release: `FOR UPDATE`, then not found → 404; expired → expiry path; `state != claimed` → `NotClaimed` (open) or `Closed`; `claimed_by != actor` → `NotClaimant` (403); else `UPDATE … set state='open', claimed_by=null, claimed_at=null where … and claimed_by=@actor and state='claimed'` + `released` event.

Expiry boundary: treat `expires_at <= now` as expired (fail closed), consistent with `key.ExpiresAt <= now` in the auth handler and endpoint [ASSUMED: charter §3 does not state the equality case; confirm in the plan and pin in a test using `ManualTimeProvider` exactly at `expires_at`].

### Pattern 5: Decide (QUE-07, 08, APR-04, D-15)
Service order (cheap and stateless first; the resolver is a port, so it runs outside any transaction):
1. Endpoint: authenticate scheme, exactly-one tenant, then body validation: decision value in registry set for the item's kind, rationale non-blank (`string.IsNullOrWhiteSpace`, Unicode-aware) and length ≤ 4000, `decided_by` comes from the claims, never the body (a body field named `decided_by` is rejected as `invalid_request`; "missing `decided_by`" is satisfied by a store/service guard that rejects an empty actor, test at service level with an empty identity).
2. `GetAsync` (read tx) → 404 if missing. Kind must have a decision handler (Phase 5: `action_approval` only; other kinds return a typed `kind_not_supported` to avoid half-implementing identity_review).
3. Role gate (D-09): `reviewer.Groups.Contains(kind.RequiredRole)` else `403 role_not_permitted`. At decide only, not at claim/list/payload (APR-03: "not when the item is claimed").
4. If the item is already `decided`: same reviewer + same decision → `200` recorded decision, no new event. Same reviewer + different decision → `409 decision_conflict`. Other reviewer → `403 not_claimant`. (Decided-state handling happens before expiry: a decision made before expiry stays valid.)
5. If open/claimed and expired (or withdrawn/proposal_expired) → expiry tx, then `409 proposal_expired`.
6. `state == open` → `409 item_not_claimed`; claimed by someone else → `403 not_claimant`.
7. Approve only: `IUserIdentityResolver` (Pattern 9), outside any transaction. Reject skips the resolver [ASSUMED: APR-02 says "approver"; recommending reject need not fail closed on resolver outage. Flag to user].
8. `DecideAsync` tx: `FOR UPDATE` re-read (state may have changed since step 2), re-run steps 4–6 against the locked row, then `UPDATE queue_item … state='decided', decision, decided_by, decided_at=@now, rationale, auth_source`, `ProposalSql.UpdateState(awaiting_approval → approved|rejected)` (`n == 1` or roll back), `proposal_event` ("transition", actor = reviewer oid, detail static `{"item_id":…, "decision":…, "auth_source":"stub"}`, no rationale) and `queue_item_event` ("decided"). All on the same `(conn, tx)`.

Do not call `TransitionAsync` from here: it opens its own `uow.ExecuteAsync` transaction (separate connection), so the item and proposal could diverge. Instead `PostgresQueueStore` uses the `ProposalSql` constants directly and re-checks `ProposalStates.CanTransition` before the SQL (same guard).

`decided_by` value: use the reviewer's Entra `oid` (stable, non-PII) [ASSUMED: IR-1 says "authenticated Entra identity"]; the tenant `tid` is already the row's tenant by construction of Q13 plus the oid/tid mapping.

### Pattern 6: List and audit reads (QUE-04, QUE-09, D-14)
```sql
select item_id, kind, subtype, state, subject_ref, payload_ref, claimed_by, created_at, expires_at
from queue_item
where tenant_id = @tenant_id
  and (@kind is null or kind = @kind) and (@state is null or state = @state)
  and not (state in ('open','claimed') and expires_at is not null and expires_at <= @now)
order by created_at, item_id
limit @limit
```
List never writes (D-11). Default `state` filter: when absent, return `open` only (QUE-04 says "open items") [ASSUMED]; allow explicit `state=claimed|decided|withdrawn`. `age_seconds` is computed in C# from `@now`. Add `limit` (default 100, cap 500) and validate `kind`/`state` against the registry/constants (unknown → 400 `invalid_request`), never concatenate. Events: `select … from queue_item_event where tenant_id=@tenant_id and item_id=@item_id order by seq`. `ORDER BY created_at` alone is unsafe (see Pitfall 3).

`GET /v1/queue/items/{id}` (single item with decision fields) is recommended for the idempotent decision response and for audit; D-14's field limit applies to the list.

### Pattern 7: IR-1 v1 contract (D-13)
Create `contracts/queue-item/v1/queue-item.schema.json` (+ `.valid.json`, `.invalid.json`, additional `queue-item.inline-payload.invalid.json`, README). `additionalProperties: false`; required: `item_id, kind, subtype, tenant_id, producer, producer_ref, subject_ref, payload_ref, state, created_at`; `subject_ref`/`payload_ref` patterns `^(olive:proposal|bluto:candidate|bluto:review-evidence)/<uuid>$`; `state` enum `open|claimed|decided|withdrawn`; `decision` enum of the union `approve|reject|accept|defer`; `ruleset_version` optional; response-only optional `age_seconds`, `expires_at`, `withdrawn_reason`. Required housekeeping when deleting v0:
- `git rm -r contracts/queue-item/v0`
- `tests/Olive.Tests/ContractSchemaTests.cs` `Folders` array: replace `contracts/queue-item/v0` with `contracts/queue-item/v1` (the test requires a `.valid.json` and `.invalid.json` beside each schema: `API_05_every_schema_has_paired_examples_...`)
- `CLAUDE.md` lines 66 and 131 mention `v0` (`contracts/queue-item/v0/queue-item.schema.json`, `queue-item/v0/`); `AGENTS.md` mentions queues but not the path (verified by grep). `.planning/codebase/*.md` also cite v0 and are historical maps; update or leave per planner.
- No C# code references the v0 path (grep: only the test above).
Add a response-shape test that a real `GET /v1/queue/items` item validates against the v1 schema through `SchemaValidator`, so contract and code cannot drift.

### Pattern 8: Reviewer authentication (D-02, D-07, D-08, APR-01, APR-05)
Handler mirrors `AgentKeyAuthenticationHandler`: `public sealed class DevReviewerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>`, `SchemeName = "DevReviewer"`, header e.g. `X-Olive-Dev-Reviewer` (discretion; do not use `Authorization`, since that is the agent key and the two must be distinguishable). It resolves the id through `IReviewerIdentitySource` (interface in Application, fixture implementation `INonProductionOnly` in `Olive.Integrations.Entra`), then `IReviewerTenantMap` (groups → tenant). Claims (new `ReviewerClaims`, parallel to `OliveClaims`): `olive:reviewer_oid`, `olive:reviewer_tid`, repeated `olive:group`, `olive:auth_source` = `stub`, and `olive:tenant_id` only when exactly one tenant maps.

Registration (Program.cs): keep `AddAuthentication(AgentKeyAuthenticationHandler.SchemeName)` as the default, then `.AddScheme<…, DevReviewerAuthenticationHandler>("DevReviewer", _ => { })`. Protect reviewer routes with `RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DevReviewerAuthenticationHandler.SchemeName })` on a `MapGroup("/v1/queue")`. Existing `/v1/proposals` and `/v1/catalog` keep the default policy (default scheme only), so they stay agent-only.

D-02 mechanics (the non-obvious part): an authentication failure normally yields 401 via `HandleChallengeAsync`. The required results are 403 `principal_not_permitted` in both cross directions, so override the challenge:
- `DevReviewer.HandleChallengeAsync`: if the request carries an `Authorization: Bearer …` header (an agent key) and no reviewer header → 403 `principal_not_permitted`; otherwise 401 (new reason `reviewer_unauthenticated`, static title). The agent key is never sent to popeye from this path... **caution:** `UseAuthentication` always runs the *default* (AgentKey) scheme for every request, so a reviewer path request carrying `Authorization` will still trigger an introspection call by the default scheme. That is harmless (the key only goes to the introspector, D17.1), but a test must assert no secret is logged. Option: make the AgentKey handler return `NoResult` when the request path starts with `/v1/queue` to avoid the call; recommended, with a test that the introspector records zero calls on reviewer routes.
- `AgentKey.HandleChallengeAsync`: if the reviewer header is present and no valid agent key → 403 `principal_not_permitted` instead of 401 `key_inactive`. Presence of a reviewer header on `/v1/proposals` therefore changes the response from 401 to 403; keep a test that the header-less path remains 401 (the `StartupGuardTests` 401 probes rely on it).
- Both headers present on a reviewer route: the reviewer scheme alone decides (the agent header is ignored). On `/v1/proposals`: the agent scheme alone decides. A request needs the credential of the path's own scheme; test both.

Tenant resolution (D-08): groups from the fixture → tenants through the map; the distinct tenant set must have exactly one element. Zero or many → authenticate the identity but expose no tenant claim; the endpoint group filter (or a `HandleForbiddenAsync` override via a `ReviewerTenantRequirement`) returns 403 `reviewer_tenant_unresolved`. The tenant is read only from the claim. Add a test that a `tenant_id` query/header/body value is ignored.

Production bar (Q17): the existing `CheckNoNonProductionComponents` scans service descriptors and resolved instances for `INonProductionOnly`. A scheme handler is not a registered service, so the guard cannot see it. Therefore (a) the handler must depend on `IReviewerIdentitySource`, registered as a singleton fixture implementation that is `INonProductionOnly` (the guard then refuses production start with FND-05, no guard code change), and (b) add a guard test mirroring `FND_05_production_with_stub_*` for `FixtureReviewerIdentitySource`, `FixtureUserIdentityResolver` and, defensively, an explicit `StartupGuards` check that the `DevReviewer` scheme is absent in production via `IAuthenticationSchemeProvider.GetSchemeAsync`. Phase 7 registers an Entra source instead, which is the only change at that seam.

`auth_source`: take it from the claim (`stub` today, `entra` in Phase 7), never from the request, and store it on the decided row and in the `proposal_event`/`queue_item_event` detail.

### Pattern 9: `IUserIdentityResolver` (D-06, APR-02)
```csharp
public abstract record UserIdentityLookup {
    public sealed record Resolved(string Oid, string Tid) : UserIdentityLookup;
    public sealed record NotFound : UserIdentityLookup;       // zero matches
    public sealed record Ambiguous : UserIdentityLookup;      // several matches
    public sealed record Unavailable : UserIdentityLookup;
    public sealed record Malformed : UserIdentityLookup;
}
[OutboundPort(OutboundDestination.Directory)]
public interface IUserIdentityResolver { Task<UserIdentityLookup> ResolveAsync(Guid tenantId, string onBehalfOf, CancellationToken ct); }
```
Reuse `OutboundDestination.Directory` (no new enum member needed); ArchitectureTests/containment tests that enumerate `[OutboundPort]` interfaces must be checked for assumptions (grep `OutboundPort` in tests before planning; `RecordingPorts.cs` has per-port recorders, so add `RecordingUserIdentityResolver` to match the containment pattern). No session key and no service token by construction (same rule as `IDirectoryGroupResolver`).

Fixture: `FixtureUserIdentityResolver : IUserIdentityResolver, INonProductionOnly`, keyed `(tenant, on_behalf_of)` ordinal. To represent "ambiguous" the fixture format must allow multiple records per key: store as `ConcurrentDictionary<(Guid, string), List<(string Oid, string Tid)>>`, `Register` appends, and `ResolveAsync` returns `Ambiguous` when `Count > 1`. Fixture schema, following `contracts/directory/` (schema + valid/invalid + `index.json` + README): `{ "tenant_id", "on_behalf_of", "oid", "tid" }`, with a fixture that deliberately duplicates one user for the ambiguity test. `on_behalf_of` for `analyst.alpha@example.test` must map to a reviewer fixture oid for the self-approval test (`reviewer-self` oid equals that user's oid). The comparison is `string.Equals(oid, …, OrdinalIgnoreCase)` on lowercase GUID text and `tid` likewise; UPN strings are never compared (test: a reviewer whose display UPN equals `on_behalf_of` but a different oid is allowed; a reviewer with the same oid but a different UPN is refused).

Mapping to HTTP: equal oid+tid → `403 self_approval_forbidden`; NotFound/Ambiguous/Malformed → `403 approval_identity_unresolved`; Unavailable (fault/timeout, with a `TimeoutOptions` like `DirectoryVerifier`) → `503 identity_resolution_unavailable` retryable [ASSUMED mapping; D-06 requires "refused", both are]. Wrap the call in a small `UserIdentityVerifier` (copy `DirectoryVerifier`'s timeout/catch-all pattern; read it first) so a faulting resolver cannot become a 500 or a fail-open. A faulting test double goes in `FaultingDoubles.cs` (test project only).

### Pattern 10: Beat 4 demo (EVD-03, D-16, D-17)
`tests/Olive.Tests/Beat4DemoTests.cs` with `[Trait("Demo","beat4")]` and `[Trait("Category","Integration")]`: submits an out-of-purpose proposal (the key's purpose not in `required_purpose [servicing, collections]`; e.g. a key/fixture with purpose `marketing`, check `contracts/introspection/*.json` for an existing one or add a fixture) over HTTP; also submits one in-purpose proposal so the reviewer list is non-empty and the contrast is visible. Asserts and writes the transcript from: the 201 body (`state=refused_policy`, `refusal_reason=purpose_not_permitted`), the stored row (`olive_reader` query as in `ApiProposalTests.ReaderScalarAsync`), the `proposal_event` rows with the `policy_evaluated` detail, `select count(*) from queue_item where proposal_id=@id` = 0, and the reviewer `GET /v1/queue/items` response not containing the id. Transcript path: `Path.Combine(TestPaths.Root, "TestResults", "beat4-transcript.md")` (`TestResults/` is already gitignored). Never write `parameters`, `justification` or the rationale into it; assert the file text does not contain the parameter sentinel (reuse `SentinelScan`). Runner: `build/demo-beat4.sh` containing `dotnet test tests/Olive.Tests --filter "Demo=beat4"` (xunit.v3 with VSTest `--filter` works here: CI already uses `--filter "Category!=Integration"` [VERIFIED: ci.yml]). CI already runs the full suite, so the demo runs in CI automatically.

### Anti-Patterns to Avoid
- **Throwing for business outcomes inside `uow.ExecuteAsync`:** rolls back the expiry commit (POL-08).
- **Calling `IProposalStore.TransitionAsync` from the queue path:** separate transaction, breaks APR-04.
- **Putting a reviewer identity or tenant into the request body/query/header** (other than the credential header): Q13.
- **`ORDER BY created_at` for event history:** `now()` is the transaction start time, so all events in one transaction tie (verified: `now() = clock_timestamp()` is false mid-transaction).
- **Dropping the `tenant_id = @tenant_id` predicate** because RLS exists. `TenantIsolationAblationTests` must be extended to prove each layer in turn on the new tables.
- **Hard-coding the role string in endpoint code:** use the registry's `required_role`, or Phase 7 cannot "only swap the identity source".
- **Echoing the rationale or any parameter value** in problem+json titles/extensions, logs, events, or the transcript.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Exactly-one-winner claim | App-level locks, `lock`, in-memory sets, retry loops | Row lock + `UPDATE … WHERE state='open'` rowcount | Multi-instance safe; the scaffold's non-thread-safe `Dictionary` queue is the defect CONCERNS lists |
| Strict YAML | Line scanners (the scaffold's `Parse`) | YamlDotNet strict deserialize as in `AgentAllowlist` | Silent acceptance of unknown keys is the failure mode |
| Idempotent resubmit | Compare-and-skip in app code | Unique `(tenant_id, producer, producer_ref)` + state-based classification | Backs Phase 8 IR-1 idempotency too |
| Event ordering | Timestamps | `seq bigint generated always as identity` | Verified to need no sequence grant for `olive_app` |
| Problem+json | New ad hoc JSON | `ProblemResults.Result(status, reason)` plus new `ProblemReasons` constants and `Title()` cases | Static text only; `Title()` falls through to "An internal error occurred." for unknown reasons, so every new reason needs a switch case (and a test) |
| Identity comparison | UPN string equality | `IUserIdentityResolver` oid+tid | D-06 / Q14 |
| Tenant from client | Header/claim trust | Server-side map (D-08) | Q13 |

**Key insight:** every concurrency and isolation guarantee in this phase should come from the database (row lock, CHECKs, forced RLS, column grants), with app code classifying the outcome. The tests then prove it by ablation.

## Runtime State Inventory

Not a rename/refactor phase. Brief checks that matter for a forward migration:
| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Existing `proposal` rows in `awaiting_approval` (Phase 4 and earlier dev/test data) have no queue item | None for tests (fresh Testcontainers DB per test). For the shared dev DB, rows accepted before 0008 will never appear in the queue. State this in the plan summary; no backfill (greenfield POC). Optionally a one-off backfill statement in the migration is **not** recommended because the migration must stay generic and checksum-pinned |
| Live service config | None | None |
| OS-registered state | None | None |
| Secrets/env vars | No new variables. DevReviewer header name is a code constant, tenant map and kind registry are embedded resources (not env-overridable, like `agents.yaml`) | None |
| Build artifacts | New embedded resources (`config/*.yaml` glob already covered in `Olive.Catalog.csproj`; Entra project needs a new `EmbeddedResource` for reviewer/identity fixtures) | Update `Olive.Integrations.Entra.csproj` |

## Common Pitfalls

### Pitfall 1: Expiry rolls back with the error (POL-08)
**What goes wrong:** the store throws `ProposalExpiredException` inside the UoW lambda; `ExecuteAsync` never reaches `CommitAsync`, the item stays `open`, and the next call "expires" it again.
**Why:** `TenantUnitOfWork` commits only on normal return.
**Avoid:** result unions everywhere. **Detect:** the test for criterion 4 must read the DB with a separate connection after the 409 and assert proposal `expired`, item `withdrawn`/`proposal_expired`, and both event rows exist.

### Pitfall 2: Two transactions for one decision (APR-04)
**What goes wrong:** item updated, then `TransitionAsync` fails or the process dies; item `decided`, proposal still `awaiting_approval`.
**Avoid:** one `(conn, tx)`; a fault-injection test that makes the proposal CAS affect 0 rows (e.g. pre-move the proposal with an admin SQL) and asserts the item is unchanged (rolled back).

### Pitfall 3: Event order ties
**What goes wrong:** expiry writes `proposal_event` and `queue_item_event` rows in one transaction; `created_at` is identical; history order is nondeterministic.
**Avoid:** order `queue_item_event` by `seq`. (`proposal_event` has the same weakness since 0001, out of scope; do not order it by `created_at` in the new evidence/transcript code, order by insertion via a secondary key or accept `(created_at, event_id)` ties only for display.)

### Pitfall 4: Default-scheme side effects on reviewer routes
**What goes wrong:** `UseAuthentication` runs the default AgentKey scheme on every request, so a reviewer request with an `Authorization` header introspects an agent key; a mis-sent session key hits popeye on a path that has no business with it.
**Avoid:** AgentKey `NoResult` for `/v1/queue/*`; test introspector call count is zero there.

### Pitfall 5: Challenge status codes
**What goes wrong:** `AuthenticateResult.Fail` always becomes 401 through challenge; D-02 wants 403 for the other principal class.
**Avoid:** challenge overrides as in Pattern 8, with a four-case matrix test (agent key on reviewer route, reviewer header on proposals, neither, both).

### Pitfall 6: Stale classification between read and write
**What goes wrong:** service reads item (open), resolver call takes time, another reviewer claims; the write decides on stale data.
**Avoid:** `DecideAsync` re-reads `FOR UPDATE` and re-classifies; never trusts the pre-read for anything but resolver inputs.

### Pitfall 7: `Title()` fallthrough
New reasons without a `ProblemResults.Title` case silently render "An internal error occurred." Add cases and one table-driven test over all `ProblemReasons` constants (a reflection loop asserting the title is not the fallback except `InternalError`).

### Pitfall 8: Privilege-matrix tests do not auto-extend
`PrivilegeMatrixTests.Expected`, `RlsPatternTests.ExpectedTables`, and the hard-coded `["0001"…"0007"]` list in `MigrationRunnerTests.DB_02_applies_0001_as_owner_and_records_checksum` (plus an `expected8` checksum assertion) all need explicit edits. `DB_03_every_table_has_not_null_uuid_tenant_id_and_forced_rls` and `..._one_policy_without_missing_ok` discover tables dynamically from `pg_class`, so a missing RLS statement on either new table fails them immediately (good). Also add column-level tests for `queue_item` mirroring `DB_04_app_update_is_limited_to_...` and `DB_04_app_insert_is_limited_to_...` (state/created_at/claimed_*/decision* not insertable; only the listed columns updatable; tenant_id/kind/refs not updatable).

### Pitfall 9: Ablation tests need seed helpers
`TenantIsolationAblationTests.Setup` seeds proposals only, using `TestDatabase.SeedProposalAsync`. Add `SeedQueueItemAsync` to `TestDatabase` (via `olive_app` INSERT with the tenant set) and extend: cross-tenant read blocked when predicate removed; RLS-disabled ablation shows the app predicate in `PostgresQueueStore` blocks claim/decide/get cross-tenant; `AblationDatabase` takes table names, so pass `"queue_item", "queue_item_event"`.

### Pitfall 10: Docker/Windows determinism for parallel claim test
For the N-parallel claim proof use a pool with `MaxPoolSize >= N` (do not reuse `Pool1`). Use distinct reviewer fixtures so winners are distinguishable; assert `count(Claimed)==1`, `count(AlreadyClaimed)==N-1`, exactly one `claimed` event row.

### Pitfall 11: Rationale handling
Never log it; blank check must be Unicode-aware in C# (`IsNullOrWhiteSpace`) with the DB regex as a backstop only. The rationale is stored once on the item row; events carry no rationale; problem responses never echo it. Add a sentinel test (existing `SentinelScan`/`SecretLeakageTests` pattern) that a rationale sentinel does not appear in logs (`factory.Logs`), problem bodies, event `detail`, or the beat 4 transcript.

### Pitfall 12: Checks on the CONCERNS.md scaffold defects against current code
All queue defects listed there belonged to `src/Olive/Domain.cs`, which no longer exists (restructured in Phase 1). Re-expressed as requirements the new code must meet and tests must prove: approve guarded by state (CAS on `awaiting_approval`); reject path exists; decide requires claim and claimant (CHECK `decided_by = claimed_by`); approver != requester; entitlement/role enforced; no duplicate enqueue on resubmit (idempotent replay returns before insert, plus unique constraint); expired approval is a distinct error. None are "present" in current code; all must be tests.

## Code Examples

### Result union (no throw inside UoW)
```csharp
// Olive.Application
public abstract record DecideResult {
    public sealed record Decided(QueueItemRecord Item) : DecideResult;
    public sealed record AlreadyDecidedSame(QueueItemRecord Item) : DecideResult;   // 200 no-op
    public sealed record Conflict : DecideResult;                                    // 409 decision_conflict
    public sealed record NotClaimant : DecideResult;                                 // 403
    public sealed record NotClaimed : DecideResult;                                  // 409 item_not_claimed
    public sealed record Closed : DecideResult;                                      // 409 item_closed
    public sealed record ProposalExpired : DecideResult;                             // 409 proposal_expired (already committed)
    public sealed record NotFound : DecideResult;                                    // 404
}
```
Endpoint maps with an exhaustive `switch` expression; a `_` arm is a bug (add a test per arm).

### Same-tx decide core (shape)
```csharp
return await uow.ExecuteAsync<DecideResult>(tenantId, async (conn, tx) => {
    var item = await conn.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(QueueSql.GetForUpdate, new { tenant_id = tenantId, item_id = itemId }, tx, cancellationToken: ct));
    // classify per Pattern 5 steps 4-6 ...
    await conn.ExecuteAsync(new CommandDefinition(QueueSql.Decide, new { tenant_id = tenantId, item_id = itemId, decision, decided_by = actor, decided_at = now.UtcDateTime, rationale, auth_source }, tx, cancellationToken: ct));
    var n = await conn.ExecuteAsync(new CommandDefinition(ProposalSql.UpdateState, new { tenant_id = tenantId, proposal_id = item.ProposalId, from_state = ProposalStates.AwaitingApproval, to_state = target }, tx, cancellationToken: ct));
    if (n != 1) throw new InvalidOperationException("Decision compare-and-set did not affect exactly one row.");   // genuine invariant failure -> rollback -> 500
    // proposal_event + queue_item_event on the same tx
    return new DecideResult.Decided(...);
}, ct);
```
(Throwing here is correct: it is an invariant violation, not a business outcome.)

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Scaffold `ReviewQueueService` (in-memory list, no guards) | Postgres queue with CAS + forced RLS + column grants | Phase 5 | Replaces the CONCERNS defects wholesale |
| `contracts/queue-item/v0` placeholder (states `awaiting_approval/approved/rejected`, `purpose` field) | IR-1 v1 (`open/claimed/decided/withdrawn`) | Phase 5 (D-13) | Breaking relative to v0, intentional; v0 was never consumed externally |
| Decisions `approved/rejected` (v0) | `approve/reject` (action_approval), `accept/reject/defer` (identity_review) | Q10 | Proposal states stay `approved/rejected` (vocabulary), item decisions are verbs |

**Deprecated/outdated:** `contracts/queue-item/v0/` (delete). The 05-CONTEXT "Migration 0002/0003" nominal labels in ROADMAP are stale; real next number is 0008 (STATE.md "migrations are numbered sequentially").

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | action_approval `subtype` constant is `proposal` | Pattern 2 | Cosmetic; changing later needs a migration CHECK or registry edit |
| A2 | `expires_at <= now` counts as expired (equality = expired) | Pattern 4 | Off-by-one at the boundary; pin with a test and confirm with spec owner |
| A3 | Self-approval resolution runs for `approve` only, not `reject` | Pattern 5 | If APR-02 is meant for every decision, reject needs the same check; low harm either way |
| A4 | `decided_by`/`claimed_by` store the reviewer's Entra `oid` | Pattern 5 | If IR-1 consumers (bluto) expect UPN/display name, a mapping layer is needed at Phase 8 |
| A5 | Default list filter is `state=open` when none supplied | Pattern 6 | UI (Phase 7) may want claimed items too; additive to change |
| A6 | Re-claim by the current claimant returns 409 `already_claimed` (no idempotent re-claim) | Pattern 4 | Client retry ergonomics only |
| A7 | `identity_resolution_unavailable` is a 503 (not 403) | Pattern 9 | D-06 only demands "refused"; either satisfies |
| A8 | Payload endpoint requires authenticated + tenant-mapped reviewer only (no role gate), since D-09/APR-03 place the role check at decide time | Pattern 8 | Parameters would be visible to a non-supervisor reviewer in the tenant; if undesired, add the kind role check to the payload endpoint |
| A9 | Decision events carry no rationale; rationale only on the item row | Pattern 5 | Audit consumers wanting rationale in history must join to the item |
| A10 | Entra fixture project is the right home for reviewer/identity fixtures (matches `FixtureDirectoryGroupResolver`) | Structure | None functionally |

## Open Questions

1. **Should `reject` also require identity resolution?**
   - Known: APR-02 and D-06 speak of "approval"/"approver".
   - Unclear: whether reject by the requester is a concern.
   - Recommendation: approve only (A3); one-line change if the user disagrees.
2. **Registry-vs-catalog role duplication.**
   - Known: catalog `required_approval` is per action; D-09 puts `required_role` on the kind.
   - Recommendation: keep kind-level as the enforced source and add a startup equality check against all catalog entries.
3. **Does the reviewer need a role to read the payload?** (A8) Needs a product call before Phase 7 UI; no rework cost either way.
4. **Release/claim of already-expired but not yet withdrawn items** is handled by the lazy path (claim, release and decide all take the expiry path). Confirm release should also trigger lazy expiry; recommended yes for consistency (QUE-10).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.x per `global.json` | — |
| Docker Desktop | Testcontainers (postgres:17-alpine) | yes | 29.7.2 (confirmed `docker info`) | — |
| PostgreSQL client (psql) | optional manual checks | yes | 18 | — |
| Shared dev Postgres `localhost:5432` | not used; tests must refuse it (`NoSharedPostgres`) | n/a | — | — |

No missing dependencies. The three Postgres behaviours below were exercised on `postgres:17-alpine` during this research: identity column insert needs no sequence grant for a role holding only table INSERT; `SELECT … FOR UPDATE` succeeds with a column-level UPDATE grant under forced RLS; `now()` is not `clock_timestamp()` within a transaction. [VERIFIED: ran in this session]

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0 (postgres:17-alpine), Mvc.Testing 10.0.13 |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration|Architecture|Unit`) |
| Quick run command | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| Full suite command | `dotnet test tests/Olive.Tests` (needs Docker); CI also runs `dotnet restore --locked-mode` |
| Demo command | `bash build/demo-beat4.sh` (`dotnet test tests/Olive.Tests --filter "Demo=beat4"`) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| QUE-01 | `queue_item` refuses inline payload / wrong scheme / cross-kind scheme (23514); v1 schema valid/invalid pair incl. inline-payload invalid | integration + unit | `--filter "FullyQualifiedName~QUE_01"` | Wave 0: `QueueSchemaTests.cs`, `QueueTableTests.cs`; update `ContractSchemaTests.Folders` |
| QUE-02 | registry loads both kinds; unknown key/duplicate kind/missing role rejected; no mutator member; DB CHECK drift test; `link_challenge` absent | unit + integration | `--filter "FullyQualifiedName~QUE_02"` | Wave 0: `QueueKindRegistryTests.cs` |
| QUE-03 | accepted submit creates exactly one open item + `created` event in the same tx; fault between proposal insert and item insert leaves neither; replay creates none; refused (trace, purpose, rate) create none | integration | `--filter "FullyQualifiedName~QUE_03"` | Wave 0: `QueueEnqueueTests.cs` |
| QUE-04 | tenant-only list; expired filtered; list performs no write (item still `open` after list past expiry); kind/state filters; response validates v1 schema | integration | `--filter "FullyQualifiedName~QUE_04"` | Wave 0: `QueueListTests.cs` |
| QUE-05 | N parallel claims across distinct reviewers: 1 winner, N-1 `already_claimed`, 1 `claimed` event | integration | `--filter "FullyQualifiedName~QUE_05"` | Wave 0: `QueueClaimTests.cs` |
| QUE-06 | claimant releases (item open, `released` event, other reviewer can claim); non-claimant 403 `not_claimant`; release of open item 409 | integration | `--filter "FullyQualifiedName~QUE_06"` | Wave 0 |
| QUE-07 | non-claimant decide 403; decide without claim 409; blank/whitespace/Unicode-space rationale 400; empty actor rejected at service; DB CHECK rejects decided without rationale/decided_by | integration + unit | `--filter "FullyQualifiedName~QUE_07"` | Wave 0: `QueueDecisionTests.cs` |
| QUE-08 | same decision resubmitted → 200, same body, event count unchanged; conflicting decision 409 `decision_conflict`; concurrent double submit leaves one `decided` event | integration | `--filter "FullyQualifiedName~QUE_08"` | Wave 0 |
| QUE-09 | full history ordered by `seq`: created, claimed, released, claimed, decided; withdrawal event; olive_app cannot UPDATE/DELETE `queue_item_event`; audit endpoint is tenant-scoped | integration | `--filter "FullyQualifiedName~QUE_09"` | Wave 0 |
| QUE-10 | claim/decide past expiry: item `withdrawn`/`proposal_expired`, proposal `expired`, 4 events committed (check from separate connection); claimed-then-expired follows same path and keeps claim event; repeat call still `proposal_expired` | integration (ManualTimeProvider) | `--filter "FullyQualifiedName~QUE_10"` | Wave 0: `QueueExpiryTests.cs` |
| APR-01 | no/unknown reviewer header → 401; unauthenticated cannot list/claim/decide | integration | `--filter "FullyQualifiedName~APR_01"` | Wave 0: `ReviewerAuthTests.cs` |
| APR-02 | on_behalf_of's own oid+tid refused; resolver NotFound/Ambiguous/Malformed → `approval_identity_unresolved`; Unavailable/faulting → fail closed 503; UPN lookalike with different oid allowed; same oid different UPN refused; resolver not called for expired/non-claimant | integration + unit | `--filter "FullyQualifiedName~APR_02"` | Wave 0: `SelfApprovalTests.cs`; fixtures + `RecordingUserIdentityResolver` + faulting double |
| APR-04 | approve/reject move item + proposal together; forced proposal-CAS miss rolls the item back; proposal events carry actor | integration | `--filter "FullyQualifiedName~APR_04"` | Wave 0 |
| APR-05 | decided row and events record `auth_source=stub`; header cannot override it; CHECK rejects other values | integration | `--filter "FullyQualifiedName~APR_05"` | Wave 0 |
| POL-08 | approve of expired proposal → 409 `proposal_expired` and DB shows expiry committed; later approve of the same item same reason; `proposal_event` transition present | integration | `--filter "FullyQualifiedName~POL_08"` | Wave 0 |
| EVD-03 | beat 4: refusal 201 body, stored row, `policy_evaluated` event, `queue_item` count 0, reviewer list excludes it; transcript has no parameter values | integration (Demo) | `bash build/demo-beat4.sh` | Wave 0: `Beat4DemoTests.cs`, `build/demo-beat4.sh` |
| D-02 | 4-case caller matrix (agent→reviewer 403, reviewer→proposals 403, none 401, both) + zero introspection calls on reviewer routes | integration | `--filter "FullyQualifiedName~Caller_separation"` | Wave 0 |
| D-08 | zero/multiple tenants → 403 `reviewer_tenant_unresolved`; tenant query/header/body ignored; tenant-map YAML strict-parse failures | unit + integration | `--filter "FullyQualifiedName~ReviewerTenant"` | Wave 0 |
| D-09 | decide by reviewer lacking `role:case_supervisor` → 403 `role_not_permitted`; claim and list allowed without role | integration | `--filter "FullyQualifiedName~Role"` | Wave 0 |
| Q17 | production start refused with `FixtureReviewerIdentitySource`/`FixtureUserIdentityResolver` registered (FND-05); DevReviewer scheme absent check | integration | `--filter "FullyQualifiedName~FND_05"` | extend `StartupGuardTests.cs` |
| DB-03/04 | RLS pattern, privilege matrix and column-grant tests extended to `queue_item`, `queue_item_event` (see Pitfall 8/9); ablation tests for the new tables | integration | `--filter "FullyQualifiedName~DB_0"` | extend `PrivilegeMatrixTests`, `RlsPatternTests`, `MigrationRunnerTests`, `TenantIsolationAblationTests`, `TestDatabase` |
| Sentinel | rationale/parameters never in logs, problems, event detail, transcript | integration | `--filter "FullyQualifiedName~Sentinel"` | extend `SecretLeakageTests` pattern |
| FND-01/02 | no new project, no new package, no HTTP assembly in new code | architecture | `--filter "Category=Architecture"` | existing, must stay green |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Olive.Tests --filter "Category!=Integration"` (~seconds)
- **Per wave merge:** `dotnet test tests/Olive.Tests` (full, Docker; baseline ~750 tests, ~30 s)
- **Phase gate:** full suite green plus `dotnet restore --locked-mode` and `bash build/demo-beat4.sh`, before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `sql/migrations/0008_review_queue.sql` and the test edits it forces (Pitfall 8): `MigrationRunnerTests` list/checksum, `PrivilegeMatrixTests.Expected` + new column tests, `RlsPatternTests.ExpectedTables`, ablation tests, `TestDatabase.SeedQueueItemAsync`
- [ ] New test files listed above; `FaultingDoubles.cs`/`RecordingPorts.cs` additions for `IUserIdentityResolver`; `OliveApiFactory` additions: reviewer-header client helper (`CreateReviewerClient(id)`), registration of recording/faulting `IUserIdentityResolver`, a configurable `TimeProvider` (already supported via `configureServices`)
- [ ] Fixtures: `contracts/reviewer/` (identities: two alpha reviewers with `role:case_supervisor`, one alpha without the role, one self-approver whose oid matches `analyst.alpha@example.test`, one beta reviewer, one unmappable/two-tenant reviewer), user-identity fixtures including an ambiguous one; each with schema + valid/invalid example
- [ ] `config/queue-kinds.yaml`, `config/reviewer-tenants.yaml`; embedded-resource lines in `Olive.Integrations.Entra.csproj`
- [ ] `contracts/queue-item/v1/*`; delete v0; `ContractSchemaTests.Folders`; `CLAUDE.md` v0 references

## Security Domain

### Applicable ASVS Categories (level 1, `security_enforcement` on)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | DevReviewer scheme (stub, non-production only); Entra OIDC in Phase 7 behind the same claims; fail closed on unknown id |
| V3 Session Management | no (stateless header stub; cookie/`max_age` is Phase 7) | — |
| V4 Access Control | yes | Role gate from registry at decide; claimant-only release/decide (row lock + CHECK); tenant from server-side map; app predicate + forced RLS; cross-tenant item = 404 |
| V5 Input Validation | yes | Registry-validated `kind`/`state`/`decision` filters; rationale length and blank rules; unknown JSON fields rejected (`invalid_request`); route ids parsed as `Guid` |
| V6 Cryptography | no new use | None |
| V7 Logging | yes | No rationale/parameters/oid-to-UPN mappings in logs; static reason codes only |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Reviewer spoofing the tenant | Spoofing/Elevation | Tenant only from the server-side group map (D-08); request-supplied tenant ignored and tested |
| Self-approval (requester approves own proposal) | Elevation | oid+tid resolution (D-06), fail closed on missing/ambiguous |
| Agent key used on reviewer API / reviewer on proposal API | Elevation | Hard scheme separation + 403 matrix (D-02) |
| Stub identity in production | Spoofing | `INonProductionOnly` fixture source + startup guard + explicit scheme check (Q17) |
| Double decision / race | Tampering | Row lock + state CHECKs + guarded proposal CAS in one tx |
| Audit tampering | Tampering/Repudiation | `queue_item_event` INSERT-only for `olive_app` (no UPDATE/DELETE/TRUNCATE), forced RLS |
| Cross-tenant read through event/audit/payload endpoints | Information disclosure | Every statement keeps `tenant_id = @tenant_id`; ablation tests on both layers |
| Inline body smuggled into a ref | Tampering/Disclosure | Kind-scoped regex CHECK (anchored, fixed shape); registry validator; v1 schema invalid example |
| Info leak through errors | Disclosure | Static problem titles, 404 for cross-tenant, no rationale/param echo |
| Session key reaching a non-popeye component on reviewer routes | Disclosure | AgentKey handler `NoResult` on `/v1/queue/*`; zero-introspection test |

## Sources

### Primary (HIGH confidence)
- Codebase (read in full for this research): `src/Olive.Persistence/{PostgresProposalStore,ProposalSql,TenantUnitOfWork}.cs`, `src/Olive.Application/{IProposalStore,INonProductionOnly,OutboundPortAttribute,IDirectoryGroupResolver}.cs`, `src/Olive.Domain/ProposalStates.cs`, `src/Olive.Host/{Program.cs,Auth/*,Startup/StartupGuards.cs,StartupGuardFilter.cs,Endpoints/ProposalEndpoints.cs,Problems/*}`, `src/Olive.Catalog/{AgentAllowlist,CatalogLoader,CatalogModels}.cs`, `src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs`, `sql/migrations/0001..0007`, `sql/bootstrap/001_roles.sql`, `tests/Olive.Tests/{PrivilegeMatrixTests,RlsPatternTests,TenantIsolationAblationTests,AblationDatabase,TestDatabase,OliveApiFactory,ArchitectureTests,StartupGuardTests,ExpiryTests,MigrationRunnerTests,ContractSchemaTests,RecordingPorts,TestDoubles}.cs`, `.github/workflows/ci.yml`, `Directory.Packages.props`
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (IR-1, ratified 2026-10-21), `05-CONTEXT.md`, `REQUIREMENTS.md`, `ROADMAP.md`, `STATE.md`, `CONCERNS.md`
- Live experiment on postgres:17-alpine (docker, this session): identity-column insert without sequence grant; `SELECT … FOR UPDATE` with column-level UPDATE under forced RLS; `now()` vs `clock_timestamp()` within a transaction
- PostgreSQL docs, SELECT locking clause privilege requirement [CITED: postgresql.org/docs/current/sql-select.html]

### Secondary (MEDIUM confidence)
- ASP.NET Core multiple-scheme behaviour (`AuthorizeAttribute.AuthenticationSchemes`, default scheme authenticated by `UseAuthentication`, challenge/forbid overrides) from framework knowledge; not re-verified against docs in this session, so the planner should include a Wave-0 spike test for the 4-case caller matrix before building on it.

### Tertiary (LOW confidence)
- None.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH, no changes and all pinned versions read from `Directory.Packages.props`
- Architecture: HIGH for persistence, migration and service layering (seams read directly, Postgres behaviours executed); MEDIUM for the exact ASP.NET Core challenge/`NoResult` mechanics in Pattern 8 (de-risked by the Wave-0 matrix test)
- Pitfalls: HIGH, drawn from concrete code (UoW commit semantics, `TransitionAsync` own-transaction, `Title()` fallthrough, hard-coded migration list)

**Research date:** 2026-10-23
**Valid until:** 2026-11-22 (stable; invalidated early by any change to IR-1 or Decision §1–§3)
