# Project Research Summary

**Project:** Olive: Action Broker and Review Queue (POC-2). Milestone 1 is Phase 1, simulation-only.
**Domain:** A governed AI-action broker (maker-checker for writes proposed by an agent) plus a shared human review queue that holds references only. Built on C# / .NET 10 with ASP.NET Core, Razor Pages and PostgreSQL with forced RLS.
**Researched:** 2026-10-21
**Confidence:** MEDIUM-HIGH

> **Authority.** Decision v0.5.2 and the IR-1 contract are authoritative. This research overrides nothing in them. Where common practice goes further than the specs, the item appears under Open Questions and stays undecided.

## Executive Summary

Olive is a small security component, not a workflow product. Its job is to prove one chain from the execution record. An action ran only because:
- it traced to a verified hometown answer;
- it passed policy before any human saw it;
- a named reviewer approved it and gave a rationale;
- it ran exactly once.

The ratified design maps closely onto comparable systems: LangGraph and Temporal human-in-the-loop gates, GitHub and Azure approvals, and banking maker-checker. Olive is stricter than all of them in three ways:
- **Refused proposals are never queued.**
- **Reviewers cannot edit a proposal.** `proposal_hash` binds its content.
- **Approval is per proposal.** There are no standing approvals.

The research found no reason to loosen any ratified rule. It found about 20 places where the specs are silent; these are listed under Open Questions below.

**Recommended build: ports and adapters.**
- `Olive.Domain` is pure: state machines, trace checks and policy.
- `Olive.Contracts` holds the single JCS implementation and a validator that never mutates its input.
- `Olive.Application` holds a 7-step `SubmissionPipeline` in a hard-coded order.
- Each adapter has its own assembly.
- `Olive.Migrator` is a separate executable.

**Data access** uses Npgsql and Dapper with explicit SQL, not EF Core:
- Every transaction starts with `set_config('olive.tenant_id', @t, true)`.
- Every statement also carries a tenant predicate.
- Every state change is a compare-and-set update paired with an event row in an INSERT-only table.

**Request handling** never binds the body to a model. Olive reads the raw bytes, validates them with Corvus.Text.Json.Validator and hashes them with an in-house RFC 8785 implementation. That implementation is tested against the cyberphone vectors and a golden set shared with hometown.

**Reviewer UI** is Razor Pages with the plain OpenIdConnect handler and an in-process stub identity provider for development.

**Main risk: controls that look like they work but test nothing.** Examples:
- RLS tests that run as a superuser, which bypasses RLS.
- A self-approval check that compares an Entra `oid` with a popeye user id from a different identifier space, so it never matches.
- A "JCS" built on `Utf8JsonWriter`, which turns honest proposals into permanent `trace_proposal_mismatch` refusals.
- Leak tests that run at Information log level.
- A startup guard that reads a config flag instead of checking which stubs are actually registered.

Each fix is cheap only if it is in the foundation:
- Testcontainers connection strings tied to specific roles.
- Sealed secret types that redact themselves.
- A marker interface on every stub.
- `.gitattributes` in place before any hashed fixture exists.
- An explicit pipeline with an outcome table.

So the roadmap builds the test harness and both isolation layers first, then adds one end-to-end capability per phase.

## Key Findings

### Recommended Stack

Conflicts between the research files are resolved here.

- **.NET 10 SDK 10.0.204+ and ASP.NET Core 10.0.13.** One host for Minimal APIs and Razor Pages. Each endpoint group pins its own auth scheme; there is no default scheme.
- **Npgsql 10.0.3 + Dapper 2.1.89, not EF Core.** EF issues implicit UPDATEs that break INSERT-only tables. It also hides tenant filters, which breaks the §13 layer-removal test.
- **A hand-rolled, forward-only migration runner, not DbUp** (about 150 lines):
  - records applied migrations in `schema_migrations`;
  - takes an advisory lock and detects checksum drift;
  - runs under `SET ROLE olive_owner` and reads `sql/migrations/NNNN_*.sql`.

  A separate idempotent bootstrap script creates the roles.
- **Corvus.Text.Json.Validator 5.7.9** (Apache-2.0). It validates raw bytes with format assertion turned on.
  - JsonSchema.Net carries the OSMF fee; use it only if the organisation already sponsors json-everything.
  - Do not use NJsonSchema.
