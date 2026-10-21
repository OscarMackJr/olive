# Architecture Research

**Domain:** Governed action broker plus shared human review queue (.NET 10, ASP.NET Core, Razor Pages, PostgreSQL with RLS). Phase 1 is simulation-only.
**Researched:** 2026-10-21
**Confidence:** HIGH for structure, pipeline order, transactions and state machine (derived directly from the ratified Decision v0.5.2 and IR-1 v0.1). MEDIUM for library-level enforcement mechanics (NetArchTest, BannedApiAnalyzers, JCS packages), which were checked against current web sources but not against Context7.

Spec references below: "D§" is `specs/DECISION_olive-poc2-phase1_v0.5.md`, "IR-1§" is `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`.

---

## Standard Architecture

### System Overview

```
  Liam (agent, session key via nono)     Reviewer browser (Entra OIDC)     bluto (v1.1, stub only)
            │ POST/GET /v1/proposals              │ /queue/* Razor Pages        │ POST/GET /v1/queue/items
            ▼                                     ▼                             ▼
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ Olive.Host  (ASP.NET Core: minimal API endpoints + Razor Pages + composition root)        │
│  auth scheme "AgentKey"  │  auth scheme "Oidc" (cookie)  │  auth scheme "ServiceToken"   │
│  StartupGuards (OLIVE_ENVIRONMENT, stub/fixture bar, DB role check, catalog, vocab hash)  │
└──────────────┬───────────────────────────┬───────────────────────────────┬───────────────┘
               │ calls use cases only      │                               │
               ▼                           ▼                               ▼
┌───────────────────────────────────────────────────────────────────────────────────────────┐
│ Olive.Application  (use cases + PORTS; no I/O of its own)                                 │
│  SubmissionPipeline (7 ordered steps)   QueueService + QueueKindRegistry   ExecutionService│
│  Ports: IPopeyeIntrospection  IPopeyeTokenIssuer  ITraceResolver  IActionExecutor         │
│         IReviewerIdentity  IReviewerRoleChecker  IProposalStore  IQueueStore  ILedgerStore│
│         ITenantUnitOfWorkFactory  ICatalog  IAlarm  TimeProvider                          │
│  Caches: TraceResolutionCache (tenant,trace)  ServiceTokenCache (tenant,agent,key_id)     │
└──────────────┬───────────────────────────────────────────────────────────────────────────┘
               │ depends on                                    ▲ implements ports
               ▼                                               │
┌──────────────────────────────┐   ┌────────────────────────────┴──────────────────────────────┐
│ Olive.Domain (pure, BCL only)│   │ Adapters (each its own assembly)                          │
│  Proposal + state machine    │   │  Olive.Persistence     Npgsql, tenant tx, repositories    │
│  QueueItem + state machine   │   │  Olive.Catalog         YamlDotNet loader, schema check    │
│  Policy rules (pure)         │   │  Olive.Executor.Simulated  JSONL target, NO network       │
│  Trace checks 1-5 (pure)     │   │  Olive.Integrations.Popeye    Phase 1: stub only          │
│  Expiry calculator           │   │  Olive.Integrations.Hometown  Phase 1: fixture only       │
│  Value types, reason codes   │   │  Olive.Integrations.Entra     OIDC wiring + dev IdP stub  │
└──────────────────────────────┘   └────────────────────────────────────────────────────────────┘
               ▲
┌──────────────┴───────────────┐   ┌──────────────────────────────┐
│ Olive.Contracts              │   │ Olive.Migrator (console exe) │
│  v1 DTOs, JSON Schema 2020-12│   │  reads OLIVE_MIGRATOR_URL     │
│  validator (non-mutating),   │   │  applies sql/migrations/*.sql │
│  JCS canonicalizer + SHA-256 │   │  schema_migrations table      │
└──────────────────────────────┘   └──────────────────────────────┘
                                           │
                              PostgreSQL "olive" db (deploy-postgres-1 locally, Testcontainers in CI)
                              proposal · proposal_event · queue_item · queue_item_event · execution_ledger
                              ENABLE+FORCE RLS on all; olive_app NOBYPASSRLS; events+ledger INSERT-only
```

Dependency direction is strictly inward: Host → Application → Domain/Contracts; adapters → Application (to implement ports) → Domain. Domain references nothing in the solution.

### Component Responsibilities

| Component | Owns | Must never | Talks to |
|-----------|------|------------|----------|
| **Olive.Domain** | `Proposal`, `QueueItem`, `ExecutionRecord` entities; the two state machines; pure policy rules (purpose, entitlement intersection); pure trace checks 1-5 (D§17.3); expiry calculation (D§9); typed reason codes (`trace_not_found`, `trace_proposal_mismatch`, `not_in_catalog`, ...); value types (`TenantId`, `AgentId`, `RunId`, `KeyId`, `TraceId`, `ProposalId`) | Reference any package; do I/O; read the clock directly (takes `DateTimeOffset now`) | Nothing |
| **Olive.Contracts** | v1 wire DTOs, embedded JSON Schemas (copied/linked from `contracts/proposal/v1/`), non-mutating validator over `JsonElement`, RFC 8785 canonicalizer, `BodyHash`/`ProposalHash` computation | Deserialize-then-validate (coercion risk); strip or default anything | Domain (value types only) |
| **Olive.Application** | Use cases (`SubmitProposal`, `GetProposal`, `ListQueue`, `Claim`, `Decide`, `Withdraw`, `Execute`, `Audit`); the `SubmissionPipeline`; `QueueKindRegistry`; all port interfaces; both caches; alarm raising | Know about HTTP, Npgsql, YAML, files; hold a DB transaction across an outbound port call | Domain, Contracts, ports |
| **Olive.Persistence** | `TenantUnitOfWork` (connection + tx + `set_config`), repositories with explicit `tenant_id` predicates, CAS state updates, unique-violation translation (`23505` → `DuplicateKey`), startup DB self-check | Run any statement outside a tenant transaction; build SQL without `tenant_id = @tenant` | Application (implements ports), Npgsql |
| **Olive.Migrator** | Forward-only runner over `sql/migrations/NNNN_*.sql`, `schema_migrations` table, role/grant DDL | Be referenced by the Host (the service must never see `OLIVE_MIGRATOR_URL`) | PostgreSQL as `olive_migrator` |
| **Olive.Catalog** | Load `catalog/*.yaml` with YamlDotNet, validate against `action-type.schema.json`, filename == `action_id`, vocabulary purpose check, compile parameter schemas; immutable `ICatalog` with no public mutator | Offer any runtime registration (REQ-ACT-06) | Application (implements `ICatalog`) |
| **Olive.Executor.Simulated** | `IActionExecutor` that appends a "would_send" line to the JSONL target keyed by `proposal_id`, target-side idempotent | Reference `System.Net.*`, Npgsql, any SDK; hold any credential | Application (implements port) |
| **Olive.Integrations.Popeye** | Phase 1: `StubPopeyeIntrospection`, `StubPopeyeTokenIssuer` driven by fixtures (incl. refusal/disagreement cases). Phase 2+: the only place a popeye HTTP client may live | Log or persist tokens or session keys | Application (implements ports) |
| **Olive.Integrations.Hometown** | Phase 1: `FixtureTraceResolver` over `contracts/trace-resolution/*.json`, exposing `Source = fixture`. Later: HTTP resolver with `Source = hometown` | Receive a `SessionKey` (type system forbids it) | Application (implements port) |
| **Olive.Integrations.Entra** | OIDC registration, claims → `ReviewerPrincipal`, `IReviewerRoleChecker` (group membership at decision time); `DevIdentityProvider` stub for local/ci | Cache role membership across decisions | Application |
| **Olive.Host** | Composition root, endpoint mapping, auth schemes, Razor Pages, problem-details mapping of outcomes to HTTP codes, startup guards | Contain business logic; map one scheme onto another's endpoints | Application only (plus adapters for DI registration) |

