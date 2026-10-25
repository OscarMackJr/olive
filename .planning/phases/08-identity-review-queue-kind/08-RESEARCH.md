# Phase 8: Identity Review Queue Kind - Research

**Researched:** 2026-10-25
**Domain:** ASP.NET Core 10 credential dispatch on a shared route, PostgreSQL forward-only migration and compare-and-set transitions, strict config registries, Razor reviewer pages
**Confidence:** HIGH (everything below was read from the repo at commit bf4475b or from the ASP.NET Core docs; the few `[ASSUMED]` items are listed in the Assumptions Log)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

Carried forward, not re-decided:
- **IR-1 v0.1 (ratified 2026-10-21) fixes the wire contract.** Routes, the six-field create body, `400 server_assigned_field` (naming no value), `400 invalid_request` for unknown fields, idempotency on `(producer, tenant_id, producer_ref)` over the RFC 8785 canonical body (`200` replay / `409 idempotency_conflict`), withdraw reason codes `superseded_by_rule | source_changed | ruleset_retired`, no expiry, no policy check before queueing.
- **Phase 5 rulings.** Only the claimant can release (Q7) or decide (Q8). Callers are hard-separated (D-02). The tenant comes only from the server-side map (D-08). The role is checked at decide time from the kind registry's `required_role` (D-09). Problem+json reason codes never echo values (D-04). Missing and cross-tenant items get indistinguishable 404s.
- **Phase 7 rulings.** The UI stays minimal: no dashboards, bulk actions, SLA views or assignment rules. Antiforgery and PRG on every POST. Flash messages carry static codes only.
- **Any change to IR-1 needs both owners' agreement and re-ratification.** Nothing here changes IR-1 except as flagged in D-11.

Shared `/v1/queue/items` path
- **D-01: Dispatch by credential and keep IR-1's paths exactly.** A policy or forwarding authentication scheme on `/v1/queue/items` chooses the handler from the credential presented. A service Bearer token reaches the producer endpoints (`POST /v1/queue/items`, `POST /v1/queue/items/{id}/withdraw`, producer `GET /v1/queue/items` poll). A reviewer credential (DevReviewer header, or the reviewer cookie where Phase 7 admits it) reaches the existing reviewer endpoints. Create and withdraw are service-only. On `GET /v1/queue/items` the credential decides which view is returned. No reviewer route is moved.
- **D-02: The producer GET is narrow.** Only `kind=identity_review&state=decided` plus optional `decided_after`; any other filter combination gets `400 invalid_request`. Returns only items where `producer` equals the caller's producer, in the token's tenant (application predicate on top of forced RLS).
- **D-03: Reviewers see one shared queue.** Reviewer list (JSON API and `/review`) shows both kinds together, oldest first, with a kind badge. Existing `kind`/`state` filters keep working. Items whose `required_role` the reviewer lacks are still listed; the role gates deciding and the payload view, not visibility.
- **D-04: Extend the Phase 7 `CredentialRouteMatrixTests`.** Add a service-token row and columns for producer routes (create, withdraw, poll). Only intended pairings admitted. Rows must show: service token refused on `/v1/proposals`, `/review`, `/dev` and every reviewer action; agent keys refused on producer routes; reviewer and user credentials refused on create and withdraw.