- **An in-house RFC 8785 (JCS) implementation**, ported from or checked against cyberphone. The NuGet options are all 0.1.x, or their provenance is unknown.
- **YamlDotNet 18.1.0.** Read the catalog through the RepresentationModel and convert it to JsonNode with YAML 1.2 scalar rules.
- **Tests:** xUnit v3 **3.2.2** (not 4.x) and Testcontainers.PostgreSql 4.16.0 with a native assembly fixture. Pin the image to the major version of `deploy-postgres-1`.
- **Supporting libraries and tooling:**
  - the OpenIdConnect handler, not Microsoft.Identity.Web;
  - TimeProvider / FakeTimeProvider;
  - BannedApiAnalyzers;
  - Microsoft.OpenApi 3.11.0, used only for a drift test (`openapi.yaml` is hand-authored);
  - Central Package Management with lockfiles.
- **Session-key capture.** Phase 1 has no `HttpClient` in `src/`. It uses in-process stubs, `RecordingDecorator`s at each port, and architecture tests. WireMock and DiagnosticListener arrive with the live transports (Q19).

### Expected Features

**Must have**
- **Endpoints:**
  - `POST /v1/proposals`: agents only; users and applications get 403.
  - `GET /v1/proposals/{id}`: a cross-tenant id returns 404.
  - `GET /v1/catalog/{version}`.
- **Ordered pipeline.** Each outcome has a distinct reason code and is explicitly stored or not stored.
- **JCS idempotency:**
  - a replay returns 200 with the current state;
  - a changed body returns 409;
  - two racing first submissions converge on one row.
- **Trace checks 1–5 against fixtures:**
  - only positive results are cached;
  - `trace_verification` comes from the resolver instance that answered.
- **Policy before queueing:**
  - purpose check;
  - entitlement intersection that fails closed;
  - a rate limit enforced in the database that counts accepted proposals only;
  - a `policy_evaluated` event.
- **Lifecycle:** every transition is guarded by compare-and-set. Expiry rules apply.
- **IR-1 generic queue:**
  - a source-controlled kind registry;
  - references only;
  - list, claim, decide and audit;
  - append-only history.
- **Approval rules:**
  - the reviewer is authenticated;
  - no self-approval, checked on matching identifiers and failing closed;
  - the role is checked at decision time;
  - the rationale must not be blank.
- **Execution:** simulated execution, idempotent on `proposal_id`, an INSERT-only ledger, and one query that returns the whole chain.
- **UI:** minimal Razor pages for the four actions, with a "trace verified by FIXTURE" banner.
- **Evidence:**
  - the §16.8 and §17.4 test suites, plus TC-ACT, TC-QUE and TC-IRQ;
  - the credential audit;
  - the session-key capture;
  - the beat 2 / beat 4 demo.

**Should have** (within the ratified text, no ruling needed)
- Compare-and-set on claim and decide.
- Deciding twice is idempotent when it is the same reviewer and the same decision.
- A production bar on every stub.
- Catalog validity and expiry re-checked at execution time.
- The audit page shows the hashed fields and both hashes.
- An `IAlarm` port that tests can assert on.
- The reviewer's auth source is recorded (`entra | stub`).

**Defer, or anti-features**
- **Binding exclusions:** dashboards, bulk actions, SLAs, assignment rules.
- **Approval:** edit-before-approve, standing or automatic approval, dual approval, delegation, reopen.
- **Integration:** approving from Teams, webhooks, AI-written rationales, registering types or kinds at runtime.
- **Phase 2 and beyond:** a real executor or credential, execution retry, cross-tenant audit, `link_challenge`.
- **Pending a ruling:** releasing a claim, re-checking policy at decision time, withdrawing an action.

### Architecture Approach

Dependencies point inward: Host → Application → Domain/Contracts. Adapters implement the ports defined in Application.

**Correctness lives in Postgres.** Unique constraints, compare-and-set transitions, rate counts under advisory locks and ledger uniqueness all enforce it there. Caches never change a decision.

