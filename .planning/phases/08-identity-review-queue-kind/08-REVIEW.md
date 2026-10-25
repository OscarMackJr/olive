---
phase: 08-identity-review-queue-kind
reviewed: 2026-10-25T00:00:00Z
depth: standard
files_reviewed: 33
files_reviewed_list:
  - build/demo-identity-review.sh
  - config/producers.yaml
  - sql/migrations/0010_identity_review_producer.sql
  - src/Olive.Application/IProducerRegistry.cs
  - src/Olive.Application/IQueueStore.cs
  - src/Olive.Application/IServiceTokenVerifier.cs
  - src/Olive.Application/ProducerContext.cs
  - src/Olive.Application/ProducerCursor.cs
  - src/Olive.Application/QueueService.cs
  - src/Olive.Catalog/ProducerRegistry.cs
  - src/Olive.Contracts/ProblemReasons.cs
  - src/Olive.Contracts/QueueDtos.cs
  - src/Olive.Domain/InboundBearerToken.cs
  - src/Olive.Domain/QueueStates.cs
  - src/Olive.Host/Auth/QueueCallerFilters.cs
  - src/Olive.Host/Auth/ServiceClaims.cs
  - src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs
  - src/Olive.Host/Endpoints/QueueEndpoints.cs
  - src/Olive.Host/Pages/Review/Audit.cshtml
  - src/Olive.Host/Pages/Review/Audit.cshtml.cs
  - src/Olive.Host/Pages/Review/Index.cshtml
  - src/Olive.Host/Pages/Review/Index.cshtml.cs
  - src/Olive.Host/Pages/Review/Item.cshtml
  - src/Olive.Host/Pages/Review/Item.cshtml.cs
  - src/Olive.Host/Pages/Shared/FlashMessages.cs
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Integrations.Popeye/Olive.Integrations.Popeye.csproj
  - src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs
  - src/Olive.Persistence/PostgresQueueStore.cs
  - src/Olive.Persistence/QueueSql.cs
findings:
  critical: 1
  warning: 4
  info: 5
  total: 10
status: issues_found
---

# Phase 8: Code Review Report

**Reviewed:** 2026-10-25
**Depth:** standard
**Files Reviewed:** 33
**Status:** issues_found

## Summary

I reviewed the phase-8 diff (`9a109c4..HEAD`) for the identity_review producer path: service-token authentication and dispatch, the producer registry, create, withdraw and poll, the defer transition, migration 0010, and the reviewer page changes.

Several things hold up:
- Tenant predicates and producer predicates are present in every new SQL statement. RLS stays forced.
- Create is idempotent through `ON CONFLICT DO NOTHING` under READ COMMITTED, with the follow-up read in the same transaction.
- Withdraw and decide race correctly under `FOR UPDATE`.
- The rationale never reaches the producer poll.
- `ApprovalExecution` cannot run for identity items because it requires `decision == "approve"` and a `ProposalId`.
- The fixture verifier is caught by the FND-05 resolution pass.

The main defect is in the poll cursor. Keyset pagination over `decided_at` is not safe when the stamp is taken before commit. Concurrent decisions can be skipped for good, so bluto never learns them. That breaks IRQ-03.

Other issues:
- Credential dispatch is by header shape only. Service tokens and agent session keys therefore cross into each other's verifier ports, which D-05 forbids. D-07's 403 for a service token on `/v1/proposals` is not delivered; the caller gets 401.
- With no expiry, a reviewer who lacks the steward role can lock identity items indefinitely.
- There are clock-source inconsistencies and a demo runner that can pass on a stale transcript.

## Narrative Findings (AI reviewer)

## Critical Issues

### CR-01: Poll cursor permanently skips decisions committed out of `decided_at` order

**File:** `src/Olive.Persistence/PostgresQueueStore.cs:354`, `src/Olive.Persistence/QueueSql.cs:72-76`, `src/Olive.Application/QueueService.cs:191-193`
**Issue:**
- `DecideIdentityAsync` stamps `decided_at` from `TimeProvider` after the row lock (line 354). The transaction commits later: after the UPDATE, the event INSERT and `Reload`.
- `TenantUnitOfWork` runs at READ COMMITTED (default `BeginTransactionAsync`).
- The poll uses `(decided_at, item_id) > (@after_at, @after_id)` and returns the last row as `next_cursor`.

The failure sequence:
1. Transaction A locks item X and stamps T1.
2. Transaction B locks item Y, stamps T2 > T1, and commits first.
3. bluto polls. It sees Y, but not X, which is still uncommitted, and receives cursor (T2, Y).
4. A commits with `decided_at = T1 < T2`.
5. Every later poll uses `> (T2, Y)`, so X is never returned.

