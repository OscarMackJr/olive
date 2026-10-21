# Codebase Concerns

**Analysis Date:** 2026-10-21

Context: the repo is an explicit scaffold (`AGENTS.md`, `docs/STATUS.md`). All domain logic lives in one file, `src/Olive/Domain.cs` (129 lines), with in-memory stores. Tests are in `tests/Olive.Tests/RequirementsTests.cs`. The Postgres DDL in `db/migrations/V001__action_broker.sql` is not used by any code. Several concerns below are expected scaffold gaps; they are listed so they are not mistaken for done work.

## Tech Debt

**All persistence is in-memory; the SQL migration is unconnected:**
- Issue: `ActionBroker._proposals`, `ReviewQueueService._items`, `SimulatedActionExecutor._executions` and `PolicyEngine._attempts` are plain `Dictionary`/`List`/`Queue` fields. Nothing reads or writes `action_broker.*` tables. There is no Npgsql/Dapper dependency (see `src/Olive/Olive.csproj`).
- Files: `src/Olive/Domain.cs`, `db/migrations/V001__action_broker.sql`
- Impact: Decision v0.5 §10 says in-memory stores are for unit tests only and requires Postgres from Phase 1. State is lost on restart. Idempotency, rate limits and expiry do not survive a process boundary.
- Fix approach: Add repository interfaces (`IProposalStore`, `IQueueStore`, `IExecutionLedger`) with a Postgres implementation. Set `olive.tenant_id` per transaction and use SERIALIZABLE for multi-row writes, as the migration footer says.

**No thread safety anywhere:**
- Issue: Non-concurrent `Dictionary`/`List` and unsynchronised rate-limit queues. `ActionBroker.Execute` is read-modify-write on `_proposals` and the executor with no locking.
- Files: `src/Olive/Domain.cs` (`PolicyEngine.Evaluate`, `ReviewQueueService.Update`, `SimulatedActionExecutor.Execute`)
- Impact: Concurrent use corrupts state or double-writes the JSONL. REQ-ACT-03 (idempotent execution) holds only single-threaded.
- Fix approach: Enforce idempotency with DB unique constraints (`executions` has `unique (tenant_id, proposal_id)`); remove in-process state.

**Hand-rolled YAML parsing in `Catalog`:**
- Issue: `Catalog.Load`/`Parse` use regex and `StartsWith` line scans instead of a YAML parser or the JSON schema. Vocabulary purposes are extracted with `^  - [a-z-]+$`, which matches any 2-space-indented list item anywhere in `contracts/vendored/vocabulary.yaml`, not only the `purposes:` block. `Value("required_purpose")` requires inline `[a, b]` syntax. `limit:` and `window_seconds:` are found by first-match, so nesting is ignored. Parameter schemas under `parameters:` are never loaded.
- Files: `src/Olive/Domain.cs` (lines ~28-45), `catalog/crm.case_note@1.yaml`
- Impact: The vocabulary check can wrongly accept values (for example, data class or other lists with lowercase hyphenated names such as `public`, `internal`). A reformatted catalog file fails with an opaque `InvalidOperationException` ("Sequence contains no matching element"). `contracts/schemas/action-type.schema.json` is not applied to catalog files. A malformed catalog file crashes startup.
- Fix approach: Use a YAML library (YamlDotNet) and read `purposes:` as a key. Validate catalog files against `action-type.schema.json`. Compile `parameters` into a validator.

**Catalog filename and id are not cross-checked; `Catalog.Load` globs only `*.yaml`:**
- Issue: The file `catalog/crm.case_note@1.yaml` is never compared with its `action_id`. Duplicate ids make `ToDictionary` throw. The vocabulary version and `required_approval` role are never checked.
- Files: `src/Olive/Domain.cs`
- Fix approach: Assert filename equals `action_id + ".yaml"` and validate `required_approval`.

**Whole domain in one file; stringly-typed states:**
- Issue: Models, catalog, policy, queue, executor and broker share `src/Olive/Domain.cs`. Outcomes and states are `string` constants rather than enums; policy outcomes are compared to the literal `"allowed"`.
- Impact: Typos compile; hard to split by owner as the API, Popeye client and persistence arrive.
- Fix approach: Split into files per component and use enums or closed types for state.