Service-token authentication and producer registry
- **D-05: A new inbound verifier port checks the service token.** bluto sends `Authorization: Bearer <token>`. New port `IServiceTokenVerifier` returns `{active, principal_type=application, client_id, tenant_id, exp}`, no `delegated_agent_id`; a token carrying a delegated agent is refused. Stub is fixture-backed (same pattern as `StubKeyIntrospector`), `INonProductionOnly`, barred in production by FND-05. Kept separate from `IKeyIntrospector`. Verifier unavailable or malformed: fail closed with 503 (Phase 3 alarm pattern). Token never logged or stored; IDN-08 sentinel scans cover it.
- **D-06: Producers come from a source-controlled `config/producers.yaml`.** Maps token `client_id` to a `producer` (`bluto`) and that producer's allowed `kinds`/`subtypes` (`identity_review`/`candidate_pair`). Strict-parsed, validated at startup against `config/queue-kinds.yaml` (unregistered kind/subtype fails startup, as does a ref scheme that does not match). Like `config/agents.yaml`, not overridable by env var or appsettings. Unknown `client_id` gets 403. Producer never taken from the body.
- **D-07: The wrong caller type gets `403 principal_not_permitted`.** Agent key, user credential or reviewer credential on a producer-only route, and a service token on `/v1/proposals`. Reuses Phase 5 D-02. 401 is kept for missing, inactive or invalid credentials.
- **D-08: An unknown tenant fails closed.** A token that verifies but whose `tenant_id` is not a known vocabulary tenant UUID gets 403 with a static reason code (e.g. `tenant_not_permitted`, name at Claude's discretion). No item created, nothing echoed.

Identity items for reviewers
- **D-09: A defer clears the claim and returns the item to `open`.** Works like a release that also records a decision. `state=open`, `claimed_by` cleared. A `deferred` event is written with `decided_by` and `rationale`, but none of the item's `decision`, `decided_by`, `decided_at` or `rationale` columns are set (`queue_item_state_shape` CHECK requires null while open). Any steward in the tenant can claim next; no soft hold. `accept`/`reject` move the item to `decided`. identity_review decisions change no proposal.
- **D-10: The item page for an identity item shows references only.** Kind, subtype, `subject_ref`, `payload_ref`, `producer_ref`, `ruleset_version`, age, claim state and history, as encoded monospace text. A static note says evidence comes from bluto's evidence endpoint under the reviewer's own credential. No clickable link, no absolute URL, no outbound call (CSP and `UiSurfaceTests` unchanged). Olive never resolves `bluto:review-evidence/...`.
- **D-11: The decision form is driven by the kind.** Buttons come from the kind registry: `accept / reject / defer` for identity_review, `approve / reject` for action_approval. For identity items the rationale help text reads, in substance, "Do not include names, dates, identifiers or other record values." A rationale is required for all three decisions, including defer. Self-approval and post-approval execution apply only to action_approval.
- **D-12: Add steward fixtures.** New fixtures in `contracts/reviewer/` (e.g. `alpha-steward-1`, `alpha-steward-2`, `beta-steward`, plus a supervisor-only identity without steward) so TC-IRQ-04 can prove the refusal. Add `identity_steward` to the `EntraClaimsMapper` role allowlist (registry-derived; check whether it already appears). The Entra group stays unnamed, so live decisions remain gated.

Polling and withdraw
- **D-13: The cursor is an opaque `decided_after` token** (base64url of `(decided_at, item_id)`). Ordered `(decided_at, item_id)` ascending. First poll omits `decided_after`. Each response returns a `next_cursor`. Malformed cursor: `400 invalid_request`. Flag for planning: IR-1 does not specify the response envelope, so `next_cursor` should be named as an additive response field when shared with the bluto owner.
- **D-14: Each poll item carries the decision fields but no rationale.** Fields: `item_id`, `kind`, `subtype`, `producer_ref`, `subject_ref`, `payload_ref`, `ruleset_version`, `state`, `decision`, `decided_by`, `decided_at`. Rationale stays in Olive's audit only.
- **D-15: The poll returns only accept and reject decisions.** Only `state=decided`. Defer events stay in Olive's history.
- **D-16: The page size is fixed at 100** (`QueueService.DefaultLimit`). No client-chosen limit. bluto follows `next_cursor` until a short page.
- **D-17: bluto can withdraw any undecided item, including a claimed one.** Withdraw clears any claim, sets `state=withdrawn` with the reason code, writes a `queue_item_event`, all in one tenant transaction. The claimant's next decide or release gets 409 with a static code (e.g. `item_withdrawn`). Withdrawing a decided item gets 409 (TC-IRQ-09). Missing, other-tenant or other-producer item: indistinguishable 404. Unknown reason code: `400 invalid_request`.
- **D-18: Ship a scripted demo, run in CI.** xUnit integration test tagged `[Trait("Demo", ...)]`, over HTTP against Testcontainers Postgres, with a one-line runner script under `build/` (same pattern as Phase 5 D-16 and Beat 2). Stub bluto creates an item, gets an idempotent replay, gets `server_assigned_field` for `tenant_id`. A steward claims it, defers it, re-claims it and accepts it. bluto polls the decision with the cursor and withdraws a second item. Writes a markdown transcript containing no record values or rationale text.

### Claude's Discretion
- How the dispatch mechanism is built (forwarding policy scheme, endpoint filters, or separate route handlers on the same pattern). Must keep the Phase 7 rule that schemes are named only per route group, and D-01 must hold.
- The `IServiceTokenVerifier` fixture format and location (e.g. `contracts/service-token/` with a schema and valid/invalid examples, following `contracts/introspection/`), and the `producers.yaml` schema.
- Reason-code names not fixed by IR-1 or Phase 5: `tenant_not_permitted`, `item_withdrawn`, unknown producer, and token-with-delegated-agent.
- Storage for create idempotency. `queue_item` already has `producer`, `producer_ref`, `ruleset_version`, the bluto ref CHECK and `unique (tenant_id, producer, producer_ref)` (migration `0008_review_queue.sql`). No `body_sha256`, so expect a forward-only `0010_*` migration (column, grants, maybe the `deferred` event type) following the Phase 1 RLS and grant pattern.
- How the `deferred` event is represented in `queue_item_event`, and how a defer appears on the audit page.
- What the reviewer `GET /v1/queue/items/{id}/payload` returns for an identity item (refs only, or 404, or `no_payload`). It must never resolve bluto evidence.
- Whether the same reviewer may re-claim right after their own defer (default allowed, Q7 has no cool-down).
- Whether the poll cursor is HMAC-protected or plain base64url. Opaque is required; integrity protection is optional.

### Deferred Ideas (OUT OF SCOPE)
- `link_challenge` (splits) with `uphold | split | defer`. Reserved until bluto's merge and split semantics are approved (IR-1 §7.1).
- A clickable evidence link to bluto's evidence endpoint.
- A client-chosen poll page size, and producer listing of non-decided states.
- Live popeye service tokens and the bluto registry entry (IR-1 §8 gate).
- Naming the `role:identity_steward` Entra group.
- IR-1 §7 items 2-4: purpose for evidence reads, untenanted records, cross-tenant candidates.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| IRQ-01 | bluto (stub traffic) creates `identity_review`/`candidate_pair` items under IR-1 create-body rules; `tenant_id`/`producer` rejected; `link_challenge` rejected | Create handler order (parse, server-assigned check, unknown-field check, shape validation, JCS hash, insert-or-replay); `producers.yaml`; migration 0010 `body_sha256`; contract schemas |
| IRQ-02 | Reviewer can accept, reject or defer; decision recorded in history | `QueueService.DecideAsync` branch for identity kind; new store method; `deferred` event type; steward fixtures; Razor kind-driven form |
| IRQ-03 | bluto polls decisions via IR-1 mechanism and withdraws its own items | Poll SQL with `(decided_at,item_id)` cursor; withdraw SQL clearing the claim; producer-scoped 404s |
| IRQ-04 | Inbound path authenticates with a stubbed popeye service token; no live traffic | `IServiceTokenVerifier` + stub + fixtures; `ServiceTokenAuthenticationHandler`; FND-05 marker; startup cross-validation |
| IDN-03 | Agents/users cannot reach the service-caller path; proposal endpoint never accepts a service credential | `QueueCaller` policy scheme + per-subgroup caller filters; `/v1/proposals` principal check already refuses non-agent; extended `CredentialRouteMatrixTests` |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

CLAUDE.md's stack section is partly stale (describes the early single-file scaffold). Directives that still bind this phase:
- No write-capable credential anywhere; session key goes only to popeye; tokens never logged or stored; fail closed on any resolver/auth failure.
- Queue payloads are references only (REQ-QUE-01). Queue-item schema is `additionalProperties: false`.
- Tenant isolation has two layers (application predicate AND forced RLS); keep both. Every new SQL statement carries `tenant_id = @tenant_id`.
- Never edit `contracts/vendored/vocabulary.yaml`. Never edit `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (bluto pins its SHA-256, IR-1 §10).
- No `HttpClient` or outbound network assemblies in `src` (enforced by `ArchitectureTests`, `BannedSymbols.txt`).
- Policy decision/problem `Detail` text is static; never include request values.
- New behavior cites a requirement ID in the test name (`IRQ_01_...`). Contracts get paired `.valid.json`/`.invalid.json`.
- Changes outside a GSD workflow are not allowed; no project skills directories exist.

## Summary

The shared-queue foundation from Phase 5 and the pages from Phase 7 already carry almost everything an identity item needs: `queue_item` has `producer`, `ruleset_version`, the bluto ref CHECKs, the `(tenant_id, producer, producer_ref)` unique key, a `decision in (accept, reject, defer)` CHECK, and the registry already defines `identity_review` with `required_role: role:identity_steward`. `RoleAllowlist.FromRegistry` already derives `identity_steward` from the registry, so D-12's allowlist question is answered: nothing to add [VERIFIED: src/Olive.Integrations.Entra/EntraClaimsMapper.cs]. The work is: one migration (0010), a service-token verification path, a credential-dispatching authentication scheme on the existing group, a producer-side create/withdraw/poll slice in `QueueService` + `PostgresQueueStore`, an identity branch of decide (including the defer transition), and page changes.

Four things in the code would silently break the phase if the plan follows CONTEXT.md literally, and each is called out in Common Pitfalls: (1) agent keys and service tokens both arrive as `Authorization: Bearer ...`, so the dispatch must be by header shape plus verifier result, and the stub verifier must recognise the fixture agent/user keys to keep today's `403 principal_not_permitted`; (2) `IDN_08_only_hometown_ports_accept_a_service_token_or_grant` forbids the existing `ServiceToken` type on any non-hometown port, so `IServiceTokenVerifier` needs its own redacting inbound-token type; (3) `ItemModel` and `AuditModel` treat `PayloadResult.NotFound` as a 404, and an identity item has no proposal row, so every identity item page would 404 until a refs-only payload result exists; (4) `JsonGate.TryAdmit` demands a `run_id`, so the create body cannot use it (use `JsonGate.Walk` after a plain strict parse).

**Primary recommendation:** Add a policy scheme `QueueCaller` named only by the `/v1/queue/items` group whose `ForwardDefaultSelector` picks `ServiceToken` when an `Authorization` header is present and no dev-reviewer header is, otherwise `DevReviewer`; enforce caller type per route with two endpoint filters (`ReviewerCaller`, `ProducerCaller`); add migration `0010_identity_review_producer.sql`; implement identity decide/defer as a separate store method so the action_approval path stays byte-for-byte unchanged.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Service-token verification (stub) | API / Backend (auth handler + `Olive.Integrations.Popeye` stub) | — | Inbound credential check; popeye is the only system allowed to see tokens (IDN-08) |
| Caller-type dispatch on `/v1/queue/items` | API / Backend (auth scheme + endpoint filters) | — | Must run before body read or store access (fail closed) |
| Producer registry + cross-validation | API / Backend (`Olive.Catalog` config registry, startup guard) | — | Source-controlled config, like `agents.yaml` |
| Create idempotency (JCS hash, replay/conflict) | API / Backend (endpoint + `QueueService`) | Database (unique key, `body_sha256` CHECK) | Canonical hash computed in app; DB enforces uniqueness and format |
| Item state transitions (create, claim, defer, decide, withdraw) | Database / Storage (guarded compare-and-set under row lock) | API (`QueueService` rules) | Existing pattern: `LockItem` + guarded UPDATE + INSERT-only event |
| Tenant isolation | Database (forced RLS) | API (predicate in every statement) | Both layers kept |
| Decision polling + cursor | API / Backend (cursor codec, endpoint) | Database (ordered query, partial index) | Cursor is a pure encoding of `(decided_at, item_id)` |
| Reviewer pages (kind badge, kind-driven form, refs-only view) | Frontend Server (Razor Pages) | API (`QueueService`) | Pages call `QueueService` only; no new routes |
| Evidence resolution | Not Olive | bluto (reviewer's client) | IR-1 §5: Olive never resolves `bluto:review-evidence/...` |

## Standard Stack

No new packages. Everything is already referenced.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| ASP.NET Core / .NET | net10.0 (SDK 10.0.400 installed) | Auth schemes, endpoint filters, Razor Pages | Already the host |
| Npgsql + Dapper | pinned in `Directory.Packages.props` (Persistence only) | Tenant-scoped SQL | Existing `TenantUnitOfWork`/`PostgresQueueStore` pattern |
| YamlDotNet | pinned (Catalog only) | Strict-parse `config/producers.yaml` | Same as `AgentAllowlist`/`QueueKindRegistry` |
| JsonSchema.Net via `SchemaValidator` | pinned (Catalog only) | Contract schema tests for new shapes | Existing `QueueSchemaTests` pattern |
| `Olive.Domain.Jcs` | in-repo | RFC 8785 canonical body + `Sha256Hex` for `body_sha256` | Same canonical form as proposal idempotency |
| xunit.v3 + Testcontainers | pinned in tests | Tests | Existing |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Policy scheme + endpoint filters | `AuthenticationSchemes = "DevReviewer,ServiceToken"` on the group | Multi-scheme policies challenge every scheme and write two responses on failure; the selector gives one deterministic challenge |
| Policy scheme + endpoint filters | Two route groups on the same template | ASP.NET routing cannot pick between two identical templates by credential without a custom `MatcherPolicy`; far more machinery |
| `System.Buffers.Text.Base64Url` for the cursor | hand-rolled `Replace('+','-')` | `Base64Url` is in the BCL since .NET 9 [CITED: learn.microsoft.com/dotnet/api/system.buffers.text.base64url]; use it, no package |

**Installation:** none. `ArchitectureTests.FND_02_direct_packages_match_allowlist_in_lockfiles` must stay green, so do not add a package reference to any project.

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| (none) | — | — | — | — | — | No external packages are installed by this phase |

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

## Architecture Patterns

### System Architecture Diagram

```
 bluto (stub)                                    reviewer (steward)
 Authorization: Bearer <svc token>               X-Olive-Dev-Reviewer: <id>  (cookie not admitted on /v1)
        |                                                   |
        +----------------------+----------------------------+
                               v
              /v1/queue/items group  (RequireAuthorization, scheme "QueueCaller")
                               |
          ForwardDefaultSelector (header shape only, no I/O)
             dev header present  -> "DevReviewer"
             Authorization only  -> "ServiceToken"
             neither             -> "DevReviewer"  (401 reviewer_unauthenticated, as today)
                    |                              |
     ServiceTokenAuthenticationHandler      DevReviewerAuthenticationHandler
       IServiceTokenVerifier (stub, popeye)    IReviewerIdentitySource + tenant map
       503 on unavailable/malformed            claims: oid, tid, groups, tenant
       401 on inactive/unknown                         |
       success -> principal_type, client_id,           |
                  tenant, delegated_agent claims       |
                    |                                  |
     +--------------+---------------+------------------+---------------+
     v                              v                                  v
 ProducerCaller filter        ReviewerCaller filter              GET "" dispatches on
  application && no agent      DevReviewer principal             principal kind:
  client_id in producers.yaml  tenant claim (existing 403)        reviewer -> ListAsync (existing)
  tenant in known tenants      else 403 principal_not_permitted   producer -> PollAsync
  else 403 (3 static codes)
     |                              |
 POST ""   create                GET {id} / payload / events
 POST {id}/withdraw              POST {id}/claim | release | decision
 GET ""    poll                         |
     v                                  v
 QueueService.Create/Withdraw/Poll    QueueService.Claim/Release/Decide (kind branch)
     v                                  v
 PostgresQueueStore  --- TenantUnitOfWork (set_config olive.tenant_id, one tx) ---> Postgres (forced RLS)
   INSERT ... ON CONFLICT DO NOTHING        LockItem FOR UPDATE + guarded UPDATE
   + created event                          + INSERT-only queue_item_event
   withdraw: clear claim + event            accept/reject -> decided ; defer -> open + 'deferred' event
```

### Recommended Project Structure
```
config/producers.yaml                       # NEW: client_id -> producer, kinds, subtypes (embedded by existing config/*.yaml glob)
contracts/service-token/                    # NEW: README, index.json, *.json fixtures, schema (+valid/invalid)
contracts/queue-item/v1/                    # EXTEND: identity-create, withdraw-request, poll-response schemas (+pairs)
contracts/reviewer/                         # EXTEND: alpha-steward-1/2, beta-steward (+ index.json)
sql/migrations/0010_identity_review_producer.sql
src/Olive.Domain/InboundBearerToken.cs      # NEW redacting type (not ServiceToken, see Pitfall 2)
src/Olive.Application/IServiceTokenVerifier.cs, ServiceTokenResult.cs, IProducerRegistry.cs, ProducerCursor.cs
src/Olive.Catalog/ProducerRegistry.cs       # strict YAML + cross-validation helper
src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs  (+ csproj EmbeddedResource for contracts/service-token/*.json)
src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs, QueueCallerFilters.cs
src/Olive.Host/Endpoints/QueueEndpoints.cs  # producer handlers + filters
build/demo-identity-review.sh
tests/Olive.Tests/IdentityReview*Tests.cs, ServiceToken*Tests.cs, ProducerRegistryTests.cs, IdentityReviewDemoTests.cs
```

### Pattern 1: Dispatch by credential with a policy scheme, caller type by endpoint filter
**What:** one policy scheme named only by the queue group; the selector looks at header names only (no verifier call, so no I/O in the selector); caller-type enforcement is an explicit filter per route, returning `403 principal_not_permitted` via `ProblemResults`.
**When to use:** any route shared by two credential families.
**Example:**
```csharp
// Source: https://learn.microsoft.com/aspnet/core/security/authentication/policyschemes (ForwardDefaultSelector; all of
// authenticate/challenge/forbid are forwarded to the selected scheme)
public static class QueueCaller
{
    public const string SchemeName = "QueueCaller";
    public static string Select(HttpContext ctx) =>
        !ctx.Request.Headers.ContainsKey(DevReviewerAuthenticationHandler.HeaderName)
        && ctx.Request.Headers.ContainsKey(AgentKeyHeader.Name)
            ? ServiceTokenAuthenticationHandler.SchemeName
            : DevReviewerAuthenticationHandler.SchemeName;
}

// Program.cs, inside the existing AddAuthentication(...) chain
.AddScheme<AuthenticationSchemeOptions, ServiceTokenAuthenticationHandler>(ServiceTokenAuthenticationHandler.SchemeName, _ => { })
.AddPolicyScheme(QueueCaller.SchemeName, null, o => o.ForwardDefaultSelector = QueueCaller.Select)

// QueueEndpoints.cs
var items = app.MapGroup("/v1/queue/items")
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = QueueCaller.SchemeName });
items.MapGet("", ListOrPollAsync);                                   // handler branches on caller kind, see Pattern 2
items.MapPost("", CreateAsync).AddEndpointFilter(QueueCallerFilters.ProducerOnly);
items.MapPost("/{id:guid}/withdraw", WithdrawAsync).AddEndpointFilter(QueueCallerFilters.ProducerOnly);
foreach (var r in new[] { items.MapGet("/{id:guid}", GetAsync), /* payload, events, claim, release, decision */ })
    r.AddEndpointFilter(QueueCallerFilters.ReviewerOnly);            // = today's tenant filter + caller-type check