bluto then never applies that steward decision. That is silent data loss on the IR-1 §6 feed. Ordinary clock skew between Olive instances causes the same problem, and stamping inside the lock does not prevent either case.
**Fix:** Make the cursor commit-order safe. Options, in order of preference:
```sql
-- (a) Settle window: never return rows that a still-open transaction could precede.
--     @settle_before = poll time minus a bound greater than the max decide transaction duration (e.g. statement_timeout + margin).
... and state = 'decided' and decided_at <= @settle_before
    and (@after_at::timestamptz is null or (decided_at, item_id) > (@after_at::timestamptz, @after_id::uuid))
```
Option (b) is to serialize identity decides against the poll with a per-tenant advisory lock: shared in decide, exclusive in poll (`pg_advisory_xact_lock_shared` / `pg_advisory_xact_lock` on a tenant key). The poll then cannot read while a decide is in flight.

Option (c) is a commit-ordered feed table written by a deferred trigger. Like (b), it still needs a visibility guard such as `pg_snapshot_xmin(pg_current_snapshot())`.

Whichever option is chosen, add a Testcontainers test that holds transaction A open across a poll and asserts X appears on a later poll.

## Warnings

### WR-01: Header-shape dispatch sends service tokens to the agent-key introspector, and agent keys to the service-token verifier (D-05/D-07)

**File:** `src/Olive.Host/Auth/ServiceTokenAuthenticationHandler.cs:60-63`, `src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs:23-36` (call site), `contracts/introspection/index.json`, `contracts/service-token/index.json`
**Issue:** `AgentKeyHeader.Name` and the service token both use `Authorization: Bearer`.
- On `/v1/proposals` and `/v1/catalog`, the default AgentKey scheme wraps a bluto service token as a `SessionKey` and passes it to `IKeyIntrospector`.
- On `/v1/queue/items`, `QueueCaller.Select` sends any agent session key to `IServiceTokenVerifier`.

D-05 requires that "session keys and service tokens never share a code path". In the real system each credential would be submitted to the other popeye introspection endpoint or audience.

D-07 also requires `403 principal_not_permitted` for a service token on `/v1/proposals`. The code returns `401 key_inactive`, because the stub introspector maps the bluto tokens to `inactive.json`. The matrix test's comment at line 157 accepts this.

The 403 for an agent key on producer routes is also not real behavior. It exists only because the service-token fixture index lists `fixture-key-liam-alpha` → `agent.json`. A real verifier given an agent session key would most likely return inactive, which gives a 401. So the IDN-03 evidence comes from the fixtures, not from the code.
**Fix:** Discriminate credentials before any verifier call: for example a distinct header or token prefix agreed with popeye, or the JWT `typ`/`aud` if tokens are JWTs. Refuse the wrong type with 403 `principal_not_permitted` without calling a verifier. If no such discriminator can be agreed, record the deviation from D-05/D-07 explicitly and remove the cross-listed fixtures, so the tests stop asserting behavior that production cannot exhibit.

### WR-02: A reviewer without the steward role can claim an identity item and block it indefinitely

**File:** `src/Olive.Application/QueueService.cs:76-80`, `src/Olive.Host/Pages/Review/Item.cshtml:108-114`
**Issue:**
- Claiming requires no role. The comment says this is deliberate.
- identity_review has `expires: false`.
- Only the claimant may release (Q7).

A supervisor without `role:identity_steward` (the TC-IRQ-04 fixture) can claim an identity item. They then cannot decide it (`role_not_permitted`), but they can keep it in `claimed` forever. No steward can take it, and nothing ever times it out. The only way out is a bluto withdraw. The page warns "You do not hold the role needed to decide this item" and still renders the Claim button. For action_approval, expiry bounded this risk. For identity items nothing bounds it.
**Fix:** Require the kind's `required_role` at claim time, at least for kinds with `expires: false`. Return `RoleNotPermitted` from `ClaimAsync` (and from the store under the lock if needed), and hide the Claim form when `Model.LacksRole`.
```csharp
var item = await store.GetAsync(reviewer.TenantId, itemId, ct);
if (item is not null && registry.TryGet(item.Kind, out var def) && !def.Expires
    && !reviewer.Groups.Contains(def.RequiredRole, StringComparer.Ordinal)) { return new ClaimResult.RoleNotPermitted(); }
```

### WR-03: The new store methods ignore the caller's `now` and mix clock sources

