---
gsd_state_version: 1.0
milestone: v0.1
milestone_name: milestone
status: executing
last_updated: "2026-10-23T01:31:47.481Z"
last_activity: 2026-10-23 -- Phase 4 planning complete
progress:
  total_phases: 9
  completed_phases: 3
  total_plans: 33
  completed_plans: 26
  percent: 33
stopped_at: Phase 3 complete (6/6) — ready to discuss Phase 4
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-21)

**Core value:** No action reaches a target system unless it traces to a verified hometown answer, passed policy before any human saw it, was approved by a named reviewer with a rationale, and runs exactly once. The chain must be provable from the execution record.
**Current focus:** Phase 4 — policy, expiry and refusal

## Current Position

Phase: 4
Plan: Not started
Status: Ready to execute
Last activity: 2026-10-23 -- Phase 4 planning complete

Progress: [░░░░░░░░░░] 0%

## Performance Metrics

**Velocity:**

- Total plans completed: 26
- Average duration: -
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01 | 13 | - | - |
| 02 | 7 | - | - |
| 3 | 6 | - | - |

**Recent Trend:**

- Last 5 plans: -
- Trend: -

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in the PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [Init]: Rulings Q7 (claimant-only release), Q9 (`queue_item_event`, five tables), Q11 (expired item becomes `withdrawn`/`proposal_expired`, committed lazily) and Q13 (server-side group-to-tenant map) are settled. Working defaults Q1-Q6, Q8, Q10, Q12, Q14-Q20 are in REQUIREMENTS.md.
- [Roadmap]: 9 vertical slices. Phases 6, 7 and 8 depend only on Phase 5 and can run in parallel.
- [Roadmap]: DB-03 and DB-04 (all five tables) are assigned to Phase 1, which sets the RLS/grant pattern. Migrations 0002 (Phase 5) and 0003 (Phase 6) must follow it and extend the privilege-matrix test.
- [Roadmap]: API-08 (asserted pipeline order) is assigned to Phase 4, where steps 1-6 are all real. APR-03 (decision-time role check) is assigned to Phase 7, with the real IdP.

### Pending Todos

None yet.

### Blockers/Concerns

- D§4 stop rule. Confirm that `~/fiskroad/specs/` (POPEYE_v0.4.2, NONO_v0.4.x, CROSS_COMPONENT_EXECUTION_SPEC.md, the agent-key decisions, PROGRAM_SHARED_VOCABULARY_v0.4) and `~/liam/specs/` are present before Phases 1, 3, 4 and 9.
- Q4: the entitlement claim source must exist in POPEYE_v0.4.2 or vocabulary §3.2. If it is in neither, stop (Phase 4, and the stub shape in Phase 1).
- Environment: the Postgres major version of `deploy-postgres-1` (15+ needed); Docker Desktop in Linux mode with WSL2 and Ryuk; Docker licence coverage.
- External: the `role:case_supervisor` Entra group is unnamed, so live approvals stay blocked. This does not block Phase 7 completion.
- Research flags: `--research-phase` is recommended for Phases 3, 5, 6 and 7.

#### Open coordination items

- Agent-key header (D-12): confirm the header name with the nono owner; currently assumed `Authorization: Bearer`, isolated in `AgentKeyHeader`.
- Q14 conflict: NONO_v0.4.3 section 5 says on_behalf_of is a UPN, while the Q14 working default maps by Entra oid+tid and never UPN. Needs a ruling before Phase 5 and before Phase 7. Fixtures use UPN-style placeholders.
- Q4 narrowed: POPEYE_v0.4.2 section 2.5 says introspection returns no entitlements and Olive resolves on_behalf_of to directory groups without a write-capable credential (REQ-ACT-04). Decide in Phase 4.
- PHASE 3 RESEARCH: POPEYE section 2.1 returns active:false for non-agent keys, while D§7 requires 403 principal_not_permitted for user/application. Phase 1 stub fixtures return active:true with those principal types.
- Introspection cache constraint (D-14, POPEYE section 2.2): positives cached at most 60 s and never past expires_at; negatives never cached. No cache in Phase 1.
- DB-03/DB-04 name five tables but migration 0001 creates two (D-03). Phases 5 and 6 must append table names to the table-driven RLS and privilege tests.
- PHASE 3 popeye issuance review [ASSUMED]: the popeye owner must review the Olive-proposed `ITokenIssuer` shape (IssueAsync(SessionKey) returning token, tenant_id, delegated_agent_id, key_id, expires_at, or a typed refusal) and confirm that Olive presenting the session key to issuance (D17.1) is compatible with POPEYE section 2.2 "no onward use", before LIVE-02.
- PHASE 3 Liam S-OL-1 status-code mismatch: the Liam stub expects 200 for refusals and 202 for acceptance, while Olive v1 returns 201 for both and 200 for replay, with `state`/`refusal_reason` instead of `status`/`reason`. Record for the Phase 9 COMPATIBILITY.md and section 3 review; no Phase 3 change (D-01, D-02).
- PHASE 3 hometown fixture pairs: offer the eight `contracts/trace-resolution/*.proposal.json` fixture/proposal pairs to the hometown owner as extra cross-component hash vectors alongside the Q20 golden set.
- PHASE 3 non-agent key 403 vs POPEYE section 2.1 active:false divergence goes to Phase 9 COMPATIBILITY.md unchanged.
- Migration numbering: migrations are numbered sequentially in order of arrival; the 0002/0003 labels in CONTEXT are nominal (Q3 resolved).

## Deferred Items

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-10-23T00:22:38.606Z
Stopped at: Phase 4 context gathered
Resume file: .planning/phases/04-policy-expiry-and-refusal/04-CONTEXT.md