**Transaction rules:**
- A queue item and its proposal change in the same transaction.
- No transaction stays open across a popeye or hometown call.
- Step 3 is a short read.
- Step 7 is a single write that may re-enter a bounded number of times if it loses a race.

**Major components**
- **Domain:**
  - both state machines, written as transition tables;
  - trace checks 2–5;
  - policy and expiry;
  - reason codes and value types.
- **Contracts:**
  - a single `JsonElement` parse that rejects duplicate keys;
  - the validator and JCS;
  - two separate hashes, `body_sha256` and `proposal_hash`.
- **Application:**
  - the pipeline, with a test that asserts its step order;
  - `QueueService` with an `IQueueKind` registry;
  - `ExecutionService` and the ports;
  - the trace cache;
  - a token cache keyed on (tenant, agent, key_id), with a lifetime of min(token.exp, key.exp) minus skew, checked on every read.
- **Persistence:**
  - `TenantUnitOfWork` is the only route to the database;
  - repositories require it;
  - Postgres error 23505 (unique violation) is translated.
- **Adapters:**
  - Catalog, with no mutator;
  - Executor.Simulated;
  - the popeye stub and hometown fixture, both marked `INonProductionOnly`;
  - Entra.
- **Host:**
  - the composition root;
  - problem-details mapping;
  - startup guards that run before `app.Run()`.
- **Migrator:** the only reader of `OLIVE_MIGRATOR_URL`.
- **Enforcement:**
  - banned APIs;
  - assembly-reference tests;
  - NetArchTest;
  - a package allowlist;
  - `SessionKey` accepted only by the popeye ports.

**Additions to the §13 schema:** `queue_item_event` (INSERT-only) and `schema_migrations`.

### Critical Pitfalls

1. **RLS bypassed by a superuser or owner connection.**
   - Test fixtures expose only the `olive_app` and `olive_reader` connection strings.
   - Assert that `rolsuper` and `rolbypassrls` are false.
   - Seed two tenants and require a non-zero count for the caller's own tenant.
   - The service refuses to start as a superuser, BYPASSRLS or owner role.
   - Migrations run under `SET ROLE olive_owner`.
2. **The tenant setting under connection pooling.**
   - Call `set_config(...,true)` only inside an explicit transaction.
   - No `TransactionScope`, no `No Reset On Close`, and no `current_setting(...,true)` in policies.
   - Test cross-tenant and unset-tenant cases with `Max Pool Size=1`, because a reused connection returns `''`.
3. **Hashing bytes that differ from the bytes received.**
   - Read the raw bytes and parse them once into one `JsonElement`.
   - The validator, the hasher and the four-key extraction all share that element.
   - Use the in-house JCS with the cyberphone vectors, plus a hometown golden set run in both CIs.
   - A mismatch becomes a permanent stored refusal.
4. **An append-only ledger or exactly-once execution that isn't.**
   - Separate policies per command and explicit grants, with a privilege-matrix test expecting 42501.
   - SELECT granted for replay.
   - Composite `(tenant_id, id)` foreign keys and `security_invoker` views.
   - Execution serialised per proposal, and an executor that is idempotent on `proposal_id`.
   - A fault-injection test.
5. **Approval and guard checks that fail silently.**
   - Check the reviewer's role inside the decide transaction, not from the cookie snapshot.
   - Map identifiers explicitly and fail closed when they cannot be compared.
   - Find stubs through the marker interface, and test the guard at process level with no test overrides.
6. **Secret leakage and test-harness traps.**
   - Use sealed secret types that redact themselves.
   - Search logs at Trace level, the JSONL output and every table row for a sentinel key.
   - Add `.gitattributes` first.
   - Guard against tests reaching `localhost:5432`.
   - Run CI on Ubuntu.

## Implications for Roadmap

There are 9 vertical slices. Each runs end-to-end against Testcontainers.

1. **Foundation and walking skeleton**
   - **Repo:** `.gitattributes`, the solution split, CPM and lockfiles, analyzers, architecture tests, secret types.
   - **Database:** a Testcontainers fixture per role, the bootstrap script, the migrator, and `0001_init` (`proposal` and `proposal_event` with FORCE RLS and the §13 grants).
   - **Guards:** `TenantUnitOfWork`, the `OLIVE_ENVIRONMENT` and marker guards, a self-check of the database role, the vocabulary hash check.
   - **Endpoints:** POST and GET proposal with the introspection stub.
   - **CI:** on Ubuntu.
   - **Avoids pitfalls:** 1–4, 12, 14 and 16.