**Dense one-line test and code style:**
- Issue: Test methods in `tests/Olive.Tests/RequirementsTests.cs` and several broker methods are multiple statements on one line (for example `Approve`, `Execute`).
- Impact: Hard to review and debug; poor failure localisation.

## Known Bugs

**Expiry state transition is not persisted by a thrown path, and approval of an expired proposal returns a non-error:**
- Symptoms: `ActionBroker.Approve` on an expired proposal returns the proposal with `State = expired` and does not throw; it does not touch the queue item, which stays `awaiting_approval`. `Execute` throws `InvalidOperationException("Proposal expired.")` with no distinct error type. Decision v0.5 §9 says expired proposals return a distinct error on approve and execute.
- Files: `src/Olive/Domain.cs` (`ActionBroker.Approve`, `ActionBroker.Execute`)
- Trigger: Approve after `ExpiresAt`.
- Workaround: None.

**Approve has no state guard (double or contradictory approval):**
- Symptoms: `Approve` does not check `proposal.State == awaiting_approval`. A `refused_policy`, `rejected` or already `executed` proposal reaching `Approve` fails only because `queue.List(...).Single(...)` throws `InvalidOperationException` when no queue item exists (refused) or if more than one matches. An approved proposal can be approved again by another reviewer, overwriting `Approver`; an executed proposal can be re-approved, setting state back to `approved`, which then permits re-execution of state transitions.
- Files: `src/Olive/Domain.cs` (`ActionBroker.Approve`)
- Fix approach: Guard on current state; reject with a typed error. There is no reject path on `ActionBroker` at all (queue supports `Rejected`, broker never sets `Outcomes.Rejected`).

**`Queue.Decide`/`Claim` have no state or concurrency checks:**
- Symptoms: Decided items can be decided again, claimed by a second reviewer, or decided without being claimed. No check that the decider is entitled (`role:case_supervisor` from `catalog/crm.case_note@1.yaml` `required_approval` is parsed but never enforced). Approver may equal the proposer (no separation of duties).
- Files: `src/Olive/Domain.cs` (`ReviewQueueService`, `ActionType.RequiredApproval`)

**Rate limit consumed by refused proposals and by retries:**
- Symptoms: `PolicyEngine.Evaluate` evaluates all three checks without short-circuit and enqueues an attempt timestamp even when purpose or delegation refused. A refused proposal burns rate budget. The key is `(principal.Id, action)` with no tenant, so tenants share a limiter if a principal id repeats.
- Files: `src/Olive/Domain.cs` (`PolicyEngine`)

**`Delegation` check semantics diverge from the spec:**
- Symptoms: For non-agent principals with null `Grants`, `Delegation` returns `true` (allow by default). For agents it requires both grant sets. Decision v0.5 §7/§17 require Popeye introspection and entitlement intersection; here grants are caller-supplied in the proposal. `Principal.Grants` is trusted input.
- Files: `src/Olive/Domain.cs`
- Impact: Fail-open default for users; with real API wiring a caller could assert its own grants.
- Fix approach: Fail closed (null means deny); source grants only from introspection.

**Same action refused vs. unknown action:**
- Symptoms: `Catalog.Get` throws `InvalidOperationException` for an unknown action id from `Submit`. Spec §15.2 says an unknown catalog action is stored as `refused_policy`. Submit currently crashes instead.
- Files: `src/Olive/Domain.cs` (`ActionBroker.Submit`)

**`ExpiresAt` and `parameters` are not validated on submit:**
- Symptoms: `Submit` never validates `Parameters` against the action's parameter schema, never caps expiry (§9 default 8h, catalog max), and never checks `ExpiresAt` in the past at submit time. `proposal.valid.json` and `proposal.schema.json` exist, but no code calls them (only the queue-item schema is used by a test).
- Files: `src/Olive/Domain.cs`, `contracts/schemas/proposal.schema.json`, `catalog/crm.case_note@1.yaml`