```
The reviewer GET list needs the same checks inline (it cannot carry a static filter because the same endpoint also serves the producer). Make the two filter bodies public static methods (`ReviewerCheck(HttpContext)`, `ProducerCheck(HttpContext)` returning `IResult?`) and call the right one at the top of `ListOrPollAsync`.

**Why this satisfies "schemes named only per route group":** `QueueCaller` is named only by the `/v1/queue/items` group; `ServiceToken` and `DevReviewer` are named only inside the selector; `AgentKey` stays the default scheme and keeps returning `NoResult` outside `/v1/proposals` and `/v1/catalog`; the cookie scheme is still named only by the `ReviewerUi` policy. `ServiceTokenAuthenticationHandler.HandleAuthenticateAsync` must itself return `NoResult` unless `Request.Path.StartsWithSegments("/v1/queue/items")` (defence in depth, mirrors `AgentKeyAuthenticationHandler`).

**Challenge/forbid behaviour to preserve (matrix today):** no credential or cookie-only -> selector returns `DevReviewer` -> `401 reviewer_unauthenticated` (unchanged); dev header -> `DevReviewer`; Bearer only -> `ServiceToken`.

### Pattern 2: Producer vs reviewer view on `GET /v1/queue/items`
Branch on `http.User.Identity.AuthenticationType` (both handlers build principals with `Scheme.Name` as the authentication type: `ReviewerClaims.ToPrincipal(..., Scheme.Name)` and the new service principal). Producer branch validates `Request.Query.Keys` is exactly `{kind, state}` or `{kind, state, decided_after}` with `kind=identity_review`, `state=decided`; anything else `400 invalid_request`. Reviewer branch is today's `ListAsync` unchanged (D-03: both kinds already appear because `QueueSql.List` filters only by optional kind/state).

### Pattern 3: Service-token handler (fail closed, no secret in logs)
```csharp
// Source: pattern of src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
{
    if (!Request.Path.StartsWithSegments("/v1/queue/items", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
    var token = InboundBearerToken.TryRead(Request.Headers.Authorization.ToString());   // same parsing rules as AgentKeyHeader.TryRead
    if (token is null) return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
    var r = await verifier.VerifyAsync(token, Context.RequestAborted);                   // IntrospectionUnavailableException propagates -> 503
    if (!r.Active || r.Caller is null || r.Caller.ExpiresAt <= time.GetUtcNow()) return AuthenticateResult.Fail(ProblemReasons.KeyInactive);
    return AuthenticateResult.Success(new AuthenticationTicket(ServiceClaims.ToPrincipal(r.Caller, Scheme.Name), Scheme.Name));
}
protected override Task HandleChallengeAsync(AuthenticationProperties p) { Response.Headers.WWWAuthenticate = "Bearer"; return ProblemResults.WriteAsync(Context, 401, ProblemReasons.KeyInactive); }
protected override Task HandleForbiddenAsync(AuthenticationProperties p) => ProblemResults.WriteAsync(Context, 403, ProblemReasons.PrincipalNotPermitted);
```
Reuse `IntrospectionUnavailableException` / `IntrospectionCallerRejectedException` so `ProblemExceptionHandler` already produces the 503 and the alarm [VERIFIED: src/Olive.Host/Problems/ProblemExceptionHandler.cs]. A new reason code is optional; the title for `introspection_unavailable` reads "Key introspection is unavailable.", acceptable for the stub, but a dedicated `service_token_unavailable` is cleaner if the planner wants distinct alarms (Discretion).

### Pattern 4: Producer-side store operations (all inside one `uow.ExecuteAsync`)
- **Create:** `INSERT ... ON CONFLICT (tenant_id, producer, producer_ref) DO NOTHING RETURNING item_id`. If a row returns, insert the `created` event (`from_state` null, `to_state` open, actor = producer name, detail null) in the same tx and return `Created`. If nothing returned, `SELECT item_id, state, body_sha256 ... WHERE tenant_id AND producer AND producer_ref` and return `Replay` (hash equal) or `Conflict` (hash differs). Do not catch `23505`: a failed statement aborts the Postgres transaction, so the re-read would fail.
- **Withdraw:** `LockItem` (FOR UPDATE) first, then check `producer`/`kind` (mismatch -> NotFound, indistinguishable), then state: `decided` -> `Closed` (409 `item_closed`), `withdrawn` -> idempotent (see Open Questions), `open|claimed` -> guarded UPDATE that also clears the claim, then event.
- **Poll:** single SELECT, no write, no lock.
- **Identity decide:** new `DecideIdentityAsync` (do not touch the action_approval `DecideAsync`).

### Anti-Patterns to Avoid
- **Passing the service token through `SessionKey`/`ServiceToken`/`ServiceTokenGrant`:** trips the IDN-08 architecture tests (Pitfall 2).
- **Loosening `AgentKeyAuthenticationHandler`'s path scope to include `/v1/queue`:** it must keep returning `NoResult` there; the agent key is introspected only on agent routes (the matrix asserts the introspector call count is unchanged on queue routes).
- **Taking `producer` or `tenant_id` from the body, header or query:** both come from the verified token plus `producers.yaml` only.
- **Hashing a body that was rewritten or defaulted:** reject server-assigned/unknown fields first, then hash exactly the parsed tree.
- **A trigger or DB-side function to enforce transitions:** the project convention is guarded UPDATE under `FOR UPDATE` plus row-count check.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Canonical JSON + hash | custom serializer | `Jcs.Canonicalize(root)` then `Jcs.Sha256Hex(bytes)` | RFC 8785 vectors already pinned in `contracts/jcs/v1`; same form as proposal idempotency |
| I-JSON well-formedness (duplicates, lone surrogates, lossy numbers) | own walker | `JsonDocument.Parse(bytes, {MaxDepth=64, AllowDuplicateProperties=false})` + `JsonGate.Walk(root)` | `JsonGate.TryAdmit` itself cannot be used (requires `run_id`), but `Walk` is public |
| Bounded body read | own stream loop | `ProposalEndpoints.ReadBoundedAsync` (internal, same assembly) | Same 413 behaviour |
| Strict YAML registry | ad-hoc parser | `DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)` without `IgnoreUnmatchedProperties`, as in `AgentAllowlist` | Unknown keys fail startup |
| Base64url | manual char replace | `System.Buffers.Text.Base64Url` | BCL, strict decoding |
| Problem responses | new writer | `ProblemResults.Result/WriteAsync` + new constants in `ProblemReasons` and a title in `ProblemResults.Title` | `ProblemReasonTitleTests` checks every reason has a static title |
| One-winner transitions | app-level locks | `LockItem` + guarded UPDATE + row-count check inside `TenantUnitOfWork` | Existing concurrency tests rely on it |
| Stub non-production gate | a config flag | `INonProductionOnly` marker | FND-05 walks registrations and resolved instances |

**Key insight:** every hard part (canonical hashing, strict config, tenant transaction, CAS transitions, fixture stubs, FND-05) already exists. The phase is composition plus the four traps below.

## Runtime State Inventory

Not a rename/refactor/migration phase. Omitted.

## Common Pitfalls

### Pitfall 1: Agent keys and service tokens share `Authorization: Bearer`
**What goes wrong:** the dispatch cannot tell them apart by header shape. An agent key (`fixture-key-liam-alpha`) sent to `/v1/queue/items` reaches the service verifier.
**Why:** `AgentKeyHeader.Name = "Authorization"`, `Scheme = "Bearer"`; today the DevReviewer challenge heuristic turns "Authorization and no dev header" into `403 principal_not_permitted`, which the matrix (`Cred.AgentKey x Route.QueueItems = NotPermitted`) and `QueueEventsTests.QUE_09_events_are_tenant_scoped_and_agents_are_refused` pin.
**How to avoid:** the stub `IServiceTokenVerifier` fixture index must also contain the existing visible agent/user keys, returning `active: true` with `principal_type` agent/user (a documented divergence, exactly like `contracts/introspection/user.json`). The handler then authenticates successfully and the caller-type filter returns 403. Truly unknown bearer strings return 401 `key_inactive` (note: previously 403 on queue routes; no existing test sends an unknown bearer to `/v1/queue`, verified by grep). A real popeye verifier's behaviour for agent keys is unratified, so tag this `[ASSUMED]` and keep the 403 logic in Olive, not in the stub.
**Warning signs:** matrix row `AgentKey x QueueItems` returns 401 instead of 403.

### Pitfall 2: `IDN_08_only_hometown_ports_accept_a_service_token_or_grant`
**What goes wrong:** declaring `IServiceTokenVerifier.VerifyAsync(ServiceToken token, ...)` fails the architecture test (only Hometown-destination ports may take `ServiceToken`/`ServiceTokenGrant`). Using `SessionKey` is legal only on popeye ports and is semantically wrong.
**How to avoid:** add `Olive.Domain.InboundBearerToken` (sealed class, `[DebuggerDisplay("[REDACTED]")]`, `RedactingJsonConverter`, `ToString()` returns `"[REDACTED]"`, `ExposeSecret()`; modelled on `ServiceToken`/`SessionKey`). Mark the port `[OutboundPort(OutboundDestination.Popeye)]`. Extend `ArchitectureTests`: add the new type to a third list and assert it appears only on Popeye-destination ports, and non-vacuously that the verifier port does carry it. `IDN_08_every_application_port_implemented_in_an_integration_assembly_is_marked_outbound` also requires the attribute on the new port.

### Pitfall 3: Identity items 404 on the Razor pages and the payload API
**What goes wrong:** `ItemModel.OnGetAsync` and `AuditModel.OnGetAsync` map `PayloadResult.NotFound` to a 404 page. `QueueSql.Payload` inner-joins `proposal`; an identity item has `proposal_id null`, so the join yields no row, so every identity item would render "Item not found".
**How to avoid:** add `PayloadResult.NoPayload` (identity items, role holder) and make `QueueService.GetPayloadAsync` return it when `item.ProposalId is null`; keep `RoleNotPermitted` first so the role gate still precedes it. `GET .../payload` maps `NoPayload` to `404` with a new static reason `no_payload` (Discretion; keeps it distinguishable from `item_not_found` only for role holders, who are already allowed to know the item exists). Pages treat it as refs-only. `Audit` must also handle it (Record is null for identity items already because `detail.ProposalId` is null).

### Pitfall 4: `JsonGate.TryAdmit` is proposal-specific
It requires a string `run_id` and rejects otherwise. For create, parse with `JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = JsonGate.MaxDepth, AllowDuplicateProperties = false })`, require an object, call `JsonGate.Walk(root)`, then run the field logic. Duplicate keys, lone surrogates and lossy numbers all fail as `400 invalid_request`.

### Pitfall 5: Field-check order decides which error bluto sees (TC-IRQ-03)
IR-1: a body that supplies `tenant_id`, `producer`, `item_id`, `state` or any decision field is `400 server_assigned_field`; other unknown fields are `400 invalid_request`. A body with both kinds gets `server_assigned_field` (check it first). Define the server-assigned set once: `tenant_id, producer, item_id, state, decision, decided_by, decided_at, rationale, claimed_by, claimed_at, auth_source, withdrawn_reason, expires_at, created_at, proposal_id` (the last eight are an interpretation of "any decision field"; `[ASSUMED]`, see A3). Nothing in the response names the offending field or value; `ProblemResults.Result(400, ProblemReasons.ServerAssignedField)` only.

### Pitfall 6: Replay compares the hash, not the row
`409 idempotency_conflict` requires comparing `body_sha256`, so the first insert must store it. Replay of an item that has since been claimed/decided/withdrawn still returns `200` with the current `{item_id, state}`; a candidate that was withdrawn can never be recreated under the same `producer_ref` (unique key), so bluto must mint a new candidate id. Say so in the demo/README to avoid a bluto-side surprise.

### Pitfall 7: Cursor race between `decided_at` assignment and commit
`decided_at` is the app clock value taken before the transaction locks the row (`time.GetUtcNow()` in `QueueService.DecideAsync`), not the commit time. Decision A stamped t1 can commit after bluto polled and advanced its cursor past decision B stamped t2 > t1, so A is never returned. The window is a few milliseconds per decide, but it is real. Mitigations, cheapest first: (a) tell the bluto owner (with the `next_cursor` note) to keep a small overlap when storing its cursor; application of a decision is already idempotent on `item_id` (IR-1 §6); (b) take `decided_at` from `@now` read inside the transaction after `LockItem`; (c) a poll "settle" lag. Recommend (a)+(b); (c) changes observable timing and needs the bluto owner. Record the choice in the plan.

### Pitfall 8: Existing tests and fakes that the new members break
- `QueueServiceDecideTests.QUE_07_identity_review_item_is_kind_not_supported...` asserts `KindNotSupported` for identity items; rewrite it (identity is now supported; keep an unknown-decision case).
- Any test double implementing `IQueueStore` (`QueueServiceDecideTests` fake store and others) must gain the new members; adding the interface members is a compile break in the test project. Add new methods to a separate interface (`IIdentityQueueStore`) only if the planner prefers to avoid churn; otherwise extend `IQueueStore` and update fakes in the same task.
- `PrivilegeMatrixTests.QueueInsertable` must gain `body_sha256` (the test asserts the exact set of insertable columns).
- `MigrationRunnerTests` expected `applied` list and per-version checksum assertions: add `0010`.
- `ReviewerSessionTests.UI_01_picker_source_lists_all_fixture_reviewers...` asserts `Assert.Equal(8, all.Count)`; adding 3 fixtures makes 11.
- `CredentialRouteMatrixTests.UI_07_cell_table_covers_seven_credentials_by_five_routes` asserts 35 cells; update with the new enum members.
- Adding fields to positional records `QueueItemDetail`/`QueueItemSummary`/`QueueItemView`/`QueueItemDto` touches many call sites. Add `RulesetVersion` as a trailing optional parameter (`string? RulesetVersion = null`) to avoid churn.

### Pitfall 9: A non-steward can claim an identity item and strand it
Claim deliberately needs no role (Phase 5: "Role is deliberately not required to claim or release"), D-03 keeps non-stewards able to see the items, and identity items never expire. A case supervisor who claims an identity item cannot decide it, and only they (release) or bluto (withdraw) can free it. Not a Phase 8 decision to reverse (Q7 is locked); record it as an accepted risk and make sure the Item page for a non-steward shows the existing "role" explanation before the Claim button is offered (a static sentence, no new logic) (Open Question 2).

### Pitfall 10: Rationale in the `deferred` event detail is reviewer free text
D-09 puts the defer rationale in the event, and `GET /v1/queue/items/{id}/events` returns `detail` verbatim to any same-tenant reviewer (no role check on `EventsAsync`). Existing rule: the rationale never reaches an event detail for decide. Recommendation: store `detail = {"decision":"defer","auth_source":"...","rationale":"..."}` (D-09 satisfied, rationale preserved in the system of record), and make `QueueService.EventsAsync` strip the `rationale` key unless the reviewer holds the item's `required_role`. Add a sentinel test that the rationale text appears in no log line, no poll response and no non-steward events response.

### Pitfall 11: DB check name for the event-type CHECK
`0008` declared the `event_type` CHECK inline, so Postgres auto-names it `queue_item_event_event_type_check` (`<table>_<column>_check`) `[ASSUMED: PostgreSQL naming convention]`. The migration must drop and re-add that constraint; verify the name in the migration test with `select conname from pg_constraint where conrelid = 'queue_item_event'::regclass`, and make the Wave 0 migration test fail loudly if it is wrong.

## Migration 0010 (exact shape)

File: `sql/migrations/0010_identity_review_producer.sql` (`^[0-9]{4}_[a-z0-9_]+\.sql$`, contiguous, embedded by the existing Persistence glob; run by the Migrator as `olive_owner`; 0001-0009 are checksum-pinned and must not change).

```sql
-- 0010: identity_review producer path (IRQ-01, IRQ-02, IRQ-03). 0001-0009 are checksum-pinned and unchanged.
-- body_sha256: lowercase hex SHA-256 of the RFC 8785 canonical create body (idempotency key payload, IR-1 section 4).
alter table queue_item add column body_sha256 text;
alter table queue_item add constraint queue_item_body_sha256_format
    check (body_sha256 is null or body_sha256 ~ '^[0-9a-f]{64}$');