2. **Contract integrity and idempotent submission**
   - **Validation:** strict parsing of the raw body, the v1 schemas, the Corvus validator, the in-house JCS and both hashes.
   - **Catalog:** the YamlDotNet catalog and `GET /v1/catalog/{version}`.
   - **Idempotency:** step 3, with a race test.
   - **Golden set:** started with hometown.
   - **Avoids pitfalls:** 5–8.
   - **Blocked by:** Q1 and Q2.
3. **Trace verification**
   - **Popeye:** the token-issuer stub and the token cache.
   - **Resolution:** the fixture resolver (7-field fixtures checked against the schema) and the resolution cache.
   - **Checks:** 1–5, following the stored/not-stored matrix, with 503 plus an alarm.
   - **Evidence:** `trace_verification`, the recording decorators, the session-key capture.
   - **Blocked by:** the §4 stop rule (POPEYE_v0.4.2 must be present), plus Q19 and Q20.
4. **Policy, expiry and refusal** (the precondition for beat 4)
   - **Policy:** the purpose check, an entitlement intersection that fails closed, and a rate limit on accepted proposals under an advisory lock.
   - **Recording:** `not_in_catalog` stored as a refusal, a `policy_evaluated` event, an absolute `expires_at`.
   - **Tests:** each outcome row is called twice.
   - **Scaffold defects:** fixed here.
   - **Blocked by:** Q3 and Q4.
5. **Generic queue and action approval** (completes beat 4)
   - **Migration 0002:** `queue_item` and `queue_item_event` in IR-1 shape, with a reference CHECK and indexes.
   - **Queue:** the kind registry; enqueueing inside the step-7 transaction; list, claim, decide and audit as JSON endpoints with a dev reviewer.
   - **Transitions:** compare-and-set across both state machines.
   - **Rules:** the approval rules, and lazy expiry committed before the error is returned.
   - **Blocked by:** Q9, Q10 and Q11 (these shape the schema, so answer them before the migration), plus Q13 and Q14. Q7 is recommended.
6. **Simulated execution, ledger and audit chain** (beat 2 through the API)
   - **Migration 0003:** the ledger, unique on `(tenant_id, proposal_id)`, with SELECT.
   - **Execution:** `ExecutionService` with a lock per proposal and re-checks, an idempotent simulated target, the trigger, and the `execution_failed` path.
   - **Testing:** fault injection.
   - **Chain:** a `security_invoker` view.
   - **Demo:** scripted.
   - **Blocked by:** Q5 and Q6.
7. **Reviewer UI and Entra OIDC**
   - **Pages:** Razor list, claim, decide and audit, with PRG, antiforgery, the banner and the view of hashed fields.
   - **Auth:** the OIDC handler behind interfaces, a DevReviewer stub barred in production, and one test of the real handler against `mock-oauth2-server`.
   - **Coverage:** every credential tried against every route group.
   - **Decision time:** the role check.
   - **Blocked by:** Q13, Q14 and Q15. Live approvals also wait for the group name, which is an external dependency.
8. **The identity_review kind** (stub traffic; can run in parallel with Phases 6 and 7)
   - **Create endpoint:** fields assigned by the server.
   - **Lifecycle:** withdraw and defer.
   - **Polling:** a cursor.
   - **Inbound auth:** a service token.
   - **Rejected:** `link_challenge`.
   - **Blocked by:** Q7.
9. **Contract and evidence pack**
   - **OpenAPI 3.1** with a drift test.
   - **COMPATIBILITY.md:** records the outcomes of Q1–Q3.
   - **Liam fixtures.**
   - **Evidence:** the credential audit, a sentinel search at Trace level, and a guard test for each stub.
   - **Docs:** STATUS refreshed, v0.1 marked superseded.