**Re-submitting the same `ProposalId` overwrites it:**
- Symptoms: `_proposals[saved.ProposalId] = saved` and a fresh queue item is added each time. There is no idempotency on `(tenant, agent_id, run_id)` (spec §15.2) and no uniqueness on `ProposalId`. A resubmit of an approved/executed proposal resets it to `awaiting_approval` and enqueues a duplicate item, so `Approve`'s `.Single(...)` then throws.
- Files: `src/Olive/Domain.cs` (`ActionBroker.Submit`)

**Executor writes before recording; failure path missing:**
- Symptoms: `SimulatedActionExecutor.Execute` appends to JSONL, then records in `_executions`. A crash in between yields a duplicate on retry (not atomic). Outcome is always `executed`; `execution_failed` is never produced. `ActionBroker.Execute` sets `proposal.State = execution.Outcome` without persisting a ledger or outbox fact. The proposal `Get` also returns `Executed` state proposals as executable again (`Approved or Executed` passes), relying only on the executor's dictionary.
- Files: `src/Olive/Domain.cs`

## Security Considerations

**Tenant predicates and RLS (AGENTS.md constraint): application layer present, DB layer unwired, and the "RLS" test is a tautology:**
- Risk: `ReviewQueueService.ListWithDatabaseRls` is an in-memory `Where(TenantId == sessionTenant)` that mimics RLS. `REQ_QUE_02_database_rls_scopes_when_application_predicate_is_removed` therefore verifies a LINQ filter, not Postgres. The real policies in `db/migrations/V001__action_broker.sql` are never exercised by any test or code path.
- Files: `src/Olive/Domain.cs`, `tests/Olive.Tests/RequirementsTests.cs`, `db/migrations/V001__action_broker.sql`
- Current mitigation: Application predicates exist in `List`, `Update`, `Audit` and `ActionBroker.Get`. `Approve` and `Execute` write `_proposals[proposalId]` keyed by id only, after the tenant-checked `Get`.
- Recommendations: Add Testcontainers/Postgres integration tests that connect as a non-owner role, set `olive.tenant_id`, drop the app predicate, and assert isolation. Create the application role: the migration has no `CREATE ROLE`, no `GRANT`, and no append-only grant on `executions` (Decision v0.5 §10/§13 require append-only ledger grants). `force row level security` applies to the table owner but superusers and `BYPASSRLS` roles still bypass it; the connection role must be non-superuser.

**RLS policy gaps in the migration:**
- Risk: `current_tenant_id()` returns NULL if the setting is missing, so the policy denies all (safe), but `::uuid` cast of a malformed setting raises an error. Policies are `FOR ALL` with no per-command split, so UPDATE and DELETE are allowed on `executions`, `queue_items` and `outbox_facts` (the ledger is meant to be append-only). No immutability trigger for decided queue items. `idempotency_records` and `outbox_facts` have no tenant-format check against the vocabulary regex. `proposals.parameters` is `jsonb` storing inline parameter bodies (see next item).
- Files: `db/migrations/V001__action_broker.sql`

**Reference-only payloads (AGENTS.md constraint): enforced only by JSON schema, not by the domain or the DB:**
- Risk: `QueueItem.PayloadRef` is a free string; `ReviewQueueService.Add` does no validation. The migration only checks `payload_ref <> ''`. `queue-item.schema.json` uses `additionalProperties:false` and is exercised by one test with a fixed invalid example. A caller can place a record body in `PayloadRef` or `SubjectRef` as a string, and nothing detects it. `Proposal.Parameters` (`JsonElement`) and `action_broker.proposals.parameters jsonb` store parameter bodies inline; the catalog uses `case_ref` and `note_ref`, which are references, but the broker never enforces that parameters are references.
- Files: `src/Olive/Domain.cs`, `db/migrations/V001__action_broker.sql`, `contracts/schemas/queue-item.schema.json`, `catalog/crm.case_note@1.yaml`
- Recommendations: Validate `payload_ref` and `subject_ref` against a URI-scheme allowlist (`proposal://`, `bluto:`...), and validate proposal parameters against the catalog schema at submit.

