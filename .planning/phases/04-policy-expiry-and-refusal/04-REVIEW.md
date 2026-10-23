---
phase: 04-policy-expiry-and-refusal
reviewed: 2026-10-23T00:00:00Z
depth: standard
files_reviewed: 12
files_reviewed_list:
  - src/Olive.Domain/PolicyEvaluator.cs
  - src/Olive.Domain/ProposalStates.cs
  - src/Olive.Persistence/PostgresProposalStore.cs
  - src/Olive.Persistence/ProposalSql.cs
  - src/Olive.Host/Endpoints/ProposalEndpoints.cs
  - src/Olive.Application/DirectoryVerifier.cs
  - src/Olive.Catalog/AgentAllowlist.cs
  - src/Olive.Catalog/CatalogLoader.cs
  - src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs
  - sql/migrations/0007_policy_expiry_rate.sql
  - contracts/proposal/v1/proposal-request.schema.json
  - src/Olive.Domain/Jcs/Jcs.cs
findings:
  critical: 0
  warning: 4
  info: 2
  total: 6
status: issues_found
---

# Phase 04: Code Review Report

**Reviewed:** 2026-10-23
**Depth:** standard
**Files Reviewed:** 12 (the remaining scoped files, including tests, were not read in full; see the scope note at the end)
**Status:** issues_found

## Summary

I found no blockers. The fail-closed paths hold up: directory failures return 503 and store nothing, and unknown users get empty groups, which makes the entitlement check refuse. The static verdict JSON carries no values or group names. The advisory lock, count and insert run in one transaction, and the tenant predicate is present in every SQL statement. Rate refusals and trace refusals are excluded from the count. The four warnings below are robustness and consistency problems.

## Warnings

### WR-01: Advisory-lock key is ambiguous and can collide across scopes

**File:** `src/Olive.Persistence/ProposalSql.cs:17`
**Issue:** The key is `tenant|agent|action` joined with `|`, and `agent_id` and `action_type` are not forbidden from containing `|`. Agent ids are constrained by the allowlist pattern, but `action_type` comes from the catalog, and the store accepts any string. Two scopes can therefore share a lock. That only causes extra serialization, not a missed lock, so the impact is low. The 64-bit hash can also collide. Neither case breaks correctness, but the code comment implies an exact per-scope lock.
**Fix:** Use a two-key lock, `pg_advisory_xact_lock(hashtext(tenant||agent), hashtext(action))`, or length-prefix the components. Document that collisions are harmless.

### WR-02: Event type for refused rows depends on whether `policyDetailJson` is null

**File:** `src/Olive.Persistence/PostgresProposalStore.cs:86,115`
**Issue:** `InsertCoreAsync` chooses between a `trace_refused` and a `policy_refused` audit event, and decides whether to write `policy_evaluated`, from `policyDetailJson is null`. A future caller that passes a policy refusal without a detail JSON would silently be recorded as `trace_refused`, which corrupts the audit trail. The null parameter also acts as a hidden mode switch.
**Fix:** Add an explicit enum or `bool isPolicyRefusal` parameter, or two distinct methods.

### WR-03: `DirectoryVerifier` reuses `TraceVerifierOptions.Timeout`

**File:** `src/Olive.Application/DirectoryVerifier.cs:12,21`
**Issue:** The directory lookup timeout is taken from the trace verifier's options. Tuning the trace timeout silently changes directory fail-closed behaviour. The two timeouts have different dependencies and should be independent.
**Fix:** Add `DirectoryVerifierOptions` or a dedicated `DirectoryTimeout` setting.

### WR-04: Accepted proposal can be stored with `expires_at` in the past, and the unreachable guard hides a contract

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:256-257`, `src/Olive.Persistence/PostgresProposalStore.cs:130-133`
**Issue:** `expires_at = min(now + ttl, key.ExpiresAt)`. If the introspected key expiry is at or before `now` (clock skew, or a stub introspector), the proposal is accepted into `awaiting_approval` with `expires_at <= created_at`. Nothing in this phase guards against that. The `ActionType is null` check in `InsertEvaluatedAsync` can never fire for the endpoint caller, so it is dead defensive code that throws an unmapped 500 if it ever does.
**Fix:** Refuse, or return 4xx, when `key.ExpiresAt <= now`. Alternatively assert `ExpiresAt > now` before insert. Make `NewProposal.ActionType` non-nullable for the evaluated path.

## Info

### IN-01: Insert parameter object duplicated

**File:** `src/Olive.Persistence/PostgresProposalStore.cs:46-68,145-167`
**Issue:** The 20-field anonymous parameter object for `ProposalSql.Insert` is copy-pasted between `InsertCoreAsync` and `InsertEvaluatedAsync`. A new column must be added in both places.
**Fix:** Extract a private `InsertParams(Guid tenantId, Guid id, NewProposal p)` helper.

### IN-02: Logging a refusal reason at Trace level

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:116`
**Issue:** The policy refusal is logged only at Trace, which is normally disabled in production. That is consistent with the trace paths, but operators get no signal of rate-limit refusals.
**Fix:** Consider logging rate-limit refusals at Information, using static reason codes only.

---

Scope note: the test files and several scoped contract, DTO and validator files were not read in full. The conclusions above cover only the files listed in the frontmatter.

_Reviewed: 2026-10-23_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
