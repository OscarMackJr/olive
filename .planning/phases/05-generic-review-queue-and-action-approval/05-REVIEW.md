---
phase: 05-generic-review-queue-and-action-approval
reviewed: 2026-10-23T00:00:00Z
depth: standard
files_reviewed: 23
files_reviewed_list:
  - sql/migrations/0008_review_queue.sql
  - config/queue-kinds.yaml
  - config/reviewer-tenants.yaml
  - build/demo-beat4.sh
  - src/Olive.Application/ApprovalIdentityCheck.cs
  - src/Olive.Application/IQueueStore.cs
  - src/Olive.Application/QueueService.cs
  - src/Olive.Application/UserIdentityVerifier.cs
  - src/Olive.Catalog/QueueKindRegistry.cs
  - src/Olive.Catalog/ReviewerTenantMap.cs
  - src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs
  - src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs
  - src/Olive.Host/Auth/ReviewerClaims.cs
  - src/Olive.Host/Endpoints/QueueEndpoints.cs
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs
  - src/Olive.Integrations.Entra/FixtureUserIdentityResolver.cs
  - src/Olive.Persistence/PostgresProposalStore.cs
  - src/Olive.Persistence/PostgresQueueStore.cs
  - src/Olive.Persistence/QueueSql.cs
findings:
  critical: 1
  warning: 5
  info: 4
  total: 10
status: issues_found
---

# Phase 5: Code Review Report

**Reviewed:** 2026-10-23
**Depth:** standard
**Files Reviewed:** 23 of the 33 files listed in the request. Some tiny interface and DTO files were read only through their uses. `ReviewerContext`, `IQueueKindRegistry`, `IReviewerIdentitySource`, `IReviewerTenantMap`, `IUserIdentityResolver`, `ProblemReasons`, `QueueDtos` and `QueueStates` were not opened directly.
**Status:** issues_found

## Summary

The transaction design is sound.
- Business outcomes are returned as values, so lazy expiry commits before the 409.
- The item update, proposal compare-and-set and both events share one `(conn, tx)`.
- `SELECT ... FOR UPDATE` under the default READ COMMITTED isolation level gives exactly one claim winner. The loser re-reads the committed state and gets `AlreadyClaimed`, not a serialization failure.
- Every queue SQL statement keeps the tenant predicate, and forced RLS backs it.
- The rationale is never placed in an event, a view, a log or a problem body.

The main defect is a check-then-act gap in the approve path. A reviewer can bypass the self-approval check by racing their own requests (CR-01).

## Critical Issues

### CR-01: Self-approval check can be skipped by racing claim and decide (APR-02 bypass)

**File:** `src/Olive.Application/QueueService.cs:75-92`
**Issue:** The `ApprovalIdentityCheck` runs only when the pre-read (outside the transaction) shows the item as `live`, meaning claimed by this reviewer and unexpired. The store then re-validates claimed_by and state under the row lock, but it never re-checks identity.

A requester who is also a `case_supervisor` can send two concurrent requests, a decide/approve and a claim.
1. The decide pre-read sees the item `open`, so `live` is false and the identity check is skipped.
2. The claim commits.
3. `store.DecideAsync` locks the row, sees `Claimed` with `claimed_by == actor`, and approves.

The self-approval guard is skipped and the proposal is approved by its own requester. This breaks the core-value invariant, and it is also not fail-closed. The comment "the store re-validates everything" does not hold for identity.
**Fix:** Run the identity check for every approve that is not already decided, whatever the pre-read state. Do not gate it on `live`.
```csharp
if (decision == QueueStates.Approve && item.State != QueueStates.Decided)
{
    var payload = await store.GetPayloadAsync(reviewer.TenantId, itemId, ct);
    if (payload is null) { return new DecisionResult.NotFound(); }
    var verdict = await identityCheck.CheckAsync(...);
    // handle SelfApproval / Unresolved / Unavailable as today
}
```
This costs an extra resolver call on approvals that were going to fail anyway. If that is unwanted, pass a verified-identity flag into `store.DecideAsync` and have it refuse approve when the flag is absent.

## Warnings

### WR-01: Rationale containing U+0000 causes an unhandled 500 (and a Postgres error)

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:135`, `src/Olive.Application/QueueService.cs:72`
**Issue:** `"rationale":"a\u0000b"` parses and passes the `IsNullOrWhiteSpace` and length checks. Postgres rejects NUL in `text` (22021), so `Decide` throws inside the unit of work. The transaction rolls back and the exception handler returns a 500. The caller gets a server error for a client fault.
**Fix:** Reject control characters, or at least NUL, in `DecideAsync` and return `InvalidRequest`.
```csharp
if (rationale.Contains('\0')) { return new DecisionResult.InvalidRequest(); }
```

### WR-02: Claim and release do not validate the reviewer oid

**File:** `src/Olive.Application/QueueService.cs:54-58`, `src/Olive.Host/Auth/ReviewerClaims.cs:32`
**Issue:** `ToReviewerContext` maps a missing Oid claim to `""`. `DecideAsync` guards against a blank oid, but `ClaimAsync` and `ReleaseAsync` do not.
- An empty oid can claim an item. The DB check only requires `claimed_by is not null`, so `''` is accepted.
- That item can then never be decided, because `DecideAsync` throws `ArgumentException` for a blank actor, which is a 500.
- Phase 7 Entra claims make a missing oid more plausible.

**Fix:** Return null from `ToReviewerContext` when the oid or tid claim is blank, so the group filter returns 403. Alternatively, add the same guard to claim and release.

### WR-03: CS8509 is suppressed over five switch expressions with no default arm

**File:** `src/Olive.Host/Endpoints/QueueEndpoints.cs:67, 166`
**Issue:** The `#pragma warning disable CS8509` region covers every result-to-HTTP mapping, including the 14-arm decision switch. Today all arms are covered. I checked the Payload, Events, Claim, Release and Decision hierarchies. A future result subtype will not fail the build. It will throw `SwitchExpressionException` at runtime and return a 500 on a path that holds state. For a decide that has already committed, the client then sees a 500 instead of the outcome. The hierarchies are open: the `abstract record` base has a public constructor, so the compiler cannot treat them as closed.
**Fix:** Pick one of the following.
- Add an explicit `_ => throw new InvalidOperationException("Unmapped result")` and drop the pragma.
- Keep the pragma and add a reflection test that enumerates the nested subtypes of each result base and asserts each is mapped.
- Make the bases `private protected` constructors, which closes the hierarchy inside the assembly. Cross-assembly mapping still needs a test.