**Why this order**
- The foundation comes first because the traps that let controls pass silently are only cheap to prevent there.
- Phases 2 to 4 follow the ratified pipeline order.
- Execution comes before the UI, so the core value is proven through the API early.
- Phases 6, 7 and 8 depend only on Phase 5, so they can run in parallel.
- Each question is scheduled before the migration or contract it would change.

**Research flags**
- **Need `--research-phase`:**
  - **P3:** the popeye stub against POPEYE_v0.4.2, and the JCS golden set.
  - **P5:** how the action kind maps onto IR-1, and the `queue_item_event` grants.
  - **P6:** the execution trigger, holding a lock across the executor, and `execution_failed`.
  - **P7:** Entra app role plus max_age versus Graph; group overage; the oid mapping; SameSite and HTTPS.
- **Standard patterns:** P1, P2, P4, P8 and P9.
- **Spikes:**
  - `JsonDocumentOptions.AllowDuplicateProperties`;
  - Npgsql URI connection strings;
  - the privileges `ON CONFLICT DO NOTHING` needs;
  - lone surrogates;
  - the Postgres major version of `deploy-postgres-1` (15 or later is required).

## Open Questions for the Ratifier (consolidated)

| # | Question | Blocks | Recommended default |
|---|---|---|---|
| Q1 | 400 vs 409: idempotency (step 3) runs before schema validation (step 4), but §15.3 hashes "after validation" | **P2** | Return 400 before step 3 for an unparseable or non-I-JSON body, or a missing or invalid `run_id`. Otherwise hash the parsed body and return 409 on mismatch. Record this in COMPATIBILITY. |
| Q2 | Order within step 4: unknown action vs a failure against the catalog parameter schema (400 or refused_policy?) | **P2** | Check the envelope, then look up the catalog (unknown → stored refusal), then check the parameter schema (failure → 400, not stored). |
| Q3 | `expires_in_seconds` that is too large or ≤0; keys that have already expired | **P4** | 400, not a clamp, because a clamp would mutate the request. Keep the cap at the key's `expires_at` as written. An expired key gets 401. |
| Q4 | Where the entitlements claim comes from (it is missing from the D§7 claim table) | **P4** (also the stub shape in P1) | Take it only from POPEYE_v0.4.2 or vocabulary §3.2. If it is in neither, stop under §4. |
| Q5 | What triggers execution | **P6** | Run in-process after the approve commit, never inside that transaction. Add an internal endpoint for the demo and a sweep at startup. |
| Q6 | Is `execution_failed` terminal? | **P6** | Yes for Phase 1; a retry returns the first result. Flag transient retries for Phase 2. |
| Q7 | Claim release or timeout (identity items never expire, so an abandoned claim blocks one forever) | **P5**, acute in **P8** | **Ruling required.** Only the claimant can release (claimed→open, recorded). No lease timers and no admin reassignment. |
| Q8 | Must the decider be the claimant? | P5 | Yes. This is an implementer choice, enforced with a compare-and-set. |
| Q9 | The `queue_item_event` table and its grants (§13 lists four tables) | **P5** (migration 0002) | **Ruling or §13 revision.** `olive_app` gets INSERT and SELECT, the same RLS applies, and there is no UPDATE or DELETE policy. |
| Q10 | approve/reject versus IR-1's accept/reject/defer for action_approval | **P5** | Each kind has its own decision set: actions use approve or reject; identity items use accept, reject or defer. |
| Q11 | Expired proposals in the queue (IR-1 has no expired state) | **P5** | **Ruling required** (it adds a reason code). Withdraw the item with reason `proposal_expired`, committed lazily. The list query filters on `expires_at`. |
| Q12 | Withdrawing an action proposal | Not blocking | Not added, because it changes the Charter §3.2 lifecycle. Rejection plus expiry already bound the exposure. Revisit in Phase 2. |
| Q13 | Where the reviewer's tenant comes from for RLS | **P5** (dev identity), **P7** | **Ruling required.** From a credential claim or a server-side group mapping, never from a form or query string. One tenant per session. |
| Q14 | How an Entra oid maps to `on_behalf_of` for the self-approval check | **P5** logic, **P7** real IdP | An explicit mapping on `oid`+`tid`, never `sub` or UPN. If missing or ambiguous, refuse. Confirm against POPEYE_v0.4.2 and the vocabulary. |
| Q15 | How the role is checked at decision time | **P7** | An Entra app role plus a `max_age` re-challenge, or a short-lived cookie that is re-validated. Graph would need a read credential, so confirm REQ-ACT-04 first. |
| Q16 | Re-checking policy at decide or execute time | Not blocking (P6) | Re-check catalog validity and expiry at execution. Re-checking entitlements needs a ruling. |
| Q17 | A production bar on every stub, not only the §16.5 fixture | Not blocking (P1) | Bar them all through the marker interface. Confirm this matches the spirit of §16.5. |
| Q18 | How the audit chain is queried | Not blocking (P6) | A `security_invoker` view plus the UI audit page. An API endpoint would change the OpenAPI that goes to the §3 review. |
| Q19 | Session-key capture: at the port or on the wire? | P3 / P9 | Port-level decorators plus architecture tests now. Add WireMock and DiagnosticListener when the live transports arrive. |
| Q20 | Hometown runs the shared JCS golden set (a coordination item, not a ruling) | P2→P3 | Commit the vectors under `contracts/`, including the edge cases from Pitfall 6. Consider limiting integers to ±2^53. |