---

## Recommended Project Structure

```
Olive.sln
Directory.Build.props              # net10.0, Nullable, TreatWarningsAsErrors, BannedApiAnalyzers on by default
Directory.Packages.props           # central package management = the package allowlist
BannedSymbols.Network.txt          # shared ban list, included by Domain/Application/Executor/Contracts/Catalog
sql/migrations/0001_init.sql       # D§13 location; retire db/migrations/V001__action_broker.sql
catalog/crm.case_note@1.yaml
contracts/
  proposal/v1/{proposal.request.schema.json, proposal.response.schema.json, openapi.yaml, COMPATIBILITY.md}
  trace-resolution/{*.fixture.json}        # 7-field responses keyed by trace_id (D§17.3)
  popeye-stub/{keys.fixture.json, tokens.fixture.json}
  queue/v1/{queue-item.schema.json, create-identity-review.schema.json}
  queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md
  vendored/vocabulary.yaml                 # untouched
src/
  Olive.Domain/
    Proposals/        Proposal.cs, ProposalState.cs, ProposalStateMachine.cs, ExpiryCalculator.cs
    Queue/            QueueItem.cs, QueueItemState.cs, QueueStateMachine.cs, Decision.cs
    Trace/            TraceResolution.cs, TraceChecks.cs            # checks 2-5, pure
    Policy/           PolicyRules.cs, EntitlementSet.cs
    Execution/        ExecutionRecord.cs
    Identity/         TenantId.cs, AgentId.cs, KeyId.cs, ...         # readonly record structs
    Errors/           ReasonCode.cs                                   # closed set, matches DB checks
  Olive.Contracts/
    Schemas/ (embedded) Validation/JsonSchemaValidator.cs  Canonical/Jcs.cs  Hashing/BodyHash.cs
  Olive.Application/
    Ports/            IPopeyeIntrospection.cs IPopeyeTokenIssuer.cs ITraceResolver.cs IActionExecutor.cs
                      IReviewerIdentity.cs IReviewerRoleChecker.cs IProposalStore.cs IQueueStore.cs
                      ILedgerStore.cs ITenantUnitOfWorkFactory.cs ICatalog.cs IAlarm.cs
    Credentials/      SessionKey.cs ServiceToken.cs                   # opaque, redacting ToString
    Submission/       SubmissionPipeline.cs SubmissionContext.cs SubmissionOutcome.cs Steps/*.cs
    Queue/            QueueService.cs QueueKindRegistry.cs Kinds/ActionApprovalKind.cs Kinds/IdentityReviewKind.cs
    Execution/        ExecutionService.cs
    Caching/          TraceResolutionCache.cs ServiceTokenCache.cs
  Olive.Persistence/  TenantUnitOfWork.cs Repositories/*.cs DbSelfCheck.cs
  Olive.Catalog/      YamlCatalogLoader.cs
  Olive.Executor.Simulated/  SimulatedExecutor.cs
  Olive.Integrations.Popeye/  Stub/*.cs
  Olive.Integrations.Hometown/ Fixture/FixtureTraceResolver.cs
  Olive.Integrations.Entra/   OidcSetup.cs DevIdentityProvider.cs EntraRoleChecker.cs
  Olive.Host/         Program.cs Endpoints/*.cs Pages/Queue/*.cshtml Auth/*.cs StartupGuards.cs
  Olive.Migrator/     Program.cs
tests/
  Olive.Domain.Tests/        # pure unit tests: state machines, checks, expiry, policy
  Olive.Application.Tests/   # pipeline with in-memory port fakes (the ONLY place in-memory stores live)
  Olive.Architecture.Tests/  # assembly/type dependency rules, package allowlist, credential-type rules
  Olive.Integration.Tests/   # Testcontainers Postgres + WebApplicationFactory; RLS negative tests; TC-* cases
  Olive.Contracts.Tests/     # schemas vs valid/invalid examples, JCS vectors, vocabulary hash
```

### Structure Rationale

- **Domain separate from Application:** the state machines, trace checks and policy rules are the "core value" chain. Keeping them pure makes every D§16.8/§17.4 refusal case a microsecond unit test, and the architecture tests can make the strongest claims ("references nothing") about it.
- **Contracts as its own assembly:** it is the artifact that goes to the D§3 joint review. Canonicalization and validation must be identical everywhere a hash is computed (body hash, proposal hash, IR-1 create hash), so one implementation, one place.
- **Each adapter in its own assembly:** this is what makes "no HTTP client in executor/domain" enforceable per assembly rather than per file. It also makes the Phase-2 switch (live popeye, live hometown) a change to exactly one assembly plus one DI line.
- **Migrator as a separate executable:** D§15.4 says the service reads only `OLIVE_POSTGRES_URL` and the migrator alone reads `OLIVE_MIGRATOR_URL`. A separate process makes that structural.
- **`sql/migrations/`** rather than `db/migrations/V001__…`: D§13 names it; reconcile now, before any second migration exists.
- **One Host for API + Razor Pages:** acceptable for the POC provided auth schemes are pinned per endpoint group (see Anti-Pattern 5). Splitting into two hosts is a later option, not needed now.

