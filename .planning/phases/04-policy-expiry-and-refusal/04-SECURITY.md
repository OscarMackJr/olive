---
phase: 04
slug: policy-expiry-and-refusal
status: verified
threats_open: 0
asvs_level: 1
created: 2026-10-23
---

# Phase 04 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Audit base: f54db934ed9eecd783d9cc5717c9fe799d7e2c68 .. HEAD (e247302). Register authored at plan time (04-01..04-07 PLAN.md `<threat_model>`).

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| repo config to service | catalog YAML and `config/agents.yaml` are source-controlled, parsed at startup | action entitlements, expiry ceilings, rate limits, agent entitlements |
| service to agent | GET /v1/catalog exposes entry fields to authenticated agents | entitlement names (not secrets) |
| agent to POST /v1/proposals | action_type, parameters, justification, expires_in_seconds are untrusted | proposal body (references only) |
| endpoint to proposal_event | audit detail written from a static builder | static check names and reason codes |
| service to DB | expires_at written only by the service at insert; lock, count, insert and CAS run as olive_app under forced RLS | proposal rows, events |
| Olive to directory resolver | outbound read; tenant and on_behalf_of cross here | tenant id, user id (in); group names (out, untrusted until intersected) |
| concurrent agent requests | many submissions race for one (tenant, agent, action) budget | rate budget |
| contract docs to external consumers | Liam and hometown read contracts/proposal/v1 | schema shape, hash inputs |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation / Evidence | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-04-01 | Elevation | CatalogLoader null grants | mitigate | `required_entitlement` in schema `required` (contracts/catalog/v1/action-type.schema.json:3); loader throws Invalid on blank entitlement / missing expiry (src/Olive.Catalog/CatalogLoader.cs:120-124); evaluator denies blank (src/Olive.Domain/PolicyEvaluator.cs:76). Tests: CatalogLoaderTests.cs:75, PolicyEvaluatorTests.cs:98 | closed |
| T-04-02 | Elevation | AgentAllowlist | mitigate | Missing `entitlements` -> empty set, blank entry throws (src/Olive.Catalog/AgentAllowlist.cs:245-249); unknown agent -> `None` (AgentAllowlist.cs:267-268); strict YamlDotNet mapping, no IgnoreUnmatchedProperties (AgentAllowlist.cs:222-224); embedded resource only, no env/appsettings path (AgentAllowlist.cs:257-263, src/Olive.Host/Program.cs:61); no registration member. Test: AgentAllowlistTests.cs:66 | closed |
| T-04-04 | Tampering | vendored vocabulary | mitigate | `git diff f54db93..HEAD -- contracts/vendored` is empty; pinned hash check FND-03 remains (src/Olive.Application/VendoredContracts.cs:7,17; src/Olive.Host/Startup/StartupGuards.cs:18-23) | closed |
| T-04-05 | Elevation | out-of-purpose proposal reaching a human | mitigate | Purpose evaluated before insert, refusal stored via StoreRefusalAsync (src/Olive.Host/Endpoints/ProposalEndpoints.cs:219-228); refused_policy is terminal (src/Olive.Domain/ProposalStates.cs:114-119) and TransitionAsync rejects from/to refused_policy (src/Olive.Persistence/PostgresProposalStore.cs:245-248). Tests: PolicyEndpointTests.cs:57 (asserts state != awaiting_approval), LifecycleTransitionTests.cs:39,108 | closed |
| T-04-06 | Info disclosure | policy_evaluated detail and logs | mitigate | Detail built from static names/reason codes only (src/Olive.Domain/PolicyEvaluator.cs:21-52); refusal log carries tenant/agent/reason only (ProposalEndpoints.cs:116). Sentinel tests: PolicyEndpointTests.cs:85 (events + logs), PolicyEvaluatorTests.cs:58 | closed |
| T-04-07 | DoS | unknown-action throwing (500) | mitigate | `SubmissionOutcome.NotInCatalog` -> stored `not_in_catalog` refusal, no exception (ProposalEndpoints.cs:145-152). Test: PolicyEndpointTests.cs:39 | closed |
| T-04-08 | Tampering | replay re-evaluating policy | mitigate | `DecideExistingAsync` runs before validation/policy (ProposalEndpoints.cs:120-124). Tests: PolicyEndpointTests.cs:107, 285 (no new events, resolver not called again), SubmissionOrderTests.cs:203 | closed |
| T-04-09 | Repudiation | missing audit of refusals | mitigate | `submitted` + `policy_evaluated` + CAS + `policy_refused` inside one `uow.ExecuteAsync` transaction (PostgresProposalStore.cs:42-124; rate refusal path :185-198). Test: PolicyEndpointTests.cs:39,57 assert the event set. Note: REVIEW WR-02 (event type keyed on `policyDetailJson is null`) is a latent fragility; all current policy callers pass detail (ProposalEndpoints.cs:100) | closed |
| T-04-10 | Tampering | client-lengthened expiry | mitigate | `requested > entry.MaxExpirySeconds` -> 400 (ProposalEndpoints.cs:171-174); server computes absolute expiry from injected clock and caps at key expiry (ProposalEndpoints.cs:254-258). Tests: ExpiryTests.cs:84 (28801 -> 400), :111 (key cap), :121 (absolute). Note: REVIEW WR-04 (key expiry <= now) is a robustness item; introspection rejects inactive keys upstream | closed |
| T-04-11 | DoS | numeric parsing 500 (1.0, 1e0, huge) | mitigate | Schema `integer, minimum 1` (contracts/proposal/v1/proposal-request.schema.json:16); `TryGetInt64` + kind check -> 400 (ProposalEndpoints.cs:159-169). Tests: ExpiryTests.cs:99 (1.0, 1e0, 99999999999999999999), :84; ContractSchemaTests.cs:127 | closed |
| T-04-12 | Tampering | expires_at mutated after insert | mitigate | `grant insert (expires_at)` only, no UPDATE grant (sql/migrations/0007_policy_expiry_rate.sql:6); UPDATE grants remain state (0002:4) and refusal_reason (0006:13) only. Test: PrivilegeMatrixTests.cs:226 (42501 on update expires_at) | closed |
| T-04-14 | Elevation | entitlement fail-open on null/empty | mitigate | `!IsNullOrWhiteSpace(required)` && `userGroups is {Count:>0}` && `agentEntitlements is {Count:>0}` (PolicyEvaluator.cs:76-78); UnknownUser -> null groups -> refuse (ProposalEndpoints.cs:243-244). Tests: PolicyEvaluatorTests.cs:98,104; PolicyEndpointTests.cs:176 | closed |
| T-04-15 | Elevation | union instead of intersection | mitigate | Conjunction of both sides (PolicyEvaluator.cs:76-78). Tests: PolicyEvaluatorTests.cs:82; PolicyEndpointTests.cs:152 (user-only missing), :184 (agent-only missing), :194 (both) | closed |
| T-04-16 | Elevation | directory outage silently allows | mitigate | Timeout/throw/CallerRejected/Malformed/default -> `Unavailable` (src/Olive.Application/DirectoryVerifier.cs:26-49); endpoint returns 503 retryable before any insert (ProposalEndpoints.cs:235-241). Tests: PolicyEndpointTests.cs:224 (6 faults, 503, retryable, 0 rows, no insert), DirectoryVerifierTests.cs:34-68 | closed |
| T-04-17 | Info disclosure | tenant crossing in lookup | mitigate | Resolver called with `key.TenantId` (introspected) only (ProposalEndpoints.cs:234); fixture keyed `(Tenant, User)` (src/Olive.Integrations.Entra/FixtureDirectoryGroupResolver.cs:81,104). Tests: PolicyEndpointTests.cs:207 (cross-tenant), :194 (tenant asserted), FixtureDirectoryTests.cs:23 | closed |
| T-04-18 | Info disclosure | groups or UPN in logs/events | mitigate | Directory Warning log has tenant, agent, cause, exception type only (ProposalEndpoints.cs:238-239); verifier stores type name only (DirectoryVerifier.cs:32); verdict JSON never copies names (PolicyEvaluator.cs:73). Tests: PolicyEndpointTests.cs:301 (SentinelScan over events, logs, response), :320 | closed |
| T-04-19 | Spoofing | fixture resolver in production | mitigate | `FixtureDirectoryGroupResolver : INonProductionOnly` (FixtureDirectoryGroupResolver.cs:79); StartupGuards registration + resolution pass with decorator unwrap (src/Olive.Host/Startup/StartupGuards.cs:45-112). Tests: StartupGuardTests.cs:223 (factory registration), :234 (decorated), FixtureDirectoryTests.cs:41 | closed |
| T-04-20 | Elevation | write credential or HTTP client introduced | mitigate | Port is read-only lookup with no credential parameter (src/Olive.Application/IDirectoryGroupResolver.cs:12-14); Entra project adds only an EmbeddedResource, no package (csproj diff). Tests: ArchitectureTests.cs:90 (REQ_ACT_04 no outbound network assemblies), :151-152 (HttpClient banned) | closed |
| T-04-21 | DoS/Elevation | rate limit overshoot by concurrency | mitigate | `pg_advisory_xact_lock` -> count -> insert -> CAS in one transaction (src/Olive.Persistence/ProposalSql.cs:16-22; PostgresProposalStore.cs:127-210). Test: RateLimitTests.cs:123 (12 concurrent, limit 3 -> exactly 3 accepted, 9 rate_limited). Note: REVIEW WR-01 lock-key ambiguity only over-serializes, never under-locks | closed |
| T-04-22 | DoS | refusal flooding burns budget | mitigate | `state <> 'refused_policy'` in CountAccepted (ProposalSql.cs:20-22). Test: RateLimitTests.cs:95 | closed |
| T-04-23 | Tampering | illegal state jumps | mitigate | `CanTransition` table (ProposalStates.cs:114-122); checked before DB (PostgresProposalStore.cs:251-254); `UPDATE ... AND state = @from_state` CAS (ProposalSql.cs:31-35); state not in INSERT grant (0003:5, 0004:11, 0006:12, 0007:6); refusal-agrees check 0006 (PrivilegeMatrixTests.cs:271-273). Tests: LifecycleTransitionTests.cs:21,74,85,108 | closed |
| T-04-24 | Info disclosure | cross-tenant rate leakage | mitigate | CountAccepted has `tenant_id = @tenant_id` (ProposalSql.cs:21) and runs inside `uow.ExecuteAsync(tenantId, ...)` RLS tenant tx (PostgresProposalStore.cs:128). Tests: RateLimitTests.cs:154 (per tenant), :167 | closed |
| T-04-25 | Repudiation | unaudited transitions | mitigate | One `transition` event per successful CAS in same tx (PostgresProposalStore.cs:256-277; accept path :201-208; refusal :190-197). Test: LifecycleTransitionTests.cs:96 (exactly one event), :85 (stale CAS -> no event) | closed |
| T-04-26 | DoS | lock held on refused proposals / deadlock | mitigate | `InsertEvaluatedAsync` (only lock site) called only after catalog, purpose and entitlement allowed (ProposalEndpoints.cs:225-268); single LockRate per tx (PostgresProposalStore.cs:135). Tests: RateLimitTests.cs:190 (purpose refusal completes while lock held), SubmissionOrderTests.cs:143 (entitlement refusal, no rate-lock marker) | closed |
| T-04-27 | Elevation | check reordering regressions | mitigate | Peeled-fault order tests: SubmissionOrderTests.cs:39-185 (steps 1-8 and documented marker order) | closed |
| T-04-28 | Tampering | breaking v1 contract for Liam | mitigate | Only optional properties added (proposal-request.schema.json:16 `expires_in_seconds`, proposal.schema.json:28 `expires_at`; not in `required`); proposal_hash keys fixed at `action_type, party_ref, parameters, justification` (src/Olive.Domain/Jcs/Jcs.cs:11) and Jcs.cs has no diff this phase. Pre-phase body shape still validates: ContractSchemaTests.cs:140 (FullBody without expires_in_seconds), :37 paired examples | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-04-01 | T-04-03 | GET /v1/catalog exposes `required_entitlement` and `max_expiry_seconds`. Entitlement names are not secrets; fields are additive within catalog v1; no group membership or user data is exposed. | phase 04 plan (04-01-PLAN threat model) | 2026-10-23 |
| AR-04-02 | T-04-13 | `expires_in_seconds` is deliberately excluded from the four-key `proposal_hash` (D-16) so hometown hash agreement is unchanged; it is covered by `body_sha256`, so a changed value under the same run_id is a 409 (ExpiryTests.cs:147). | phase 04 plan (04-03-PLAN threat model) | 2026-10-23 |
| AR-04-03 | T-04-29 | API-08 ordering markers are static strings recorded by test-only decorators (tests/Olive.Tests); they carry no request values. | phase 04 plan (04-07-PLAN threat model) | 2026-10-23 |
| AR-04-04 | T-04-SC | No NuGet packages were added this phase. Only csproj change is an `EmbeddedResource` for directory fixtures in Olive.Integrations.Entra.csproj (verified by `git diff f54db93..HEAD -- src/*/*.csproj tests/*/*.csproj`). | phase 04 plans (all) | 2026-10-23 |

*Accepted risks do not resurface in future audit runs.*

---

## Unregistered Flags

None. All seven SUMMARY.md `## Threat Flags` sections report "None" (04-02 adds: no new endpoints or trust boundaries).

## Informational (from 04-REVIEW.md, not blocking)

- WR-02: refusal event type selected by `policyDetailJson is null` (PostgresProposalStore.cs:86,115). Current callers are correct; a future caller omitting detail would mislabel a policy refusal as `trace_refused`. Relates to T-04-09.
- WR-04: if introspected key expiry is <= now, an accepted proposal could carry `expires_at <= created_at` (ProposalEndpoints.cs:256-257). Does not lengthen expiry; relates to T-04-10 robustness only.
- WR-01: advisory-lock key `tenant|agent|action` can collide; effect is over-serialization only. Relates to T-04-21.
- WR-03: DirectoryVerifier reuses TraceVerifierOptions.Timeout; fail-closed behaviour unaffected.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-23 | 30 (26 mitigate, 4 accept) | 30 | 0 | gsd-security-auditor (Claude); targeted test run 306/306 passed |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-23