## Confidence Assessment

| Area | Confidence | Notes |
|---|---|---|
| Stack | MEDIUM-HIGH | Versions were checked live on 2026-10-21. The Corvus V5 package is new. JCS depends on the test vectors. Npgsql URIs and the DiagnosticListener payload are unverified. |
| Features | HIGH for scope / MEDIUM for comparisons | Scope comes from the ratified specs. The comparisons rest on official docs plus vendor and community sources. |
| Architecture | HIGH | Taken directly from D§6–§17 and IR-1. The enforcement tooling is MEDIUM. |
| Pitfalls | MEDIUM-HIGH | RLS, GUC and JSON defaults were checked against docs. Entra and cookie details, `ON CONFLICT` privileges and lone surrogates are LOW to MEDIUM and need spikes. |

**Overall: MEDIUM-HIGH.** The remaining uncertainty is in Q1–Q20 and a few spikes, not in the phase structure.

**Gaps**
- **Hard blockers by phase:**
  - P2: Q1, Q2
  - P4: Q3, Q4
  - P5: Q9, Q10, Q11, Q13, Q14 (Q7 recommended)
  - P6: Q5, Q6
  - P7: Q15
- **§4 stop rule:** confirm that POPEYE_v0.4.2, the vocabulary and the Liam specs are present before P1, P3 and P4.
- **Environment:**
  - the Postgres version of `deploy-postgres-1`;
  - Docker Desktop in Linux mode, with WSL2 and Ryuk;
  - Docker licence coverage.
- **Repo hygiene:**
  - add `.gitattributes`;
  - mark v0.1 as superseded;
  - move migrations from `db/` to `sql/migrations/` before a second migration exists.

## Sources

Full source lists are in STACK.md, FEATURES.md, ARCHITECTURE.md and PITFALLS.md.

**Primary**
- **Project specs:** Decision v0.5.2, Charter v0.4, IR-1 v0.1, PROJECT.md, CONCERNS.md.
- **Registries:** the NuGet and GitHub release APIs.
- **PostgreSQL:** the RLS and INSERT docs.
- **JCS:** RFC 8785 and cyberphone.
- **Microsoft Learn:** JSON defaults and header redaction.
- **Testing:** the xUnit 4.0 release notes and the Testcontainers docs.

**Secondary**
- **Postgres:** mailing-list threads on GUCs, and npgsql#4451.
- **Libraries:** the Corvus docs, NetArchTest, BannedApiAnalyzers, Duende.
- **Comparable systems:** the GitHub and Azure approval docs, LangGraph, Temporal.

**Tertiary (needs verification)**
- **Npgsql:** URI connection strings.
- **.NET:** the DiagnosticListener payload.
- **Entra:** app roles with max_age, and group overage.
- **Cookies:** the default behaviour.
- **Postgres:** the privileges `ON CONFLICT` needs.
- **Docker:** licence coverage.
- **Comparable systems:** vendor and community sources on human-in-the-loop.

---
*Research completed: 2026-10-21*
*Ready for roadmap: yes*