---

## Enforcing "no HTTP client in executor/domain"

Use four layers; any one would catch most mistakes, together they make the REQ-ACT-04 claim auditable. The current test (substring search of `Domain.cs` for `HttpClient`) is replaced.

| Layer | Mechanism | Catches | Confidence |
|-------|-----------|---------|------------|
| 1. Compile time | `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a shared `BannedSymbols.Network.txt` (`T:System.Net.Http.HttpClient`, `T:System.Net.Http.HttpMessageHandler`, `T:System.Net.Sockets.Socket`, `T:System.Net.Sockets.TcpClient`, `T:System.Net.WebRequest`, `T:System.Net.WebClient`, `T:System.Diagnostics.Process`, `T:Npgsql.NpgsqlConnection` ...) added as `AdditionalFiles` in Domain, Contracts, Application, Catalog, Executor.Simulated. With `TreatWarningsAsErrors`, RS0030 fails the build | Direct use in source | MEDIUM (doc ID format verified; prefer explicit `T:` entries over relying on `N:` namespace bans) |
| 2. Assembly references | Test iterates `typeof(X).Assembly.GetReferencedAssemblies()` for each restricted assembly and asserts none of `System.Net.Http`, `System.Net.Sockets`, `System.Net.Requests`, `Npgsql`, `Microsoft.AspNetCore.*`, `Azure.*` appear. The C# compiler only emits references that IL actually uses, so this is a real signal | Use via any route, including transitive helpers in the same assembly | HIGH |
| 3. Type dependencies | `NetArchTest.eNhancedEdition` (maintained fork; original NetArchTest.Rules last released 2021) or ArchUnitNET: `Types.InAssembly(domain).ShouldNot().HaveDependencyOnAny("System.Net", "Npgsql", "Microsoft.AspNetCore", "Olive.Persistence", "Olive.Integrations")`; plus layering rules (Domain → nothing; Application ↛ adapters; Executor.Simulated → only Application/Domain) | Layer violations, not just network | MEDIUM |
| 4. Project/package graph | Test parses every `*.csproj` + `Directory.Packages.props`: (a) restricted projects have no `PackageReference` outside an allowlist; (b) `ProjectReference` graph matches the allowed matrix; (c) no project except `Olive.Integrations.*` may ever add `Microsoft.Extensions.Http`; (d) in Phase 1, **no** project at all uses `IHttpClientFactory`/`HttpClient` (Host included) | Someone adding an SDK package "just to try" | HIGH |

Credential-flow rules, also in `Olive.Architecture.Tests` (they implement D§17.1 item 2 structurally, in addition to the runtime capture test):

- `SessionKey` is a sealed type with a redacting `ToString()` and no `JsonConverter`; the only methods in the solution with a `SessionKey` parameter are on `IPopeyeIntrospection`, `IPopeyeTokenIssuer` and their implementations. Test: reflect over all public/internal methods in all `Olive.*` assemblies.
- `ServiceToken` is accepted only by `ITraceResolver` (and later the bluto-facing queue auth handler as an inbound credential). It has no serializable public property holding the raw value.
- Runtime complement (D§17.4 "capture of every outbound call"): in integration tests, wrap every outbound port with a `RecordingDecorator` that records `(port, credential kind, credential fingerprint)`. Assert the session key fingerprint appears only on popeye ports. When real HTTP adapters arrive in Phase 2, add a `DelegatingHandler` recorder at the HttpClient level too.

---

## Ports

All ports are async, take `CancellationToken`, and return closed result types (no exceptions for expected outcomes). Results mirror the facts the other side returns; Olive's decisions live in Domain (D§16.2 "hometown returns facts, not a verdict").

```csharp
// Olive.Application/Ports
public interface IPopeyeIntrospection {
    Task<IntrospectionResult> IntrospectAsync(SessionKey key, CancellationToken ct);
}
// IntrospectionResult = Active(Introspection) | Inactive | Invalid | Unavailable
// Introspection: KeyId, PrincipalType{user|application|agent}, AgentId?, OnBehalfOf?, Purpose,
//                TenantId, ExpiresAt, Entitlements (see gap: source of agent/user entitlements)

public interface IPopeyeTokenIssuer {
    Task<TokenIssueResult> IssueAsync(SessionKey key, CancellationToken ct);
}
// TokenIssueResult = Issued(ServiceToken) | Refused(reason: tenant_not_registered|agent_not_permitted) | Unavailable
// ServiceToken: opaque value + TenantId, DelegatedAgentId, SourceKeyId, ExpiresAt, PrincipalType=application

public interface ITraceResolver {
    TraceVerificationSource Source { get; }          // fixture | hometown (D§16.5)
    Task<TraceResolveResult> ResolveAsync(TraceId id, ServiceToken token, CancellationToken ct);
}
// TraceResolveResult = Found(TraceResolution{trace_id, schema_version, run_id?, tenant_id, agent_id?,
//                      proposal_hash?, created_at}) | NotFound | Unavailable | CallerRejected(401|403)

public interface IActionExecutor {
    ExecutionMode Mode { get; }                       // simulated (Phase 1 only value)
    Task<ExecutorResult> ExecuteAsync(ExecutionCommand cmd, CancellationToken ct);
}
// ExecutionCommand: ProposalId (also the target idempotency key), TenantId, ActionType, Parameters (refs)
// ExecutorResult = Succeeded(targetRef) | Failed(code) ; MUST be idempotent on ProposalId at the target