-- Every identity_review row is created through the producer path and therefore carries a hash; action_approval rows stay null.
alter table queue_item add constraint queue_item_body_sha256_by_kind
    check (kind <> 'identity_review' or body_sha256 is not null);

-- IR-1 section 3: a defer returns the item to open and is kept in history as its own event type.
alter table queue_item_event drop constraint queue_item_event_event_type_check;
alter table queue_item_event add constraint queue_item_event_event_type_check
    check (event_type in ('created', 'claimed', 'released', 'decided', 'withdrawn', 'deferred'));

-- Poll: (tenant, producer) decided items ordered by (decided_at, item_id).
create index queue_item_decided_poll_idx on queue_item (tenant_id, producer, decided_at, item_id) where state = 'decided';

-- Additive column grant only. The state, claim and decision columns keep their 0008 UPDATE grant; withdrawn_reason, claimed_by
-- and claimed_at are already updatable, so a withdraw-with-claim-clear needs no new UPDATE grant. olive_app and olive_reader
-- hold table-level SELECT from 0008 and so read the new column without a grant.
grant insert (body_sha256) on queue_item to olive_app;
```
RLS needs nothing: no new table; the `tenant_isolation` policy (ENABLE + FORCE) from 0008 covers the new column. `queue_item_state_shape` is deliberately unchanged: the `withdrawn` arm only requires `decision is null and withdrawn_reason is not null`, so a claim-clearing withdraw satisfies it, and the defer path reuses the `open` arm (all claim/decision columns null). Do NOT tighten `withdrawn` to `claimed_by is null`: Phase 5 expiry withdrawals can legitimately retain `claimed_by`.

Tests to add (follow `QueueTableTests`/`PrivilegeMatrixTests`): hash format and by-kind CHECK (`23514`), `body_sha256` is in the olive_app INSERT set and in no UPDATE set, `deferred` event insert succeeds and an unknown event type is `23514`, poll index exists.

## SQL for the new transitions (all with `tenant_id = @tenant_id`)

```sql
-- Create (inside uow.ExecuteAsync; actor/producer from the registry, tenant from the token)
insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, ruleset_version, body_sha256)
values (@tenant_id, @item_id, 'identity_review', 'candidate_pair', @producer, @producer_ref, @subject_ref, @payload_ref, @ruleset_version, @body_sha256)
on conflict (tenant_id, producer, producer_ref) do nothing returning item_id;
-- on no row:
select item_id, state, body_sha256 from queue_item where tenant_id = @tenant_id and producer = @producer and producer_ref = @producer_ref;