**File:** `src/Olive.Persistence/PostgresQueueStore.cs:9,112-133,343,354`, `src/Olive.Persistence/QueueSql.cs:4,12-15`
**Issue:**
- `CreateIdentityItemAsync` takes `DateTimeOffset now` but never uses it. `InsertIdentityItem` omits `created_at`, so the DB default `now()` sets it. That contradicts the invariant stated at `QueueSql.cs:4` ("Time comes from the caller (@now), never from the database clock").
- `DecideIdentityAsync` also ignores `now` and stamps from an optional `TimeProvider? time = null` that silently falls back to `TimeProvider.System`.

The result is that `created_at` comes from the DB clock and `decided_at` from the app clock. Under skew, `decided_at < created_at` is possible. Fake-time tests cannot control `created_at` (age, list order). Any construction that omits `time` drifts silently to the wall clock.
**Fix:**
- Pass `created_at = @now` explicitly in `InsertIdentityItem`; this needs an insert grant on `created_at`, or use the existing column grant pattern.
- Make `TimeProvider` a required constructor parameter.
- Either remove the unused `now` parameters from the port or use them.
- Update the `QueueSql` header comment so it matches the actual behavior.

### WR-04: The demo runner can pass on a stale transcript

**File:** `build/demo-identity-review.sh:10-17`
**Issue:** The script checks only that `TestResults/identity-review-transcript.md` exists and is non-empty. It does not delete the file first. If the `Demo=identity` filter matches no tests (a renamed trait, or a build that skips the class), `dotnet test` exits 0. A transcript left from an earlier run then satisfies `-s`, and the D-18 evidence is reported as produced when it was not.
**Fix:**
```bash
transcript="$root/TestResults/identity-review-transcript.md"
rm -f "$transcript"
dotnet test tests/Olive.Tests --filter "Demo=identity"
```
Also consider failing when the test count is zero, for example by parsing the TRX output. The same pattern exists in `demo-beat2.sh` and `demo-beat4.sh`.

## Info

### IN-01: The producer tenant gate uses the reviewer group map, not the vocabulary tenant list

**File:** `src/Olive.Host/Auth/QueueCallerFilters.cs:107`
**Issue:** D-08 asks for "a known vocabulary tenant UUID". `IReviewerTenantMap.AllTenants` is the set of tenants that have a reviewer group mapped. The check fails closed, so a vocabulary tenant with no reviewer group is refused with 403. The two sources of truth can drift.
**Fix:** Check against the vocabulary tenant set. Alternatively, document the reviewer map as the authoritative source, since it also means a reviewer exists to decide the item.

### IN-02: The poll does not check that the producer is registered for identity_review

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:112-130`
**Issue:** `PollAsync` accepts `kind=identity_review` from any registered producer without checking `producer.KindSubtypes`. Today only bluto exists, and the SQL filters by producer, so nothing leaks. The authorization model is still incomplete: a future producer without the identity kind would get an empty 200 instead of a refusal.
**Fix:** `if (!producer.KindSubtypes.ContainsKey(QueueStates.IdentityReview)) return invalid;` (or 403).

### IN-03: Duplicated producer gating, and `ProducerContext.ClientId` is unused

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:99-104`, `src/Olive.Host/Auth/QueueCallerFilters.cs:119-126`, `src/Olive.Application/ProducerContext.cs:4`
**Issue:** `ListOrPollAsync` re-implements the `ProducerOnly` filter body. `ProducerContext.ClientId` is set but never read.
**Fix:** Factor the gating into a single helper that returns `(IResult?, ProducerContext?)`. Drop `ClientId` or use it in event actors.

### IN-04: The stub verifier accepts `expires_at` without an offset

**File:** `src/Olive.Integrations.Popeye/StubServiceTokenVerifier.cs:77`
**Issue:** `DateTimeStyles.AssumeUniversal` silently accepts offset-less timestamps, which the RFC 3339 contract shape does not allow. A malformed fixture is treated as valid instead of failing closed.
**Fix:** Require an explicit `Z` or offset. For example, parse with `DateTimeStyles.RoundtripKind` and reject `DateTimeKind.Unspecified`, or match a strict RFC 3339 regex before parsing.

### IN-05: The startup cross-check treats the producer name as a ref scheme

**File:** `src/Olive.Host/Startup/StartupGuards.cs` (`CheckProducers`, `kind.RefSchemes.Contains(producer.Producer)`)
**Issue:** The check works only because the producer `bluto` and the ref scheme `bluto` happen to share a name. A producer whose name differs from its ref scheme would fail startup wrongly, or pass wrongly. In addition, `QueueService.CreateAsync:161` hard-codes the `bluto:` prefixes rather than deriving them from the registry.
**Fix:** Add an explicit `ref_scheme` to `producers.yaml`, or derive the prefixes from the kind registry, and validate against that.

---

_Reviewed: 2026-10-25_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