public interface IReviewerIdentity {                  // who is acting, from the authenticated request
    ReviewerPrincipal? Current { get; }               // Entra oid, display name, tenant
}
public interface IReviewerRoleChecker {               // checked at DECISION time, never cached (D§8, IR-1§3)
    Task<bool> IsMemberAsync(ReviewerPrincipal r, string role, CancellationToken ct);
}
```

Supporting ports: `ITenantUnitOfWorkFactory`, `IProposalStore`, `IQueueStore`, `ILedgerStore`, `ICatalog`, `IAlarm` (operational alarm sink; Phase 1 = structured log at Critical plus a test-visible counter), and .NET's built-in `TimeProvider` (use `FakeTimeProvider` in tests) instead of a custom clock.

---

## Architectural Patterns

### Pattern 1: Submission pipeline as an explicit ordered sequence with first-failure-wins

**What:** A `SubmissionPipeline` class that calls the steps in a hard-coded order. Each step returns `StepResult.Continue` or `StepResult.Stop(SubmissionOutcome)`. The first `Stop` ends the pipeline. Every `SubmissionOutcome` declares its HTTP status, reason code, whether it is **stored** and whether it raises an **alarm**, so the D§17.3 "Stored?" column is data, not scattered `if`s.

**When to use:** Here, because the order is a ratified contract (D§15.2 as amended by §16.3 and §17.3). Do **not** discover steps through DI or an `Order` attribute; registration order is an invisible place for the contract to drift. A test asserts the exact step sequence.

**Steps and outcomes:**

| # | Step | Stops with | Stored? | Notes |
|---|------|------------|---------|-------|
| 1 | `IntrospectStep` | `401 key_inactive` / `401 key_invalid`; popeye unavailable → `503` | No | Always runs, even on retries |
| 2 | `PrincipalStep` | `403 principal_not_permitted` (user/application), `403 agent_not_allowlisted` | No | Allowlist from config: `liam` |
| 3 | `IdempotencyStep` | match + same `body_sha256` → `200` current state (pipeline ends; **no trace call**); match + different hash → `409 idempotency_conflict` (logged) | No | Short read tx (see Transactions). Needs `run_id` and the JCS hash: parse body as I-JSON first; if `run_id` cannot be extracted or the body is not I-JSON → `400` |
| 4 | `SchemaCatalogStep` | schema failure (incl. catalog parameter schema) → `400` not stored; unknown/retired `action_type` → `refused_policy not_in_catalog` | 400: No; not_in_catalog: **Yes** | Validation over `JsonElement`, non-mutating (D§17.2.4). Compute `proposal_hash` over exactly `action_type, party_ref, parameters, justification` here |
| 5 | `TraceStep` | see sub-order below | per row | Token is fetched lazily, only on a resolution-cache miss |
| 6 | `PolicyStep` | `refused_policy` with `purpose_not_permitted` / `entitlement_denied` / `rate_limited` | **Yes**, never queued | Pure checks first; rate check runs inside the persist transaction (step 7) so refusals never consume budget |
| 7 | `PersistAndEnqueueStep` | unique violation on `(tenant_id, agent_id, run_id)` → **re-enter step 3** once | — | One transaction: proposal + events + queue_item + queue_item_event |

Step 5 sub-order (D§17.3, first failure wins):

```
cache hit (tenant_id, trace_id)? ── yes ─────────────────────────────────┐
        │ no                                                              │
token cache hit (tenant, agent, key_id)? ─ no → IssueAsync(session key)   │
        │   Refused/Unavailable → 503 + alarm, not stored                 │
        │   token.tenant/delegated_agent/key_id ≠ introspection → discard, 503 + alarm
        │   token.exp > key.exp → reject, never cache, 503 + alarm        │
ResolveAsync(trace_id, token)                                             │
   Unavailable → 503 (fail closed), not stored                            │
   CallerRejected 401/403 → 503 + alarm, not stored                       │
   NotFound → 422 trace_not_found, retryable, NOT stored, NOT cached      │
   Found → cache it ──────────────────────────────────────────────────────┤
                                                                          ▼
 check 2 run_id null|≠ → trace_unresolved (stored)
 check 3 tenant ≠ key tenant → trace_tenant_mismatch (stored)
 check 4 agent_id null|≠ token.delegated_agent_id* → trace_agent_mismatch (stored)
 check 5 proposal_hash null|≠ olive hash → trace_proposal_mismatch (stored)
 record trace_verification = resolver.Source, trace_schema_version = schema_version