-- Defer (claimant only; same guard as Release, plus an event)
update queue_item set state = 'open', claimed_by = null, claimed_at = null
where tenant_id = @tenant_id and item_id = @item_id and state = 'claimed' and claimed_by = @actor;
-- event: ('deferred', from 'claimed', to 'open', actor, detail jsonb)

-- Accept / reject: existing QueueSql.Decide (state='claimed' and claimed_by=@actor) is reusable as is; event 'decided', no proposal update.

-- Producer withdraw (after LockItem confirms producer + kind + tenant)
update queue_item set state = 'withdrawn', withdrawn_reason = @reason, claimed_by = null, claimed_at = null
where tenant_id = @tenant_id and item_id = @item_id and producer = @producer and kind = 'identity_review' and state in ('open','claimed');

-- Poll (page = 100 fixed; fetch 100 rows)
select tenant_id, item_id, kind, subtype, producer_ref, subject_ref, payload_ref, ruleset_version, state, decision, decided_by, decided_at
from queue_item
where tenant_id = @tenant_id and producer = @producer and kind = 'identity_review' and state = 'decided'
  and decision in ('accept','reject')
  and (@after_at::timestamptz is null or (decided_at, item_id) > (@after_at::timestamptz, @after_id::uuid))
order by decided_at, item_id limit @limit;
```
Add `ruleset_version` and `body_sha256` to `ItemColumns`/`ItemRow` (the current `ItemColumns` lacks `ruleset_version`; poll, the item page and the reviewer DTO all need it). `DateTime` parameters for `timestamptz` must have `Kind=Utc` (existing code passes `now.UtcDateTime`).

Idempotency/lock notes: `ON CONFLICT DO NOTHING` under forced RLS works because `olive_app` has SELECT; the unique key's column order `(tenant_id, producer, producer_ref)` matches the lookup. `Decide` must keep the `claimed_by = @actor` guard (Q8).

### Service/store method map

| Operation | Service (`QueueService`) | Store | Result type (new members in bold) |
|---|---|---|---|
| Create | `CreateAsync(ProducerContext, CreateItemRequest)` -> validate refs against registry kinds/ref schemes + producer's allowed subtypes, compute hash | `CreateIdentityItemAsync` | `CreateResult.Created/Replay/Conflict` |
| Withdraw | `WithdrawAsync(ProducerContext, itemId, reason)` | `WithdrawByProducerAsync` | `Withdrawn / AlreadyWithdrawn / Closed / NotFound` |
| Poll | `PollDecisionsAsync(ProducerContext, cursor)` | `ListDecidedForProducerAsync` | `PollResult.Ok(items, nextCursor) / Invalid` |
| Decide identity | existing `DecideAsync` branches: `item.Kind == identity_review` -> registry decisions + rationale checks + role check, **skip** identity (self-approval) check | `DecideIdentityAsync` | existing `DecideResult` + **`Deferred(item)`, `Withdrawn`** |
| Release on withdrawn | existing | existing | add **`ReleaseResult.Withdrawn`** (non-expiry withdrawn); keep action_approval's `Closed`/`ProposalExpired` behaviour |

`IsWithdrawnExpired` (reason `proposal_expired`) must stay distinct from a producer withdrawal. In `DecideIdentityAsync`/`Release`: `state == withdrawn` -> `Withdrawn` -> `409 item_withdrawn`. For action_approval keep returning `Closed`.

### Cursor codec
`ProducerCursor.Encode(DateTime decidedAtUtc, Guid itemId)` = `Base64Url.EncodeToString(Utf8("v1." + unixMicros + "." + itemId.ToString("N")))`; `TryDecode` rejects: length > 128, non-canonical base64url (decode then re-encode and compare), wrong prefix, non-digit micros, micros out of `[0, year 9999]`, bad GUID. Microseconds, not ticks: Postgres `timestamptz` is microsecond precision and Npgsql returns a `DateTime` with 10-tick granularity, so a micros round-trip is lossless. HMAC is unnecessary (public ordering keys only); a tampered cursor can only skip or repeat the caller's own tenant's decisions, and it never crosses tenant or producer because both predicates come from the token. `next_cursor` = cursor of the last returned row; if the page is empty return the supplied cursor (or `null`).

### Wire shapes to add (all additive, flag to bluto owner)
- Create: `201 {"item_id","state"}` new; `200 {"item_id","state"}` replay; `409 idempotency_conflict`. Slim response on purpose: the full `QueueItemDto` includes `claimed_by` (a reviewer oid), which bluto must not learn.
- Withdraw: request `{"reason":"superseded_by_rule|source_changed|ruleset_retired"}` (IR-1 does not give the body shape; strict, one field), response `200 {"item_id","state":"withdrawn"}`. `[ASSUMED]` body shape, flag for the bluto owner with `next_cursor`.
- Poll: `200 {"items":[...D-14 fields...],"next_cursor":"..."|null}`.

## Stub verifier, fixtures, producers.yaml

**Port** (`Olive.Application`, `[OutboundPort(OutboundDestination.Popeye)]`):
```csharp
public interface IServiceTokenVerifier { Task<ServiceTokenResult> VerifyAsync(InboundBearerToken token, CancellationToken ct); }
public sealed record ServiceCaller(string PrincipalType, string ClientId, Guid TenantId, string? DelegatedAgentId, DateTimeOffset ExpiresAt);
public sealed record ServiceTokenResult(bool Active, ServiceCaller? Caller) { public static ServiceTokenResult Inactive { get; } = new(false, null); }
```
**Stub** `StubServiceTokenVerifier : IServiceTokenVerifier, INonProductionOnly` in `Olive.Integrations.Popeye`, copy the shape of `StubKeyIntrospector` (index.json maps visibly fake token strings to fixture files; malformed fixture -> `IntrospectionUnavailableException`; inactive -> `{"active": false}`; expired -> Inactive). Fixtures in `contracts/service-token/` (add `<EmbeddedResource Include="..\..\contracts\service-token\*.json" LogicalName="service-token/%(Filename)%(Extension)" />` to the Popeye csproj; embed only `*.json`, so the README is not embedded):
- `fixture-service-token-bluto-alpha` -> `{active, principal_type: application, client_id: "bluto-stub", tenant_id: <alpha>, exp}`
- `...-bluto-beta` (beta tenant, same client_id), `...-unknown-client` (client_id not in producers.yaml), `...-unknown-tenant` (tenant UUID not in the map), `...-delegated-agent` (carries `delegated_agent_id`), `...-inactive`, `...-expired`
- Same visible keys as the introspection index (`fixture-key-liam-alpha`, `fixture-key-user`) mapped to service-token-shaped agent/user responses (Pitfall 1).
- `introspection-response.schema.json` analogue: `service-token-response.schema.json` + `.valid.json`/`.invalid.json` pair (`ContractSchemaTests` enumerates a fixed folder list; add `contracts/service-token` to its `Folders` array so the pairing rule applies).

**Claims** for the service principal: reuse the `olive:` prefix (`olive:principal_type`, `olive:client_id`, `olive:tenant_id`, `olive:delegated_agent_id` when present, `olive:expires_at`) in a `ServiceClaims` helper, so `ReviewerClaims.TenantId` (= `OliveClaims.TenantId`) does not accidentally satisfy a reviewer check: the reviewer filter must test the authentication type, not merely the presence of a tenant claim. Add a test that a service principal passed to `ToReviewerContext()` is never accepted by the reviewer filter.

**`config/producers.yaml`** (embedded automatically by the existing `config/*.yaml` glob in `Olive.Catalog.csproj`):
```yaml
# Producers allowed to file queue items (D-06). Source-controlled; not overridable by environment variable or appsettings.
producers:
  - client_id: bluto-stub
    producer: bluto
    kinds:
      - kind: identity_review
        subtypes: [candidate_pair]
```
`IProducerRegistry` (Application) with `TryGetByClientId(string, out ProducerDefinition)`, `ProducerDefinition(ClientId, Producer, IReadOnlyDictionary<string, IReadOnlySet<string>> KindSubtypes)`. `Olive.Catalog.ProducerRegistry.Parse(yaml)` strict-parses (`ClientId` pattern `^[a-z][a-z0-9_-]{0,63}$`, unique client ids, non-empty kinds), invalid -> `InvalidOperationException("Producer registry is invalid.")` (static text, like the others).
**Startup cross-validation** (new `StartupGuards.CheckProducers(provider)`, wired into `StartupGuardFilter` after `CheckQueueKinds`; failure rule `QUE-02` or a new `IRQ-04`, message static): for each producer entry, every `kind` must exist in `IQueueKindRegistry`, every subtype must be in that kind's `Subtypes`, and `producer` must be in that kind's `RefSchemes` (bluto is in `[bluto]`); also enforce that `producer` is `bluto` for `identity_review` because migration 0008 constrains `producer in ('olive','bluto')` and the identity ref CHECK requires `producer = 'bluto'`. Exceptions are caught and rethrown as a `StartupGuardException` with a static message (same pattern as `CheckQueueKinds`). Not-overridable: the registry reads only the embedded resource; add a test that setting `OLIVE_PRODUCERS`/appsettings changes nothing (mirror the agents.yaml test, `AgentAllowlistTests`).

**FND-05:** `StubServiceTokenVerifier` implements `INonProductionOnly`; `CheckNoNonProductionComponents` already refuses it in production by scanning registrations. Register it like the other stubs: `builder.Services.AddSingleton<IServiceTokenVerifier>(_ => StubServiceTokenVerifier.CreateDefault(sp.GetRequiredService<TimeProvider>()))`. The registration plus the `DevReviewer` scheme mean production still refuses to start (that is already the Phase 1 behaviour, WR-02 in the 07 review); do not try to fix that here.

**Tenant known check (D-08):** use `IReviewerTenantMap.AllTenants` (distinct tenants of `config/reviewer-tenants.yaml`: alpha, beta). The vocabulary has a third `TENANT_GAMMA` (`8d61d9c2-...`) that the reviewer map never lists, so a gamma token would be 403 `tenant_not_permitted` under this choice. This is conservative (fail closed) and needs no new loader, but it ties producers to the reviewer map. `[ASSUMED]` that this is acceptable (Open Question 3). The alternative is a small `VocabularyTenants.Load` beside `VocabularyPurposes.Load` reading `poc_tenants` from the vendored vocabulary.

**Reason codes to add** (`ProblemReasons` + `ProblemResults.Title` + a test in `ProblemReasonTitleTests` style): `server_assigned_field` (400, mandated by IR-1), `tenant_not_permitted` (403), `producer_not_registered` (403, unknown `client_id`), `item_withdrawn` (409), `no_payload` (404). Delegated-agent tokens reuse `principal_not_permitted` (403): the token is a service token carrying an agent, which is by definition not a plain application caller (D-05 says "refused"; a distinct code adds no information a caller can use).

## Reviewer-side behaviour changes

### `QueueService.DecideAsync` (identity branch)
Replace `if (item.Kind != QueueStates.ActionApproval || ...) KindNotSupported` with: unknown kind or kind missing from registry -> `KindNotSupported` (keep); decision must be in `definition.Decisions` -> else `InvalidRequest`; rationale rule unchanged (non-blank, `<= 4000`, no NUL; required for defer too); role check from `definition.RequiredRole` before any lookup (D-09/TC-IRQ-04). For `identity_review` skip `identityCheck` entirely and call `store.DecideIdentityAsync`. For `action_approval` leave the code path unchanged. `ApprovalExecution.RunAsync` already ignores items where `ProposalId is null` or decision is not `approve`, so no change is needed there [VERIFIED: src/Olive.Host/Endpoints/ApprovalExecution.cs]. Map `DecisionResult.Deferred` to `200` + `ToDto(item)` in `DecisionAsync` (state `open`).

TC-IRQ-04 role matrix with the existing fixtures: `alpha-sup-1` (supervisor, no steward) is refused on identity items and the new `alpha-steward-1` (steward, no supervisor) is refused on action items; no separate "supervisor-only" fixture is needed.

### Razor pages (against 07-UI-SPEC)
- **Index:** put the kind in a badge (`<span class="badge">@item.Kind</span>`); reuse `.badge`, no new CSS file (UiSurfaceTests allows exactly `olive.css`; a one-line class addition there is fine, but the `Pages` and `wwwroot` banned-word and no-inline-style checks apply). Page copy "Open items for tenant" stays valid; the "No open items" empty state stays.
- **Item (`Item.cshtml(.cs)`):**
  - `DecisionsSupported` today equals `Decisions == [approve, reject]`. Replace by `Decisions = kind.Decisions` and render radios from it (`@foreach`), with `required` and no default; keep draft restore (`checked` when `DraftDecision == value`).
  - Rationale hint per kind; for identity: "Do not include names, dates, identifiers or other record values." Finality note per kind: action_approval unchanged; identity: "Accept and reject are final. Defer returns the item to the queue for any steward."
  - The stale-session branch of `OnPostDecideAsync` validates `decision is not ("approve" or "reject")`; make it registry-driven (needs the item's kind: call `queue.GetAsync` once at the top of POST; do not put the kind in the form).
  - `code` switch gains `Deferred -> "deferred"`, and `Decided` -> `decided_<decision>` for accept; `Withdrawn -> "item_withdrawn"` for decide and release. Add to `FlashMessages.Map`: `decided_accept`, `deferred` ("Done: deferred. The item is back in the queue and your claim is cleared."), `item_withdrawn` ("Error: the producer withdrew this item. See the audit history."). Update the `invalid_request` copy, which currently says "choose Approve or Reject".
  - Item `<dl>`: add Producer ref and Ruleset version rows for identity items; payload section for identity shows the static evidence note (no link, no `http`), driven by `PayloadResult.NoPayload`. `FixtureTrace` check already returns false for non-`olive:proposal/` refs.
  - The withdrawn copy "This item was withdrawn by its producer." already exists.
- **Audit (`Audit.cshtml(.cs)`):** handle `PayloadResult.NoPayload` (not a 404). The history table already lists any `EventType`; `deferred` appears with From `claimed` To `open`. Close the open review finding WR-05 for identity items by rendering a "Decision" `<dl>` from the `Item` view for any `decided` item (Decision, Decided by, Decided at, Auth source) instead of only the `execution_chain` block, and show the defer rationale only to role holders. Take the opportunity to remove the direct `IQueueStore` injection (WR-04): expose `ProposalId` through `QueueService` (a new `GetDetailAsync`) or add it to `QueueItemView` as a trailing optional member. Do not widen scope to WR-02/03/06/07 and IN-xx; they do not touch the identity paths.
- **Open 07-REVIEW items touching the same files:** WR-04 (Audit 404 and `IQueueStore` bypass), WR-05 (decision details missing for rejected items; the identity audit needs this anyway), IN-06 (two divergent `ParseParameters`; irrelevant to identity, skip), IN-07 (hard-coded `"entra"/"stub"` in `_Layout`; skip). WR-02/WR-03/WR-06/WR-07 are in `Program.cs`, `OidcConfig`, `DecisionFreshness` and the `Stale` challenge: the Phase 8 edit to `Program.cs` should not make them worse, and WR-07 (Entra re-challenge 500 when the scheme is absent) is reachable by the new stale-session validation path only if the planner copies that branch; keep it unchanged.
- **UiSurfaceTests** (`ExpectedPages` = nine pages) stays valid: no new Razor page, no new non-`/v1` route.

### Fixtures (D-12)
Add `alpha-steward-1`, `alpha-steward-2`, `beta-steward` to `contracts/reviewer/` and `index.json` with groups `["tenant-alpha-reviewers","role:identity_steward"]` / beta equivalent, unique `oid`s following the `aaaaaaaa-0000-4000-8000-0000000000NN` pattern, and update `contracts/reviewer/README.md`. Update the `Assert.Equal(8, all.Count)` assertion (Pitfall 8). The `EntraClaimsMapper` allowlist needs no change (registry-derived); add a mapper test that a `roles: ["identity_steward"]` claim yields `role:identity_steward`.

## Code Examples

### Create handler core (order matters)
```csharp
// Source: composed from ProposalEndpoints.PostAsync order and IR-1 section 4
var bytes = await ProposalEndpoints.ReadBoundedAsync(http.Request, ct);        // 413 if too large
if (bytes is null) return ProblemResults.Result(413, ProblemReasons.PayloadTooLarge);
using var doc = TryParseStrict(bytes);                                          // MaxDepth 64, AllowDuplicateProperties=false, object root, JsonGate.Walk
if (doc is null) return invalid;
var root = doc.RootElement;
if (root.EnumerateObject().Any(p => ServerAssigned.Contains(p.Name))) return ProblemResults.Result(400, ProblemReasons.ServerAssignedField); // before unknown-field check
if (!AllowedCreateFields.SetEquals(root.EnumerateObject().Select(p => p.Name))) return invalid;   // exactly the six fields, all required, all strings
// producer/tenant come only from the principal; kind/subtype checked against producers.yaml ("link_challenge" -> invalid_request)
var canonical = Jcs.Canonicalize(root);
var sha = Jcs.Sha256Hex(canonical);
```
Validation of values: `kind`/`subtype` in the producer's `KindSubtypes` (so `link_challenge` is rejected both because it is not in the registry and not in `producers.yaml`); `producer_ref` canonical lowercase GUID (`ToString("D")` equals input); `subject_ref == "bluto:candidate/" + producer_ref`; `payload_ref == "bluto:review-evidence/" + producer_ref` (the DB only checks UUID shape; IR-1 §2 implies equality with `candidate_id`); `ruleset_version` matches `^[A-Za-z0-9._-]{1,128}$` (`[ASSUMED]`, IR-1 says "git SHA" and only the schema bound of 128 is stated; the tight charset keeps record values out, TC-IRQ-01). Any failure is `invalid_request` with no detail.

### Matrix extension shape (D-04)
Add `Cred.ServiceToken` (+ `Cred.ServiceTokenOtherTenant` is not needed) and `Cred.ServiceDelegated`/`Cred.ServiceUnknownClient` as optional rows; add routes `QueueCreate`, `QueueWithdraw`, `QueuePoll` (send `POST`/`POST`/`GET ?kind=identity_review&state=decided`). `Build` currently always `HttpMethod.Get`; give `Route` a method and body. Expected admission table (independent of the handlers, as the existing test insists):

| Route | ServiceToken | AgentKey | DevHeader | cookies | None |
|---|---|---|---|---|---|
| Proposals | 403 principal_not_permitted (must come from `/v1/proposals` principal check; the agent scheme will introspect the bearer: stub returns `active:false` -> 401 `key_inactive`, see note) | 404 | 403 | 401 key_inactive | 401 key_inactive |
| Catalog | same as Proposals | 200 | 403 | 401 | 401 |
| QueueItems (reviewer list) | 403 principal_not_permitted | 403 | 200 | 401 reviewer_unauthenticated | 401 |
| QueueCreate / QueueWithdraw | 201/404 (admitted) | 403 | 403 | 401 | 401 |
| QueuePoll | 200 | 403 | 403 | 401 | 401 |
| Review / DevSignIn | redirect to sign-in / 404 | same as today | same | same | same |

Note on `/v1/proposals` + service token: `AgentKeyAuthenticationHandler` introspects the bearer string with `IKeyIntrospector`; the introspection fixtures do not contain `fixture-service-token-*`, so the result is `Inactive` -> `401 key_inactive`, not 403. IDN-03 and D-07 say the proposal endpoint "refuses service credentials" and "a service token on `/v1/proposals` gets 403". Reconcile: add the service-token fixture string to `contracts/introspection/index.json` as an `application` principal (as `fixture-key-application` already is), so the existing `key.PrincipalType != Agent -> 403 principal_not_permitted` check produces the 403 on `/v1/proposals` and `/v1/catalog`. That is the minimal change and the existing check is the code under test. Plan a test for exactly this (IDN-03 evidence). The matrix's "introspector call count unchanged" assertion must be kept for the queue and review routes and, for the ServiceToken row on agent routes, relaxed the same way `AgentKey` is (the agent routes do introspect).

### Demo test (D-18)
Follow `Beat4DemoTests`: `[Trait("Category","Integration")] [Trait("Demo","identity")]`, `OliveApiFactory` over HTTP, transcript at `TestResults/identity-review-transcript.md`, sentinel scan asserting no rationale/record-value/token text. Extend `OliveApiFactory` with `CreateServiceClient(token)` (adds the `Authorization: Bearer` header). `build/demo-identity-review.sh` = copy of `demo-beat4.sh` with `Demo=identity`; a `..._runner_script_filters_to_the_identity_trait` test as in Beat 4.

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| `Authorization` challenge heuristics inside one handler | Policy scheme with `ForwardDefaultSelector` | One deterministic challenge/forbid per route group |
| Manual base64url | `System.Buffers.Text.Base64Url` (.NET 9+) | Strict decode, no allocations of `Replace` chains |

**Deprecated/outdated:** CLAUDE.md's single-file `Domain.cs` description; `contracts/queue-item/v0` (gone).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Real popeye will answer a verification call for an agent/user key with an active non-application principal (so Olive's 403 logic is exercised); the stub models this | Pitfall 1 | If popeye returns `active:false` for non-application tokens, agent keys on producer routes become 401 not 403; D-07 expects 403, so the stub fixtures would need to change when the real adapter lands |
| A2 | Postgres auto-names the inline event_type CHECK `queue_item_event_event_type_check` | Pitfall 11, Migration | Migration fails to apply; caught immediately by the migration test |
| A3 | "Any decision field" in IR-1 §4 means the full set listed in Pitfall 5 | Pitfall 5 | A field omitted from the server-assigned set would return `invalid_request` instead of `server_assigned_field`; confirm with the IR-1 owners or keep the set exactly as the contract names it plus the decision columns |
| A4 | Withdraw request body is `{"reason": "<code>"}` and the create/poll/withdraw response envelopes above | Wire shapes | bluto's client is stub-only for now; mismatch is an additive-change conversation with the bluto owner (IR-1 change control) |
| A5 | `ruleset_version` charset `^[A-Za-z0-9._-]{1,128}$` | Create handler | A legitimate bluto version string with other characters is rejected |
| A6 | `IReviewerTenantMap.AllTenants` is an acceptable "known tenant" set for D-08 | Stub verifier | Gamma tenant tokens are refused; a vocabulary-based loader would admit it |
| A7 | Cookie auth is not admitted on `/v1/queue/items` (matrix: cookies -> 401 `reviewer_unauthenticated`), so D-01's "or the reviewer cookie where Phase 7 admits it" has no case to cover | Dispatch | If Phase 7 meant to admit cookies on the API, the selector needs a third arm; the current code and matrix say it does not |

## Open Questions

1. **Cursor overlap and `decided_at` source (Pitfall 7).**
   - What we know: `decided_at` is the pre-transaction app clock; the poll orders by it.
   - What's unclear: whether the bluto owner will accept "keep an overlap" in its cursor handling.
   - Recommendation: take `now` inside the transaction after the row lock and document the overlap in the same message that names `next_cursor`.
2. **Non-steward claims on identity items (Pitfall 9).**
   - What we know: claim needs no role; items never expire; only the claimant or bluto can free a claimed item.
   - What's unclear: whether the user wants claim gated by `required_role` for identity items (a change to the Phase 5 claim contract).
   - Recommendation: leave claim unchanged for Phase 8, note the accepted risk in the plan, show the role notice on the page.
3. **Which tenant set is "known" (A6).** Reviewer-map tenants (alpha, beta) versus the vendored vocabulary's three. Recommend reviewer-map for now; revisit when popeye's bluto registry entry exists.
4. **Repeat withdraw.** IR-1 is silent. Recommend `200` idempotent when already withdrawn with the same reason, `409 item_closed` with a different reason, so a retried bluto job is safe.
5. **Where the service-token fixture string for `/v1/proposals` lives** (add to introspection index, see Matrix note) versus teaching `AgentKeyAuthenticationHandler` to reject `Bearer fixture-service-token-*`. Recommend the index entry.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/tests | yes | 10.0.400 | — |
| Docker | Testcontainers Postgres integration tests and the demo | yes | 29.7.2 (daemon reachable) | none; integration tests need it |
| PostgreSQL | via Testcontainers only | yes (container) | image pinned by `PostgresFixture` | — |
| slopcheck / npm / pip | package audit | not needed | — | No new packages |

**Missing dependencies with no fallback:** none.

## Validation Architecture

(`workflow.nyquist_validation` is true in `.planning/config.json`.)

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.0 with Testcontainers.PostgreSql, `Microsoft.AspNetCore.Mvc.Testing` |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (traits: `Category=Unit|Integration|Architecture`, `Demo=...`) |
| Quick run command | `dotnet test tests/Olive.Tests --filter "Category=Unit|Category=Architecture"` (no Docker; use for pure units: cursor codec, producer registry, verifier stub, schemas, architecture) |
| Full suite command | `dotnet test tests/Olive.Tests` (needs Docker) |
| Phase-focused | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~IdentityReview|FullyQualifiedName~ServiceToken|FullyQualifiedName~ProducerRegistry|FullyQualifiedName~CredentialRouteMatrix|FullyQualifiedName~PrivilegeMatrix|FullyQualifiedName~MigrationRunner"` |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| IRQ-01 / TC-IRQ-01 | inline payload or record value in any field -> 400 `invalid_request`, no row | integration | `... --filter "FullyQualifiedName~IdentityReviewCreateTests&FullyQualifiedName~IRQ_01"` | no, Wave 0 |
| IRQ-01 / TC-IRQ-02 | identical body twice = one row, 200 replay; different body = 409 `idempotency_conflict`; hash stored | integration + unit (canonical order independence) | `... --filter "FullyQualifiedName~IRQ_02"` | no, Wave 0 |
| IRQ-01 / TC-IRQ-03 | `tenant_id` (other or same), `producer`, `item_id`, `state`, decision fields -> 400 `server_assigned_field`, no row, no value echoed; same body without them creates a tenant-A item | integration | `... --filter "FullyQualifiedName~IRQ_03"` | no, Wave 0 |
| IRQ-01 / TC-IRQ-11 | `subtype: link_challenge` -> 400 `invalid_request` | integration | `... --filter "FullyQualifiedName~IRQ_11"` | no, Wave 0 |
| IRQ-02 / TC-IRQ-04 | non-steward decide refused (`role_not_permitted`); steward refused on action item | integration | `... --filter "FullyQualifiedName~IRQ_04"` | no, Wave 0 |
| IRQ-02 / TC-IRQ-05 | decide without rationale (incl. defer) -> 400 | unit (service) + integration | `... --filter "FullyQualifiedName~IRQ_05"` | no, Wave 0 |
| IRQ-02 / TC-IRQ-06 | defer then accept: state open after defer, claim cleared, events `created, claimed, deferred, claimed, decided`, `decision` columns null after defer, poll returns only the accept | integration | `... --filter "FullyQualifiedName~IRQ_06"` | no, Wave 0 |
| IRQ-02 | identity decide never calls identity check or executor; no proposal touched | unit (service with recording fakes) | `... --filter "FullyQualifiedName~IRQ_02_identity_decide"` | no, Wave 0 |
| IRQ-03 / TC-IRQ-09 | withdraw open and claimed item (claim cleared, event, claimant's later decide/release -> 409 `item_withdrawn`); withdraw decided -> 409 `item_closed`; other producer/tenant/missing -> same 404; unknown reason -> 400 | integration | `... --filter "FullyQualifiedName~IRQ_09"` | no, Wave 0 |
| IRQ-03 / TC-IRQ-10 | poll with tenant A token returns only tenant A decided items for its producer; RLS ablation: with the app predicate removed the DB still hides tenant B | integration (+ `TenantIsolationAblationTests` pattern) | `... --filter "FullyQualifiedName~IRQ_10"` | no, Wave 0 |
| IRQ-03 | poll: cursor ordering with equal `decided_at`, `next_cursor` page-of-100 boundary, malformed cursor 400, extra query parameter 400, no rationale in response | integration + unit (`ProducerCursor`) | `... --filter "FullyQualifiedName~IdentityReviewPoll"` | no, Wave 0 |
| IRQ-04 | stub verifier: active/inactive/expired/malformed(503)/delegated agent; token never in logs (Trace), headers, responses, stored rows | unit + integration (`SentinelScan`) | `... --filter "FullyQualifiedName~ServiceToken"` | no, Wave 0 |
| IRQ-04 | `producers.yaml` strict parse; startup refuses unregistered kind/subtype/ref scheme; env/appsettings cannot override; unknown client_id -> 403; unknown tenant -> 403 `tenant_not_permitted` | unit + integration | `... --filter "FullyQualifiedName~ProducerRegistry"` | no, Wave 0 |
| IRQ-04 / FND-05 | production refuses `IServiceTokenVerifier` stub | unit (extend `StartupGuardTests`) | `... --filter "FullyQualifiedName~StartupGuard"` | exists, extend |
| IDN-03 | service token refused on `/v1/proposals`, `/v1/catalog`, `/review`, `/dev`, every reviewer action; agent/user/reviewer refused on create/withdraw/poll; matrix cell count updated | integration (matrix) | `... --filter "FullyQualifiedName~CredentialRouteMatrix"` | exists, extend |
| IDN-03 / IDN-08 | new inbound token type only on popeye ports; port marked `[OutboundPort]` | architecture | `... --filter "Category=Architecture"` | exists, extend |
| Migration | 0010 applies, checksum pinned, CHECKs, grants, event type | integration | `... --filter "FullyQualifiedName~MigrationRunner|FullyQualifiedName~PrivilegeMatrix|FullyQualifiedName~QueueTable"` | exists, extend |
| Pages (IRQ-02) | kind badge on list; identity item page renders (not 404) with refs only and no `http`; radios = accept/reject/defer; rationale hint text; defer flash; audit shows deferred row and decision block; Stale branch kind-driven | integration (HTML) | `... --filter "FullyQualifiedName~ReviewerPages|FullyQualifiedName~IdentityReviewPages|FullyQualifiedName~UiSurface|FullyQualifiedName~SentinelUi"` | exists, extend + new |
| Contracts | new create/withdraw/poll/service-token schemas with valid+invalid pairs; IR-1 file byte-identical | unit | `... --filter "FullyQualifiedName~ContractSchema|FullyQualifiedName~QueueSchema"` | exists, extend |
| D-18 | scripted demo + runner script + transcript with no rationale/record value | integration | `bash build/demo-identity-review.sh` or `dotnet test tests/Olive.Tests --filter "Demo=identity"` | no, Wave 0 |

Add an IR-1 integrity test: SHA-256 of `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` equals a recorded constant (compute it from the file at plan time; do not change the file). bluto pins this hash; a test makes accidental edits fail.

### Sampling Rate
- **Per task commit:** `dotnet test tests/Olive.Tests --filter "Category=Unit|Category=Architecture"` plus the one focused integration class for the task.
- **Per wave merge:** the phase-focused filter above (needs Docker).
- **Phase gate:** `dotnet test tests/Olive.Tests` full suite green, plus `bash build/demo-identity-review.sh`, before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/Olive.Tests/IdentityReviewCreateTests.cs`, `IdentityReviewDecisionTests.cs`, `IdentityReviewPollWithdrawTests.cs`, `IdentityReviewPagesTests.cs` (TC-IRQ-01..06, 09..11; TC-IRQ-07/08 are bluto-side, covered only by the demo showing what Olive returns)
- [ ] `ServiceTokenVerifierTests.cs`, `ServiceTokenAuthTests.cs`, `ProducerRegistryTests.cs`, `ProducerCursorTests.cs`
- [ ] `IdentityReviewDemoTests.cs` + `build/demo-identity-review.sh`
- [ ] Test helpers: `OliveApiFactory.CreateServiceClient`, `RecordingServiceTokenVerifier` in `RecordingPorts.cs` (outbound-call recording for the new port, with `FaultingDoubles` counterpart for the 503 test), `TestDatabase` helper to seed identity items directly (needs `body_sha256`)
- [ ] Update: `PrivilegeMatrixTests.QueueInsertable`, `MigrationRunnerTests` (0010), `ReviewerSessionTests` (11 fixtures), `QueueServiceDecideTests` (identity now supported), every `IQueueStore` fake, `CredentialRouteMatrixTests` (enums, method/body per route, cell-count test), `ContractSchemaTests.Folders` (+ `contracts/service-token`), `ArchitectureTests` (inbound token lists), `StartupGuardTests` (producers check, FND-05 for the verifier)
- No framework install needed.

## Security Domain

`security_enforcement` is enabled (ASVS level 1, block on high).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | Service token verified by `IServiceTokenVerifier`; fail closed (401 inactive, 503 unavailable); no token in logs; stub barred in production by FND-05 |
| V3 Session Management | no change | Reviewer cookie sessions untouched; cookie not admitted on `/v1` |
| V4 Access Control | yes | Caller-type filters (`principal_not_permitted`); producer scoping by registry (producer never from body); decide-time role from kind registry; tenant from token / group map only; app predicate + forced RLS |
| V5 Input Validation | yes | Strict parse (`JsonGate.Walk`, duplicate keys refused), exact six-field allowlist, anchored ref patterns, ruleset charset, reason-code allowlist, cursor strict decode, query-parameter allowlist on poll |
| V6 Cryptography | yes (hash only) | SHA-256 via `Jcs.Sha256Hex`; no custom crypto; cursor needs no HMAC (public ordering keys, scoped by token) |
| V7 Logging | yes | Static problem details; token wrapped in redacting `InboundBearerToken`; rationale never logged; extend `SentinelScan` to the token and the defer rationale |
| V8 Data Protection | yes | Refs only (REQ-QUE-01); poll omits rationale; create response omits `claimed_by`; defer rationale only to role holders on the events API |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Producer spoofs `tenant_id`/`producer` in the body | Spoofing / Tampering | Fields forbidden in body (400 `server_assigned_field`), values taken from verified token + `producers.yaml` |
| Agent key or reviewer replayed on producer route (and vice versa) | Elevation of privilege | Authentication-type check in the caller filters; matrix rows prove each pairing |
| Cross-tenant / cross-producer read or withdraw | Information disclosure | Tenant + producer predicates in SQL, forced RLS, indistinguishable 404 |
| Idempotency confusion (same key different body) | Tampering | Stored `body_sha256`, 409 on mismatch, unique key on `(tenant, producer, producer_ref)` |
| Free text smuggling record values via `ruleset_version` or refs | Information disclosure | Anchored patterns and tight charset; schema `additionalProperties:false` |
| Stuck/hostile claim on a never-expiring item | Denial of service | bluto withdraw clears claims (D-17); claimant release; accepted risk for non-steward claims (Pitfall 9) |
| Token leakage in logs/headers/exceptions | Information disclosure | Redacting type, `Logging` filters unchanged, Trace-level sentinel scan |
| Verifier outage treated as allow | Spoofing | Exceptions propagate to `ProblemExceptionHandler` -> 503; no cache, no default allow |
| Cursor tampering | Tampering | Opaque, strictly decoded; confined to caller's tenant+producer by predicates |
| Withdraw/decide race | Tampering | `FOR UPDATE` row lock + guarded UPDATE + row-count check |

## Sources

### Primary (HIGH confidence)
- Repository at commit bf4475b: `QueueEndpoints.cs`, `Program.cs`, `AgentKeyAuthenticationHandler.cs`, `DevReviewerAuthenticationHandler.cs`, `QueueService.cs`, `IQueueStore.cs`, `PostgresQueueStore.cs`, `QueueSql.cs`, `TenantUnitOfWork.cs`, `StartupGuards.cs`, `StartupGuardFilter.cs`, `QueueKindRegistry.cs`, `AgentAllowlist.cs`, `StubKeyIntrospector.cs`, `EntraClaimsMapper.cs`, `FixtureReviewerIdentitySource.cs`, `JsonGate.cs`, `Jcs.cs`, `ApprovalExecution.cs`, `ProblemResults.cs`, Razor pages under `Pages/Review`, `sql/migrations/0004,0005,0008,0009`, `config/*.yaml`, contracts under `contracts/queue-item/v1`, `contracts/introspection`, `contracts/reviewer`
- Tests read for conventions and breakage: `CredentialRouteMatrixTests`, `ArchitectureTests`, `PrivilegeMatrixTests`, `MigrationRunnerTests`, `QueueTableTests`, `QueueEventsTests`, `QueueSchemaTests`, `ContractSchemaTests`, `UiSurfaceTests`, `Beat4DemoTests`, `OliveApiFactory`, `SentinelScan`, `ReviewerSessionTests`, `QueueServiceDecideTests`
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md` (IR-1), `.planning/phases/07-reviewer-ui-and-entra-oidc/07-REVIEW.md`, `07-UI-SPEC.md`, `05-CONTEXT.md`
- https://learn.microsoft.com/en-us/aspnet/core/security/authentication/policyschemes?view=aspnetcore-10.0 (ForwardDefaultSelector; forwarding of authenticate/challenge/forbid)

### Secondary (MEDIUM confidence)
- `System.Buffers.Text.Base64Url` availability from .NET 9 (training knowledge corroborated by SDK 10.0.400 being installed; confirm by compiling in Wave 0)

### Tertiary (LOW confidence)
- None beyond the Assumptions Log items.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH, nothing new is added and all pieces were read in the repo.
- Architecture (dispatch, migration, transitions): HIGH for the mechanics; MEDIUM for the policy-scheme challenge/forbid interplay, which follows the documented forwarding rules but is not yet exercised by a test (Wave 0 matrix rows are the proof).
- Pitfalls: HIGH, each traced to a specific file or test assertion.

**Research date:** 2026-10-25
**Valid until:** 30 days (stable internal code); re-check if Phase 7 review fixes (WR-02..07) land first, since they touch `Program.cs` and `Item.cshtml.cs`.