**Session-key introspection and trace verification are absent:**
- Risk: Decision v0.5 §7, §14-§17 and `specs/PROMPT_OLIVE.md` require Popeye introspection, agent allowlist, trace resolution with proposal binding, tenant-scoped service tokens. None exists. `Proposal.TenantId` and `Principal` are accepted from the caller object unchallenged; `TraceRef` is an unchecked string.
- Files: `src/Olive/Domain.cs`
- Current mitigation: No network surface exists at all (no API project).
- Recommendations: Block any API exposure until introspection exists; tenant must come from the validated credential, never the body (also required by IR-1 §4 for the queue).

**"No write connector" test is weak:**
- Risk: `REQ_ACT_04_no_external_write_connector_exists` only asserts `Domain.cs` lacks the substring `HttpClient`. A new file, `WebRequest`, `Npgsql` writes, `Process.Start` or a new project would pass. It also does not satisfy the cross-repo credential audit that REQ-ACT-04 requires.
- Files: `tests/Olive.Tests/RequirementsTests.cs`
- Recommendations: Scan all of `src/` for network namespaces and assert that `Olive.csproj` has no `PackageReference` to HTTP/SDK packages. Keep the AGENTS.md rule: no real executor until a signed divisional write agreement and a scoped credential exist.

**Policy detail strings and logging:**
- Risk: `PolicyDecision.Detail` texts are static (tested by `policy_decision_detail_never_contains_parameter_value`). Exception messages never echo values. Keep that. The simulated executor writes `proposal_id` and `action_id` to a JSONL file at a caller-supplied path with no path validation.
- Files: `src/Olive/Domain.cs`

**Catalog runtime registration (AGENTS.md constraint): satisfied, with a loophole:**
- Risk: `Catalog.AddAtRuntime` is a method that only throws, preserving REQ-ACT-06. The constructor is private and `_items` is private readonly, so there is no registration path today. The throwing stub is itself a public API surface that invites a later "implementation". `Catalog.Load` takes any directory, so tests or callers can load unreviewed catalogs; there is no CODEOWNERS or CI check that `catalog/` changes need review.
- Files: `src/Olive/Domain.cs`, `catalog/`
- Recommendations: Remove `AddAtRuntime` once the test is rewritten to assert that no public mutator exists (reflection test), and add review rules for `catalog/`.

**Vendored vocabulary integrity is not verified:**
- Risk: `contracts/vendored/VENDORED.md` records a SHA-256, but no test or CI step recomputes it. AGENTS.md forbids edits to `contracts/vendored/vocabulary.yaml`; nothing enforces that. The vocabulary's own comment says conformance is by hash.
- Files: `contracts/vendored/VENDORED.md`, `contracts/vendored/vocabulary.yaml`
- Recommendations: Add a hash-check test; also add a CI guard.

## Performance Bottlenecks

**Linear scans in queue and approve path:**
- Problem: `ReviewQueueService.List` copies and filters the whole list for every call; `Approve` calls `queue.List(tenantId).Single(...)`; `Update` uses `FindIndex`. Rate limiting stores one timestamp per attempt.
- Files: `src/Olive/Domain.cs`
- Cause: In-memory list scaffold. Irrelevant at present; resolves itself when backed by indexed Postgres queries (primary key `(tenant_id, item_id)`). Note the migration lacks an index for `queue_items (tenant_id, state, kind)` and `(tenant_id, proposal_id)` lookups, and none for polling by `decided_at` (IR-1 §6 cursor).

## Fragile Areas

**Test path resolution:**
- Files: `tests/Olive.Tests/RequirementsTests.cs` (`Root`)
- Why fragile: `Root` is `AppContext.BaseDirectory` plus five `..` segments, which depends on `bin/<config>/<tfm>/` depth. Changing the TFM layout, `Directory.Build.props` output paths or running from a different artifacts folder breaks every test.
- Safe modification: Walk up to find `Olive.sln`.