```

\* On a resolution-cache hit there is no token in hand. Because D§17.3 asserts `delegated_agent_id` always equals introspection's `agent_id` (any disagreement already failed closed when the token was obtained), compare against the introspected `agent_id` on cache hits. Record this reasoning in a code comment and a test.

**Trade-offs:** More types than a single method, but each step is independently testable and the outcome table maps one-to-one onto D§16.8/§17.4 test rows.

**Example:**
```csharp
public sealed class SubmissionPipeline(
    IntrospectStep s1, PrincipalStep s2, IdempotencyStep s3, SchemaCatalogStep s4,
    TraceStep s5, PolicyStep s6, PersistAndEnqueueStep s7)
{
    public async Task<SubmissionOutcome> RunAsync(SubmissionContext ctx, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            foreach (var step in new ISubmissionStep[] { s1, s2, s3, s4, s5, s6 })
            {
                if (attempt > 0 && step is IntrospectStep or PrincipalStep) continue; // already done this request
                var r = await step.RunAsync(ctx, ct);
                if (r is StepResult.Stop stop)
                    return stop.Outcome.Stored ? await s7.PersistRefusalAsync(ctx, stop.Outcome, ct) : stop.Outcome;
            }
            var persisted = await s7.PersistAcceptedAsync(ctx, ct);
            if (persisted is not SubmissionOutcome.LostRace) return persisted;   // else: loop re-enters step 3
        }
        throw new InvalidOperationException("idempotency race did not converge");
    }
}
```
(Stored refusals also go through `s7` and can lose the same race; `PersistRefusalAsync` returns `LostRace` the same way.)

### Pattern 2: Tenant unit of work with `set_config(..., true)` per transaction

**What:** Every database interaction goes through `ITenantUnitOfWorkFactory.BeginAsync(TenantId tenant)`. It opens a pooled `NpgsqlConnection`, begins a transaction, runs `SELECT set_config('olive.tenant_id', @tenant, true)` (the `true` makes it transaction-local, i.e. `SET LOCAL`; `SET LOCAL` itself cannot take bind parameters, so `set_config` is the safe form), and hands back an object that repositories require as a parameter. Repositories read `uow.Tenant` and put `tenant_id = @tenant` in every statement. There is no repository method that does not take a unit of work.

**When to use:** Always; this is D§13's "SET LOCAL at the start of every transaction" made unbypassable.

**Trade-offs:** Slightly more ceremony than a DbContext. Worth it: the two isolation layers become visible in code review.

```csharp
public async Task<TenantUnitOfWork> BeginAsync(TenantId tenant, CancellationToken ct)
{
    var conn = await _dataSource.OpenConnectionAsync(ct);
    var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    await using var cmd = new NpgsqlCommand("select set_config('olive.tenant_id', $1, true)", conn, tx);
    cmd.Parameters.Add(new() { Value = tenant.Value.ToString() });
    await cmd.ExecuteScalarAsync(ct);
    return new TenantUnitOfWork(conn, tx, tenant);
}
```

Notes: once a custom setting has been set in a pooled session, after commit it reverts to `''` rather than "unrecognized"; `''::uuid` then raises an error, which is still fail-closed. The integration suite must include "statement with no `set_config` in this tx errors" on a reused pooled connection.

### Pattern 3: State machines as pure tables, persisted with compare-and-set plus an appended event

**What:** `ProposalStateMachine` is a static transition table in Domain:

```
proposed          → awaiting_approval | refused_policy
awaiting_approval → approved | rejected | expired
approved          → executed | execution_failed | expired
(refused_policy, rejected, executed, execution_failed, expired are terminal)
```

Persistence applies a transition with `UPDATE proposal SET state=@to, ... WHERE tenant_id=@t AND proposal_id=@id AND state=@from` and requires exactly one affected row, then `INSERT INTO proposal_event (tenant_id, proposal_id, seq, from_state, to_state, actor, reason_code, at)` in the same transaction. Zero rows means someone else moved it: return a typed `StateConflict`, never overwrite. This fixes the scaffold's re-approve/re-execute bugs (CONCERNS) without SERIALIZABLE everywhere.

Since the row is only written after policy (step 7), insert it already in its post-policy state and append two events in the same transaction (`→ proposed`, `proposed → awaiting_approval|refused_policy`) so history shows the full lifecycle.

`QueueStateMachine` (IR-1§2-§4): `open → claimed → decided | open (defer)`, and `open|claimed → withdrawn`. Every claim, decision, defer and withdrawal appends a `queue_item_event` row (INSERT-only), which is the IR-1 "decision history" and the audit view.

**Expiry** is enforced lazily at claim, decide and execute (and shown in the list). When the deadline has passed, the operation commits `→ expired` (proposal) and `→ withdrawn reason=proposal_expired` (queue item) **and then** returns the distinct `proposal_expired` error. Do not throw before commit, or the expiry is never recorded (current scaffold bug). An optional `BackgroundService` sweeper can do the same, but correctness must not depend on it.

### Pattern 4: Generic queue with a kind registry

**What:** The queue knows only IR-1's generic model. Everything kind-specific lives behind `IQueueKind`, registered in code (source-controlled, like the catalog, no runtime registration):

```csharp
public interface IQueueKind {
    string Kind { get; }                                   // "action_approval" | "identity_review"
    IReadOnlySet<string> Subtypes { get; }                 // identity_review: {"candidate_pair"}; link_challenge rejected
    IReadOnlySet<Decision> Decisions { get; }              // action_approval: {approve, reject}; identity_review: {accept, reject, defer}
    string RequiredRole(QueueItem item);                   // role:case_supervisor | role:identity_steward
    bool PurposeAllowed { get; }                           // identity_review: purpose must be absent
    IReadOnlySet<string> RefSchemes { get; }               // proposal:// ; bluto:candidate/ , bluto:review-evidence/
    Task<DecisionGuard> CheckDecisionAsync(QueueItem item, ReviewerPrincipal r, TenantUnitOfWork uow, CancellationToken ct);
    Task OnDecidedAsync(QueueItem item, Decision d, ReviewerPrincipal r, TenantUnitOfWork uow, CancellationToken ct);
}
```

- `ActionApprovalKind.CheckDecisionAsync`: proposal still `awaiting_approval`, not expired, reviewer ≠ proposal `on_behalf_of` (no self-approval). `OnDecidedAsync`: CAS proposal `awaiting_approval → approved|rejected` plus `proposal_event`, **in the same transaction** as the queue decision. Items are created internally by step 7, never through the public create endpoint.
- `IdentityReviewKind`: no pre-queue policy (IR-1§4), no expiry, decisions `accept|reject|defer`; `defer` returns the item to `open` with history kept; no `OnDecided` side effect (bluto polls). Created through `POST /v1/queue/items` with the IR-1 create-body rules: exactly six fields; `tenant_id`, `producer`, `item_id`, `state` or decision fields → `400 server_assigned_field`; unknown field → `400 invalid_request`; idempotency on `(producer, tenant_id, producer_ref)` with the same JCS rules as proposals.
- The generic `QueueService` enforces for every kind: authenticated reviewer, `decided_by` and `rationale` required, role checked at decision time through `IReviewerRoleChecker`, state guards, refs-only (scheme allowlist from the kind, max length, no JSON-looking values).

**Open choice to flag:** whether `action_approval` uses `approve|reject` (charter wording) or IR-1's `accept|reject|defer`. Recommendation: per-kind decision sets as above, with `approve|reject` for actions (no `defer` in Phase 1; a deferred action approaches its expiry with nothing gained).

### Pattern 5: Idempotency through a unique constraint plus stored JCS hash

**What:** `proposal` has `body_sha256 bytea NOT NULL` and `UNIQUE (tenant_id, agent_id, run_id)`. Step 3 reads by the key; step 7 inserts with `ON CONFLICT (tenant_id, agent_id, run_id) DO NOTHING RETURNING proposal_id`. No row returned means a concurrent first submission won: roll back and re-enter step 3, which now finds the winner and returns `200` (same hash) or `409` (different). `queue_item` gets the same treatment with `UNIQUE (tenant_id, producer, producer_ref)` for IR-1, and `execution_ledger` with `UNIQUE (tenant_id, proposal_id)` for execution idempotency.

JCS is computed in `Olive.Contracts` over the parsed body as received. Parse with duplicate properties rejected (`.NET 10` adds `AllowDuplicateProperties = false`; verify it exists on `JsonDocumentOptions` in the shipped .NET 10, otherwise detect duplicates with a `Utf8JsonReader` pass). RFC 8785 requires I-JSON, so numbers outside IEEE-754 double range or lone surrogates are a `400`. Use a library with the RFC test vectors (candidates: `Jcs.Net`, `MackySoft.Json.Canonicalization`, both 0.1.x) or a small in-house implementation, and in either case pin the RFC 8785 appendix vectors in `Olive.Contracts.Tests`.

### Pattern 6: Caches as optimisations that cannot change a decision

| Cache | Key | Value | Lifetime | Never cached | Implementation |
|-------|-----|-------|----------|--------------|----------------|
| Trace resolution (D§16.4) | `(tenant_id, trace_id)` | the 7 identifier fields only | indefinitely; bounded by `MemoryCache` `SizeLimit` (eviction only costs latency) | 404, 503, 401/403 | in-process `IMemoryCache` inside `TraceStep` (not a resolver decorator, because a miss needs a token and the key needs the tenant) |
| Service token (D§17.1.4) | `(tenant_id, delegated_agent_id, key_id)` | `ServiceToken` | absolute expiry = min(token.exp, key.exp) − skew (e.g. 30 s); checked again on read (D§17.4 "reused after expiry → fresh token first") | any token failing the claim/expiry assertions | in-process, single-flight per key (`Lazy<Task<…>>` in a `ConcurrentDictionary`) to avoid concurrent issuance |

Do not use `HybridCache` or any distributed cache for tokens: that persists a credential outside the process (D§17.1.4 "never stored"). Both caches are per instance; with several instances each warms independently, which is acceptable because decisions do not depend on the cache.

### Pattern 7: Startup guards that fail before the host accepts traffic

Run in `Program.cs` before `app.Run()` (and in `WebApplicationFactory` so tests can assert refusal):

1. `OLIVE_ENVIRONMENT` required, one of `local|ci|nonprod|production`, no default (D§16.5). Unset or other value → refuse to start.
2. `production` with `FixtureTraceResolver` → refuse (D§16.5, required). Recommended extension: `production` with any stub (`StubPopeyeIntrospection`, `StubPopeyeTokenIssuer`, `DevIdentityProvider`) → refuse. Implement as a marker interface `INonProductionOnly` on stub types and one generic check over the DI registrations.
3. `OLIVE_MIGRATOR_URL` present in the service's environment → refuse (keeps the migrator credential out of the service).
4. DB self-check as the connected role: `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user` must be `false,false`; `schema_migrations` max version equals the version the build expects.
5. Catalog loads and validates; vocabulary file SHA-256 equals `VENDORED.md`.

Use `IValidateOptions<OliveOptions>` with `ValidateOnStart()` for 1 and 3, and an explicit `StartupGuards.EnforceAsync(app.Services)` for 2, 4 and 5.

---

## Data Flow

### Request Flow: proposal submission

```
Liam ──POST /v1/proposals (Authorization: session key; body)──▶ Host "AgentKey" handler
  Host: read raw body bytes (no model binding), build SubmissionContext
  └▶ SubmissionPipeline
       1 IntrospectStep ──SessionKey──▶ IPopeyeIntrospection (stub)
       2 PrincipalStep (pure)
       3 IdempotencyStep ──[tx A: set_config(tenant); SELECT by (tenant,agent,run_id)]──▶ PG
       4 SchemaCatalogStep (Contracts validator, ICatalog; computes body_sha256, proposal_hash)
       5 TraceStep ── cache? ──▶ ServiceTokenCache ──SessionKey──▶ IPopeyeTokenIssuer (stub)
                               ITraceResolver(trace_id, ServiceToken) (fixture)   [NO tx open]
                    Domain.TraceChecks 2..5
       6 PolicyStep (Domain.PolicyRules, ExpiryCalculator)
       7 PersistAndEnqueueStep ──[tx B: set_config; advisory lock(rate key); rate count;
                                  INSERT proposal ON CONFLICT DO NOTHING; INSERT proposal_event x2;
                                  INSERT queue_item; INSERT queue_item_event; COMMIT]──▶ PG
  ◀── SubmissionOutcome → ProblemDetails / 200|201 body (Host maps status codes)