### WR-04: Reviewers without the supervisor role can claim and lock items (DoS)

**File:** `src/Olive.Application/QueueService.cs:53-58`
**Issue:** Claim and release do not require the kind's `required_role`. Any authenticated reviewer in the tenant can claim every open `action_approval` item. Only the claimant can release, and there is no claim TTL, takeover or admin release. Supervisors are then locked out until the proposal expires. The code comment says this is deliberate, but there is no mitigation and the 409 `AlreadyClaimed` does not distinguish this case.
**Fix:** Require the kind's `required_role` for claim, or add a claim lease with expiry. At minimum, document the accepted risk in a decision record.

### WR-05: GET and events return stale expired items inconsistently with the list

**File:** `src/Olive.Persistence/PostgresQueueStore.cs:71-94`, `src/Olive.Persistence/QueueSql.cs:29-33`
**Issue:** `List` hides open or claimed items whose `expires_at <= now`. `Get`, `Payload` and `Events` do not, so the same item is invisible in the list but readable and shown as `open` or `claimed` by id.
- The proposal stays `awaiting_approval` and the item stays `open` indefinitely if nobody touches it, because there is no sweeper.
- `state=withdrawn` lists never include lazily-expired items that have not yet been touched.

The result is observable disagreement between endpoints.
**Fix:** Apply the same expiry predicate or computed state in `GetItem`, or have `GetAsync` report an effective state of `withdrawn/proposal_expired` for expired rows. Alternatively, add a sweeper that calls `ExpireInTransactionAsync`.

## Info

### IN-01: Likely causes of the intermittent StartupGuardTests flakiness (cannot be proven from source alone)

**File:** `src/Olive.Host/Startup/StartupGuardFilter.cs:19-29`, `src/Olive.Host/Startup/StartupGuards.cs:113-141`
**Issue:** The startup filter runs synchronously during `Build`. Three things in it depend on timing or process-global state.
1. `CheckDatabase` blocks with `.GetAwaiter().GetResult()` on a real Npgsql connect plus the safety queries. Under full-suite parallel load, the thread pool is starved by other Testcontainers tests and blocking waits. This makes timeouts probable, and any exception is mapped to `DB-07`. Tests that expect success, or that expect a specific guard, then see `DB-07`.
2. `CheckNoNonProductionComponents` resolves every closed service type in a scope. That includes `NpgsqlDataSource` and each fixture and stub singleton. Any resolution failure is collapsed to `FND-05`, so a transient failure looks like a guard hit.
3. Process-global state is shared across parallel tests.
   - `DefaultTypeMap.MatchNamesWithUnderscores` is a Dapper global set in two static constructors. It is benign, but it is order-dependent for any other Dapper user.
   - `AddDotEnvBelowEnvironment` reads a shared `.env` at the content root, so a test that writes it can race another host build.
   - `OLIVE_*` configuration read from process environment variables is shared. I found no `SetEnvironmentVariable` in `src/` and the grep of `tests/` did not complete, so I have not confirmed whether tests mutate the environment.

**Fix:**
- Put the StartupGuard tests in a serial xUnit collection (`[Collection]` with `DisableParallelization`).
- Pass settings through `WebApplicationFactory.ConfigureAppConfiguration` instead of the environment.
- Give `CheckDatabase` an explicit short command and connect timeout.
- Where possible, assert on the guard code, not on a timing-sensitive success path.

### IN-02: Hand-built JSON for event details

**File:** `src/Olive.Persistence/PostgresQueueStore.cs:160, 182, 282`
**Issue:** Event `detail` JSON is built by string concatenation. `trigger` is a literal and `decision` is validated. `authSource` is constrained by the item UPDATE (the check allows only entra or stub) and `itemId` is a Guid, so this is safe today. A future caller passing an unvalidated string would corrupt or inject JSON.
**Fix:** Use `JsonSerializer.Serialize(new { ... })`.

### IN-03: `NotClaimant` problem title is wrong for the decision path

**File:** `src/Olive.Host/Problems/ProblemResults.cs:44`, `src/Olive.Host/Endpoints/QueueEndpoints.cs:152`
**Issue:** The title reads "Only the claimant may release this queue item", but it is also returned for decide. A decided-by-someone-else item also returns `NotClaimant` (403), which hints that the item exists and was decided.
**Fix:** Use a generic title such as "Only the claimant may perform this action".

### IN-04: Test-only tenant mappings ship in the production config

**File:** `config/reviewer-tenants.yaml:5-10`
**Issue:** The fixture groups `tenant-gamma-reviewers` and `tenant-alpha-duplicate`, which exist to exercise the multi-tenant fail-closed case, are in the embedded production config. The map is not an `INonProductionOnly` component, so the FND-05 guard does not bar it. The tenant GUIDs are fixed.
**Fix:** Move test-only groups to a test-only config, or make the map source environment-selected.

---

_Reviewed: 2026-10-23_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