**Test tautologies and low-signal assertions:**
- Files: `tests/Olive.Tests/RequirementsTests.cs`
- REQ_QUE_04 ("bluto ambiguous match routes to shared queue") only adds an item with kind `bluto_identity_review` and reads it back, so it proves nothing about routing. REQ_QUE_02 is described above. The kind string `bluto_identity_review` conflicts with the ratified IR-1 contract (`identity_review`).

**Fixed vocabulary tenant GUIDs hard-coded in tests:**
- Files: `tests/Olive.Tests/RequirementsTests.cs`
- These are the vocabulary POC tenants and are intentionally permanent; leave as is, but read them from the vendored YAML to avoid drift.

**.NET SDK pin:**
- Files: `global.json`
- `10.0.204` with `rollForward: latestFeature`. Contributors without that SDK fail to build; no CI workflow exists (`.github/` absent) to catch this.

## Spec versus Scaffold Gaps

**Untracked specs and contract not committed:**
- Files: `specs/DECISION_olive-poc2-phase1_v0.1.md`, `specs/DECISION_olive-poc2-phase1_v0.5.md`, `specs/PROMPT_OLIVE.md`, `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (all untracked per `git status`).
- Impact: The ratified authority documents (IR-1 states it is ratified 2026-10-21 and authoritative at olive's path) are not under version control. `specs/README.md` and `docs/STATUS.md` do not reference them. Decision v0.1 is superseded by v0.5 (amendments v0.2-v0.5 are appended in the same file) but both sit in `specs/`, risking implementation against the stale one.
- Fix approach: Commit them; mark v0.1 as superseded (or move to an archive); index in `specs/README.md`; add IR-1 to `contracts/vendored` hash practice for bluto.

**`docs/STATUS.md` is stale against the decision record:**
- It lists the language/stack and queue front end as open charter §6 decisions, whereas Decision v0.5 §10-§13 sets Postgres, Entra OIDC and Flyway-style migrations (`V001__...`). It does not mention IR-1, popeye or hometown dependencies, or the no-tenant-records open item.

**Queue-item contract vs. IR-1 mismatch (largest functional gap):**
- Files: `contracts/schemas/queue-item.schema.json`, `contracts/examples/queue-item.*.json`, `db/migrations/V001__action_broker.sql`, `src/Olive/Domain.cs`
- IR-1 (`contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`) specifies:
  - states `open -> claimed -> decided | withdrawn`; code, schema and DB check use `awaiting_approval | approved | rejected`.
  - decisions `accept | reject | defer` with `defer` returning to `open` and keeping history; code and DB allow only `approved | rejected` and have no decision-history table (the `queue_items` row is overwritten on each update).
  - extra fields `subtype`, `producer`, `producer_ref`, `ruleset_version`, `withdraw` reason code; none exist in `QueueItem`, the schema (`additionalProperties:false` would reject them) or the DB.
  - `purpose` must be absent for identity items, but `queue_items.purpose` is nullable, the schema's `purpose` is an enum, and `QueueItem.Purpose` is nullable. Nothing is wrong yet, but nothing enforces the kind-to-purpose rule.
  - `kind = identity_review` versus the test's `bluto_identity_review`.
  - server-assigned fields (`tenant_id`, `producer`, `item_id`, `state`) must be rejected in create bodies with `400 server_assigned_field`; the queue-item schema requires `tenant_id` in the body, which is the opposite.
  - idempotency key `(producer, tenant_id, producer_ref)` with RFC 8785 canonical body: no unique constraint, no canonicalisation code. `queue_items` has no `producer`/`producer_ref` columns.
  - `role:identity_steward` entitlement check, polling endpoint with `decided_after` cursor, `link_challenge` rejection: none implemented.
  - IR-1 §4 says no expiry for identity items; DB has none either, but `ActionBroker` is the only expiry path.
- Fix approach: Generalise the queue state machine (kind-specific state sets or map `open/claimed/decided`), add a `queue_item_events` history table, add the producer columns plus a unique index, and make the API layer assign tenant from the credential.

**Charter/Decision features with no scaffold counterpart:**
- Missing: an HTTP proposal API and wire contract (`PROMPT_OLIVE.md` first deliverable); Popeye introspection and token client; hometown trace resolution (Decision §14-§17) including `trace_verification` and proposal binding; idempotency on `(tenant, agent_id, run_id)` with canonical body hash (§15.2-§15.3, 409 `idempotency_conflict`); proposal expiry default 8h with `expires_in_seconds` and catalog maximum (§9); Entra OIDC reviewer authentication and approver role checks (§8); outbox publishing (the `integration_outbox.outbox_facts` table is created but nothing writes it); a queue front end (§11); local connection variable conventions (§15.4); credential interface with vault reference (§2).
- Status: `proposals` table has no `agent_id`/`run_id` column, so the unique `(tenant_id, agent_id, run_id)` constraint required by §15.2 cannot exist yet; `trace_verification` is not stored; `idempotency_records` keys on an arbitrary text key, which does not match `(agent_id, run_id)`.
- Domain `Proposal` lacks `agent_id`, `run_id`, `justification`, `party_ref`, `nono_session`, which `PROMPT_OLIVE.md` lists as required request fields.

**Contract fixtures vs. code:**
- Files: `contracts/schemas/proposal.schema.json`, `contracts/schemas/execution-record.schema.json`, `contracts/schemas/action-type.schema.json`, `contracts/examples/*`
- Only `queue-item` schema is used in a test. Proposal, execution-record and action-type schemas and their valid/invalid examples are not validated anywhere, and the schemas are single-line minified JSON that can drift silently from the C# records. `execution_failed` appears in the DB check for outbox publish state and executions but the C# executor never emits it.

## Scaling Limits

**Single-process, single-file executor output:**
- Current capacity: One JSONL file appended by one process.
- Limit: Multiple instances duplicate execution because idempotency is process-local.
- Scaling path: DB-backed execution ledger with unique `(tenant_id, proposal_id)` and an outbox.

## Dependencies at Risk

**Preview .NET SDK/Version:**
- Risk: `global.json` pins SDK `10.0.204`. Verify this is a released SDK in your environment and that it is available to build agents.
- Impact: Build failure on contributors and CI machines lacking it.
- Migration plan: Keep `rollForward` and document the SDK in `README.md`.

## Missing Critical Features

**Real execution path, API, authN/Z, persistence:**
- Problem: No API host, no authentication, no database access, no CI. By design, no real executor until the signed divisional write agreement and a scoped credential exist (`AGENTS.md`, charter §3.3); outstanding entry criteria in `docs/STATUS.md`: boundary approval, write agreement, target system, scoped credential.
- Blocks: Phase 1 demo beats (REQ-ACT-01 chain "queryable from the execution record" needs persistence), bluto integration (IR-1 gates in §8), POC-2.

## Test Coverage Gaps

**Negative and state-machine paths:**
- What's not tested: re-approval, approve of refused or executed proposal, reject flow, double claim, decision by non-entitled approver, unknown action id, expired approval returning a distinct error, rate-limit exhaustion (and refusal burning budget), duplicate `Submit` of the same id, concurrent execution, malformed catalog files, catalog and vocabulary loader edge cases, grant fail-open for null `Grants`, agent delegation with an agent `OnBehalfOf`.
- Files: `tests/Olive.Tests/RequirementsTests.cs`
- Risk: Behavioural bugs above would ship unnoticed.
- Priority: High

**Database layer:**
- What's not tested: migration applies cleanly, RLS isolation as a non-owner role, `force row level security`, check constraints, foreign keys, append-only grants.
- Files: `db/migrations/V001__action_broker.sql`
- Priority: High (the only real second isolation layer is untested).

**Contract schemas:**
- What's not tested: proposal, execution-record, action-type schemas against their valid/invalid examples.
- Files: `contracts/schemas/`, `contracts/examples/`
- Priority: Medium

**Coverage tooling and CI:**
- No coverage collection or CI workflow is present; the `Outcomes` and `Catalog` code is only exercised through the broker happy path.
- Priority: Medium

---

*Concerns audit: 2026-10-21*
