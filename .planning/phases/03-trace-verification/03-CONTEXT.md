# Phase 3: Trace Verification - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning

<domain>
## Phase Boundary

Before a proposal is stored, Olive obtains a popeye service token, resolves the proposal's `trace_ref` through the hometown trace resolver (fixture-backed in this phase), and applies the five D§17.3 checks in order. A proposal is either stored as verified (with `trace_verification` and `trace_schema_version`), stored as `refused_policy` with a trace reason, rejected with `422 trace_not_found` (nothing stored), or rejected with 503 (nothing stored, alarm when Olive's own credential or registration failed). Recording decorators prove the session key reaches only popeye.

Out of this phase: live hometown/popeye transports (LIVE-01/02), wire-level capture (LIVE-03), policy checks and `not_in_catalog` as a stored refusal (Phase 4), queueing (Phase 5), executor/JSONL (Phase 6), introspection cache (v2).

</domain>

<decisions>
## Implementation Decisions

Ratified constraints are not repeated here; they are listed in `03-RESEARCH.md` "Locked Decisions" (D§17.3 check order and reason codes, D§16.4 positive-only cache, D§17.1 token rules, D§15.2 replay/409/400 never reach the resolver, Phase 2 D-08 `proposal_hash`, Q19, Q17).

### Refusal response contract
- **D-01:** A newly stored trace refusal returns **201 Created** with `Location`, same as any new proposal. Liam's S-OL-1 stub expects `200` for refusals (and `202` for acceptance); record the mismatch for the Phase 9 COMPATIBILITY.md / §3 compatibility review. Do not change status codes in Phase 3.
- **D-02:** Refusal body is `{proposal_id, state: "refused_policy", refusal_reason: "<trace_* code>"}`. Keep the existing `state` field name (no rename to S-OL-1's `status`/`reason`; v1 changes stay additive). `refusal_reason` is omitted when null, so accepted-proposal bodies remain exactly `{proposal_id, state}` (API_01 asserts the exact property set).
- **D-03:** Replaying the identical body for a refused run returns **200** with the same refusal body. No re-verification on replay; the refusal is final for that `(agent_id, run_id)`.
- **D-04:** `GET /v1/proposals/{id}` additionally exposes `refusal_reason`, `trace_verification` (`fixture`|`hometown`) and `trace_schema_version`, all optional and additive (omitted when null).
- **D-05:** The stored-refusal path (insert then CAS to `refused_policy` with reason, events, one tenant transaction) is built to be reused by Phase 4 policy refusals.

### Fail-closed signalling
- **D-06:** Every trace-verification 503 carries a single public reason **`trace_verification_unavailable`**. The cause (outage, timeout, caller rejected, token mismatch, ...) is never exposed to the caller; it goes to alarms/logs only.
- **D-07:** Operational alarms fire **only for Olive's own credential/registration failures**: hometown 401/403, popeye 401/403 on introspection or issuance, popeye refusing the token scope, token claims disagreeing with introspection, token expiry outliving the session key, malformed resolver response. Plain unavailability and timeouts log at Warning and do not alarm. Each cause has its own stable alarm code (research Open Question 3 list is the starting set; hometown 401 and 403 keep separate codes).
- **D-08:** Alarm mechanism: an **`IOperationalAlarm` port in `Olive.Application`**; Host implementation writes a structured **Critical** log event carrying the stable alarm code and non-secret identifiers only; tests inject a recording alarm and assert on the code. No health/readiness degradation in this phase. `Olive.Application` stays logging-free.
- **D-09:** A trace 503 problem body includes **`retryable: true`** and the response sets a short **`Retry-After`** header (about 5 s). Nothing was stored, so retrying under the same `run_id` is safe.

### Popeye token stub
- **D-10:** Olive defines the token-issuance port itself because no popeye spec covers issuance: `ITokenIssuer.IssueAsync(SessionKey)` returns `{token, tenant_id, delegated_agent_id, key_id, expires_at}` or a typed refusal. Mark it **[ASSUMED]**, document it in `contracts/trace-resolution/README.md` as an Olive proposal, and add an open coordination item for the popeye owner (must be confirmed before LIVE-02). Also flag in that hand-off that Olive presents the session key to issuance (D§17.1), which POPEYE §2.2 "no onward use" has not explicitly confirmed.
- **D-11:** Always obtain the (cached) service token **before** consulting the trace cache or resolver. Check 4 always compares against the token's `delegated_agent_id`.
- **D-12:** Defaults: resolver timeout **5 s**, overridable via optional setting `OLIVE_TRACE_TIMEOUT_MS` (tests pass small values); token refreshed **30 s** before the earlier of token expiry and session-key expiry; trace cache and token cache each capped at **10,000** entries, clear-on-full. Time comes from the injected `TimeProvider`.
- **D-13:** Add a **caller-rejected outcome to `IKeyIntrospector`** in this phase: popeye 401/403 on introspection → fail-closed 503 plus alarm (`introspection_caller_rejected`), nothing stored. This is distinct from an inactive key, which stays 401.

### Existing test migration and fixtures
- **D-14:** `OliveApiFactory` defaults to a **test-only auto-fixture mode**: a test resolver that registers a matching fixture for each submission (hash computed with the production `Jcs.ProposalHash`), so the ~45 existing integration tests keep their meaning. Phase 3 tests opt into explicit fixtures. The auto-fixture resolver lives in the test project only, never in `src/`.
- **D-15:** Committed fixtures live in **`contracts/trace-resolution/`**: a response schema (`additionalProperties: false`) with paired valid/invalid examples, fixtures shaped like hometown's ATE trace-resolution response, each shipped with the proposal it binds (`<case>.json` + `<case>.proposal.json`), plus a README. A test asserts `fixture.proposal_hash == Jcs.ProposalHash(proposal)` for every pair; the pairs are offered to hometown as extra cross-component vectors alongside Q20.
- **D-16:** Fault cases (unavailable, hang/timeout, 401, 403, malformed, refused scope, mismatched claims, over-long expiry) are produced by **faulting test doubles** (`FaultingTraceResolver`, `FaultingTokenIssuer`, faulting introspector). The production stub stays fixture-only with no fault toggles.
- **D-17:** The fixture resolver does **not** tenant-scope; it returns the fixture verbatim so check 3 (`trace_tenant_mismatch`) is exercisable. README states that live hometown 404s cross-tenant lookups, so check 3 should never fire against live hometown.

### Claude's Discretion
Taken from research recommended defaults; planner may refine without asking:
- Port placement: `ITraceResolver`, `ITokenIssuer`, `IOperationalAlarm`, `[OutboundPort]` in `Olive.Application`; stubs in `Olive.Integrations.Hometown` / `.Popeye` (FND-01 dependency direction).
- Persistence: forward-only migration `0006_trace_verification.sql` (0001-0005 exist and are checksum-pinned) adding `trace_verification`, `trace_schema_version`, `refusal_reason` with checks and column grants; refusal stored via insert-then-CAS (keep `state` out of the INSERT grant). Update `PrivilegeMatrixTests` and `MigrationRunnerTests`.
- TRC-09 reading: "every proposal that reaches trace verification"; columns stay nullable for earlier refusals and legacy rows.
- Non-agent key 403 vs POPEYE §2.1 `active:false`: keep current stub behaviour, document for Phase 9 COMPATIBILITY.md; no Phase 3 code change.
- `trace_schema_version` stored verbatim, validated loosely (`^ate/[0-9]+\.[0-9]+$`).
- `trace_ref` used verbatim as hometown `trace_id`.
- Grant/token-holding types are sealed classes (not records) with redacting `ToString` (FND-06).
- IDN-08 containment evidence: `[OutboundPort(Popeye|Hometown)]` attribute + reflection architecture test, recording decorators on all outbound ports, sentinel scan over responses, Trace-level logs and stored rows (JSONL half waits for Phase 6).
- No new NuGet packages; hand-written `ManualTimeProvider` for tests.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Governing decision and requirements
- `specs/DECISION_olive-poc2-phase1_v0.5.md` — §4 stop rule, §7 principals, §15.2 idempotency ordering, §16 (trace resolution, as amended), §17.1 service tokens, §17.2 proposal_hash, §17.3 check order and reason codes
- `.planning/REQUIREMENTS.md` — TRC-01..TRC-09, IDN-05..IDN-08
- `.planning/ROADMAP.md` — Phase 3 success criteria
- `.planning/phases/03-trace-verification/03-RESEARCH.md` — locked-decision digest, architecture, pitfalls, validation map, suggested plan decomposition

### Upstream specs (outside repo)
- `~/fiskroad/specs/POPEYE_v0.4.2.md` — introspection response (§2.1), caching and no-onward-use (§2.2), registered callers (§2.3); D§4 stop rule
- `~/fiskroad/specs/POPEYE_v0.4.3.md` — `key_id` lowercase canonical UUID amendment
- `~/hometown/specs/HOMETOWN_v0.4.1.md` — §2.5 trace-resolution response shape, 401/403/404 semantics, `schema_version`
- `~/liam/specs/LIAM_DESIGN_SPEC_v0.4.1.md` §7.2 — S-OL-1 stub contract (200 refusal / 202 acceptance) for the Phase 9 compatibility record

### Prior phase context
- `.planning/phases/01-foundation-and-walking-skeleton/01-CONTEXT.md` — D-12 AgentKeyHeader, D-13 introspection stub fixtures, D-14 no introspection cache
- `.planning/phases/01-foundation-and-walking-skeleton/01-03-SUMMARY.md` — POPEYE §2.1 vs D§7 non-agent key divergence
- `.planning/phases/02-contract-integrity-and-idempotent-submission/02-CONTEXT.md` — D-06..D-08 hashes, D-13 current-release lookup
- `contracts/jcs/v1/README.md` — Q20 golden vectors hand-off to hometown

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Jcs.ProposalHash(root)` (`src/Olive.Domain/Jcs/Jcs.cs`): reference hash for check 5 and for fixture/proposal pair tests; hoist its call before the trace step in `PostAsync`.
- `StubKeyIntrospector` + `contracts/introspection/` fixtures (`src/Olive.Integrations.Popeye`): source of tenant/agent for the stub token issuer; already enforces lowercase-UUID `key_id`.
- `AgentKeyHeader` (sole owner of the session-key header) and `SessionKey` type.
- `INonProductionOnly` + startup guards (`src/Olive.Host/Startup/StartupGuards.cs`): bar fixture resolver and stub issuer in production.
- `ProblemReasons` / `ProblemResults`: add `trace_not_found` (422, retryable) and `trace_verification_unavailable` (503, retryable, Retry-After).
- `OliveApiFactory` (`configureServices`, `extraConfig`) in tests: hook for auto-fixture mode and faulting doubles.

### Established Patterns
- Ports in `Olive.Application`, implementations in `Olive.Integrations.*`; Host wires DI.
- Typed result hierarchies for expected outcomes (no exceptions for not-found/refusal).
- Value-free logging: tenant/agent/proposal ids and hash prefixes only.
- Forward-only, checksum-pinned SQL migrations with column-level grants; privilege matrix tests pin every grant.
- Requirement IDs in test names (`TRC_0x_...`, `IDN_0x_...`).

### Integration Points
- `src/Olive.Host/Endpoints/ProposalEndpoints.cs` `PostAsync`: new step after schema/catalog validation and before persist; maps verdicts to 201 verified / 201 refused / 422 / 503.
- `IProposalStore` / `PostgresProposalStore`: new `InsertRefusedAsync`; `NewProposal` gains trace fields; `FindByRunAsync` replay must return the refusal reason.
- GET proposal endpoint: optional trace/refusal fields.

</code_context>

<specifics>
## Specific Ideas

- Fixture/proposal pairs in `contracts/trace-resolution/` double as cross-component vectors for hometown, extending the Q20 hand-off.
- Coordination items to record in STATE.md: popeye owner review of the Olive-proposed `ITokenIssuer` shape and session-key presentation to issuance; Liam/S-OL-1 status-code mismatch (201/200 vs 200/202) for the §3 compatibility review.

</specifics>

<deferred>
## Deferred Ideas

- Health/readiness endpoint reporting degraded after an alarm — considered, not chosen for Phase 3; revisit with operational hardening.
- Aligning response status codes/field names with Liam S-OL-1 — Phase 9 compatibility review, not a Phase 3 change.

</deferred>

---

*Phase: 03-trace-verification*
*Context gathered: 2026-10-22*