```

### Request Flow: review and execution

```
Reviewer ─OIDC─▶ /queue (Razor) ─▶ QueueService.List(tenant)       [tx: set_config; SELECT ... WHERE tenant_id]
           claim ─▶ QueueService.Claim  [tx: CAS open→claimed; queue_item_event]
           decide ─▶ QueueService.Decide
                      IReviewerRoleChecker (at decision time) ─▶ Entra/dev stub
                      [tx: lazy expiry check → (expire+withdraw, commit, return proposal_expired)
                           | CAS claimed→decided; queue_item_event;
                             ActionApprovalKind.OnDecided: CAS proposal awaiting_approval→approved|rejected; proposal_event]
execute ─▶ ExecutionService.Execute(proposal_id)
           [tx: SELECT proposal FOR UPDATE; ledger row exists? → return it (REQ-ACT-03)
                expired? → expire, commit, proposal_expired
                state must be approved and trace_verification recorded
                IActionExecutor.ExecuteAsync(cmd keyed by proposal_id)   ← local file in Phase 1
                INSERT execution_ledger (execution_mode='simulated', approver, rationale ref, trace ref, body hash)
                CAS approved→executed|execution_failed; proposal_event; COMMIT]
```

Holding the row lock across the executor call is acceptable in Phase 1 (local file, per-proposal lock). The executor is target-idempotent on `proposal_id`, so a crash between the target write and commit is safe on retry: the target returns the earlier result and the ledger row is written then. Phase 2 must revisit this (a network call inside a DB transaction); flag it for that decision record.

### Who triggers execution

Not specified in the ratified record. Recommendation for Phase 1: execution is triggered in-process immediately after an approve decision commits (a `Channel`-fed `BackgroundService` or a direct call after commit), plus an idempotent `POST /internal/proposals/{id}/execute` used only by the demo script and tests. Never inside the approval transaction.

### Key Data Flows

1. **Tenant:** always from the introspected key (proposals) or the popeye service token (bluto items) or the reviewer's session (UI); never from a body. It flows into `set_config` and into every repository predicate from the same `TenantUnitOfWork.Tenant` value.
2. **Credentials:** session key → popeye ports only. Service token → hometown port only (and inbound on the bluto queue path). Neither appears in logs, rows, exceptions or problem details.
3. **Hashes:** `body_sha256` (whole v1 body, idempotency) and `proposal_hash` (the four keys, trace binding) are both JCS + SHA-256 from one implementation; both are stored on the proposal for audit.
4. **Evidence chain (core value):** `execution_ledger` → `proposal` (trace_ref, trace_verification, trace_schema_version, body_sha256, proposal_hash, key_id, on_behalf_of) → `proposal_event` (policy pass) → `queue_item_event` (who, when, rationale) — one SQL join answers "prove this action".

---

## Suggested Build Order (vertical slices)

Each slice ends with something runnable through the HTTP host (or UI) against Testcontainers Postgres, and its own TC/REQ tests green.

| Slice | Delivers end to end | Proves | Depends on |
|-------|---------------------|--------|------------|
| **S0 Solution skeleton and guards** | New solution layout; scaffold `Domain.cs` split into Domain/Catalog/Executor; `Olive.Architecture.Tests` (all four layers); BannedApiAnalyzers; `OLIVE_ENVIRONMENT` guard; CI workflow; vocabulary hash test; catalog loader on YamlDotNet | REQ-ACT-04 structurally, REQ-ACT-06, D§16.8 env rows | — |
| **S1 Walking skeleton: submit and read** | `Olive.Migrator` + `sql/migrations/0001_init.sql` (4 roles, `proposal`, `proposal_event`, ENABLE+FORCE RLS); `TenantUnitOfWork`; `POST /v1/proposals` with stub introspection, steps 1, 2, 4 and 7 only; `GET /v1/proposals/{id}`; v1 request schema | Postgres path, roles, RLS both-layers negative test, 401/403 paths, non-mutating 400 | S0 |
| **S2 Idempotency** | Step 3, JCS + body hash, unique constraint + race loop | 200 replay, 409 conflict, concurrent first submissions converge, 400 not stored | S1 |
| **S3 Trace verification** | Token issuer stub + token cache; fixture resolver + resolution cache; `contracts/trace-resolution/` fixtures; checks 1-5; `proposal_hash`; alarms; recording decorators | Every D§16.8 and §17.4 row, session-key capture | S2 (needs persisted refusals and the retry semantics of `trace_not_found`) |
| **S4 Policy and expiry** | Step 6 (purpose, entitlement intersection, DB rate limit not consumed by refusals), `not_in_catalog`, expiry calculation | Beat-4 precondition: refused_policy stored | S3 |
| **S5 Generic queue + action_approval** | Migration 0002: `queue_item`, `queue_item_event` modelled on IR-1 from day one (producer, producer_ref, subtype, ruleset_version, withdraw reason columns); kind registry; enqueue in step 7 tx; list/claim/decide/audit as application services and JSON endpoints; dev reviewer identity; approval rules; lazy expiry | TC-QUE-01..04, no self-approval, rationale required, beat 4 complete (never queued) | S4 |
| **S6 Reviewer UI** | Razor Pages list/claim/decide/audit; OIDC wiring behind `IReviewerIdentity`; dev IdP stub; scheme separation tests | UI path, antiforgery, role-at-decision-time | S5 |
| **S7 Execution and ledger** | Migration 0003: `execution_ledger` INSERT-only; `ExecutionService`; simulated executor target-idempotent; execution trigger; chain query; scripted beat 2 / beat 4 demo | REQ-ACT-03, REQ-ACT-05, TC-ACT-01..06, core-value chain from the execution record | S5 (S6 for the demo) |
| **S8 identity_review kind (IR-1)** | `POST /v1/queue/items`, withdraw, `GET` polling with `decided_after` cursor; service-token auth handler against popeye stub; create-body rules | TC-IRQ-01..11 (stub-only) | S5 |
| **S9 Contract and evidence pack** | OpenAPI 3.1 `$ref`-ing schemas, `COMPATIBILITY.md`, Liam fixtures, credential audit, docs/STATUS refresh | Milestone done-criteria | All |

**Ordering rationale:**
- Persistence and RLS go in S1, not later: D§10 bars in-memory stores outside unit tests, and every later slice's tests need the real isolation layers. Retro-fitting `set_config` discipline is where tenant leaks come from.
- Idempotency (S2) precedes trace (S3) because §16.3 semantics ("422 not stored, a retry with the same run_id is evaluated afresh") are only testable once the idempotency store exists.
- Trace (S3) precedes policy (S4) to mirror pipeline order and because the trace slice is the highest-risk, most spec-dense work; doing it early surfaces spec gaps while the popeye/hometown owners are engaged.
- The queue tables in S5 must be IR-1-shaped immediately, even though `identity_review` is registered only in S8, so S8 adds a kind and an endpoint, not a migration that rewrites queue states.
- S6 and S8 are independent of each other and of S7 once S5 exists; they can run in parallel.

**Research flags:** S3 (popeye stub shape must match `POPEYE_v0.4.2`, which the implementer must confirm present per D§4 stop rule), S5 (decision vocabulary for action_approval, expiry/withdraw reason codes), S6 (Entra group claims vs Graph lookup for decision-time role checks), S7 (execution trigger, lock across executor). S0, S1, S2 follow standard patterns.

---

## Scaling Considerations

| Scale | Architecture Adjustments |
|-------|--------------------------|
| POC (one agent, a handful of reviewers, tens of proposals per day) | Single Host instance; in-process caches; JSONL target. Nothing to optimise |
| Several instances | Everything correctness-critical is already in Postgres (idempotency, rate limit, state CAS, ledger uniqueness). Caches warm per instance. JSONL target needs a file lock or moves to a table |
| Many tenants and agents | Indexes on `queue_item (tenant_id, state, kind, created_at)`, `queue_item (tenant_id, decided_at, item_id)` for the IR-1 cursor, `proposal (tenant_id, agent_id, created_at)` for rate counting |

### Scaling Priorities

1. **First bottleneck:** rate-limit counting under the advisory lock if one agent floods; acceptable at POC volume, move to a counter row per window later.
2. **Second bottleneck:** the row lock held across a real executor call (Phase 2 only).

---

## Anti-Patterns

### Anti-Pattern 1: Model-binding the proposal into a DTO before validation
**What people do:** `[FromBody] ProposalRequest req` and validate the object.
**Why it's wrong:** System.Text.Json may coerce, default or drop unknown keys; the hash then covers something other than what was sent and check 5 fails honest proposals or passes altered ones (D§17.2.4).
**Do this instead:** Read raw bytes, parse to `JsonDocument` with duplicates rejected, validate the `JsonElement` against the schema, hash it, and only then map to a domain object.

### Anti-Pattern 2: Holding a DB transaction open across popeye/hometown calls
**What people do:** One transaction for the whole submission.
**Why it's wrong:** Outbound latency holds locks and pool connections; on a 503 you must roll back anyway.
**Do this instead:** Short read tx at step 3, no tx through steps 4-6 (except pure checks), one write tx at step 7 with the race loop.

### Anti-Pattern 3: Ordering pipeline steps through DI registration
**Why it's wrong:** The order is a ratified contract; DI order is invisible and easy to reshuffle.
**Do this instead:** Hard-coded sequence plus a test asserting it.

### Anti-Pattern 4: Throwing on expiry before recording it
**Why it's wrong:** The rollback discards the `expired` transition; the queue item stays actionable.
**Do this instead:** Commit the expiry transition, then return the distinct error.

### Anti-Pattern 5: One authentication scheme accepted everywhere
**What people do:** Default scheme with multiple handlers, so a reviewer cookie can call `/v1/proposals` or a bearer key can reach the UI.
**Why it's wrong:** Violates D§7 "caller classes are separate paths".
**Do this instead:** No default authenticate scheme; each endpoint group pins exactly one scheme via its authorization policy; integration tests try every credential against every route group.

### Anti-Pattern 6: Queue-kind logic as `if (kind == ...)` inside QueueService
**Why it's wrong:** Three consumers share the queue (charter §6.3); kind rules (roles, decisions, purposes, ref schemes, expiry) leak and conflict.
**Do this instead:** `IQueueKind` registry, source-controlled.

### Anti-Pattern 7: Trusting RLS and dropping the app predicate (or the reverse)
**Do this instead:** Both, always; the negative test disables each in turn (app predicate removed via a test-only raw query as `olive_app`; RLS removed via `ALTER TABLE ... DISABLE/NO FORCE ROW LEVEL SECURITY` in the throwaway container).

---

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| popeye introspection | `IPopeyeIntrospection`; Phase 1 fixture stub | Claim set only from `POPEYE_v0.4.2` (D§4 stop rule; do not infer from Liam spec) |
| popeye token issuance | `IPopeyeTokenIssuer`; Phase 1 stub with refusal and claim-disagreement fixtures | Not ratified on popeye's side (D§17.1) |
| hometown resolution | `ITraceResolver`; Phase 1 fixture | Live switch = new adapter + `Source = hometown`; transport-only |
| Entra ID | OIDC (Microsoft.Identity.Web or plain OpenIdConnect handler) behind `IReviewerIdentity`; dev IdP stub | Group-claim overage means decision-time membership may need a Graph call, which is an outbound HTTP dependency: place it in `Olive.Integrations.Entra` and allowlist it explicitly when it arrives |
| bluto | Inbound only: create/withdraw/poll on `/v1/queue/items`; stub-only | Olive never calls bluto (IR-1§6) |
| JSONL target | `Olive.Executor.Simulated` | The target, not the ledger (D§10) |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| Host ↔ Application | Direct calls to use-case classes; outcomes are values mapped to HTTP by Host | Host has no SQL, no rules |
| Application ↔ adapters | Port interfaces, DI | Adapters never reference each other |
| QueueService ↔ ActionApprovalKind ↔ proposal store | Same `TenantUnitOfWork` | Queue decision and proposal transition commit atomically |
| ExecutionService ↔ executor | Port call inside the per-proposal locked tx | Revisit for Phase 2 |

---

## Gaps and Decisions to Surface

- **Hash vs validation order at step 3:** D§15.2 runs idempotency before schema validation, while D§15.3 says the hash is taken "after schema validation". With non-mutating validation the bytes are identical, so hash-then-lookup is consistent, but a malformed body reusing an existing `run_id` gets `409` rather than `400`. Confirm with the ratifier.
- **Entitlement source:** D§7's minimum claim table has no entitlements claim, yet policy needs the agent ∩ `on_behalf_of` intersection. Source must come from `POPEYE_v0.4.2` or the vocabulary §3.2; do not invent it.
- **`expires_in_seconds` larger than allowed:** clamp or `400`? "May shorten, never lengthen" suggests `400`; confirm.
- **Catalog parameter-schema failure:** treated here as `400` (not stored). Confirm vs `refused_policy`.
- **Fourth/fifth tables:** `queue_item_event` (IR-1 decision history) and `schema_migrations` are additions to D§13's four-table list; they need the same RLS/grant treatment (`queue_item_event` INSERT-only for `olive_app`).
- **Execution trigger** (who calls execute after approval) is unspecified.

## Sources

- `specs/DECISION_olive-poc2-phase1_v0.5.md` §6-§17 (authoritative; HIGH)
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (authoritative; HIGH)
- `.planning/codebase/ARCHITECTURE.md`, `.planning/codebase/CONCERNS.md` (scaffold defects this design closes)
- [NetArchTest vs ArchUnitNET vs Roslyn analyzers](https://www.dotnet-guide.com/articles/software-architecture/netarchtest-archunitnet-roslyn-analyzers/) (MEDIUM; recommends the maintained NetArchTest.eNhancedEdition fork)
- [NetArchTest wiki](https://github.com/BenMorris/NetArchTest/wiki)
- [BannedApiAnalyzers help](https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md) (doc-ID format; MEDIUM)
- [RFC 8785](https://rfc-editor.org/rfc/rfc8785.html), [Jcs.Net](https://www.nuget.org/packages/Jcs.Net), [MackySoft.Json.Canonicalization](https://www.nuget.org/packages/MackySoft.Json.Canonicalization) (packages are 0.1.x; LOW maturity, pin RFC vectors)
- [JsonSerializerOptions.AllowDuplicateProperties](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.allowduplicateproperties), [dotnet/docs commit](https://github.com/dotnet/docs/commit/5af705fa0d330cb330bc772c75f6a71afc62fe72) (`JsonDocumentOptions` member version unconfirmed; MEDIUM)
- PostgreSQL `set_config(name, value, is_local)` transaction scoping and FORCE RLS behaviour: [Bytebase RLS reference](https://bytebase.com/reference/postgres/how-to/postgres-row-level-security), [wener.me tenant notes](https://wener.me/notes/db/relational/postgresql/tenant) (MEDIUM; consistent with PostgreSQL docs from training)

---
*Architecture research for: Olive action broker and shared review queue (Phase 1, simulation-only)*
*Researched: 2026-10-21*
