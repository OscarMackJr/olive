---
phase: 07-reviewer-ui-and-entra-oidc
verified: 2026-10-24T00:00:00Z
status: human_needed
score: 5/5 roadmap criteria verified (2 with warnings)
overrides_applied: 0
human_verification:
  - test: "Live Entra sign-in, prompt=login/max_age=0 with auth_time, removed role assignment, real-browser CSP, local https __Host cookie"
    expected: "See 07-HUMAN-UAT.md items 0-4"
    why_human: "Needs a real Entra tenant and browser; external dependency, non-blocking per UAT file"
  - test: "Decide whether CR-01 (claimed items absent from /review) must be fixed before phase close"
    expected: "Product decision: reviewer can find their own claimed items from the list"
    why_human: "UI-SPEC and ROADMAP wording differ; developer decision"
gaps: []
warnings:
  - id: CR-01
    truth: "Criterion 2 usability: claimed items disappear from the list"
    detail: "Index.cshtml.cs:19 calls ListAsync(..., null, \"open\", ...). Test ReviewerPagesListTests.UI_02_claimed_items_are_absent_from_the_open_list locks this behavior in, so it is deliberate against the roadmap wording ('open items') but contradicts UI-SPEC (open and claimed badges)."
  - id: WR-02
    truth: "Criterion 1 production path"
    detail: "Program.cs registers DevSignInGate/Fixture source unconditionally, so a production boot fails FND-05 before UI-01 checks run."
  - id: WR-05
    truth: "Criterion 4 completeness"
    detail: "Decision/decider/rationale rendered only from Record.Chain for approved items with an execution row."
  - id: WR-01
    detail: "Dev picker is an unauthenticated sign-in path in any non-production deployment, even with Entra configured; stub sessions skip freshness."
---

# Phase 7: Reviewer UI and Entra OIDC Verification Report

**Phase Goal:** A reviewer signs in through OIDC and works the queue in a deliberately minimal web UI. The approver role is verified at the moment of decision.
**Status:** human_needed (automated checks pass; warnings and live-Entra UAT remain)
**Re-verification:** No

## Observable Truths (ROADMAP contract)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Sign-in via OpenIdConnect handler or DevReviewer IdP (refused in prod); tenant only from server-side group map | VERIFIED (warning WR-01/02/03) | `ReviewerOidc.cs`, `EntraClaimsMapper.cs`, `Dev/SignIn`, `StartupGuards` UI-01/FND-05 checks; `ReviewerOidcTests`, `StartupGuardTests`. Production refusal of the dev gate works (FND-05 rejects it). The shipped composition cannot boot in production at all, but that is already consistent with Phase 1 simulation-only (stub introspector and fixtures are refused too). The picker remains reachable unauthenticated outside production even when Entra is set (WR-01). |
| 2 | Signed-in reviewer sees tenant's open items, claims/releases, approves/rejects with required rationale; antiforgery and PRG | VERIFIED (warning CR-01) | `Index.cshtml.cs` lists open items for the reviewer's tenant; `Item.cshtml` has Claim/Release/Decide forms; `ReviewerPagesPostTests`, `ReviewerPagesDecideTests` cover PRG, rationale, self-approval refusal. CR-01 confirmed: after claiming, the item leaves `/review` for everyone, including the claimant. Claim, release and decide still work via the PRG redirect to the item URL, so the criterion text ("open items") is met, but practical queue work is impaired and the UI-SPEC "claimed" badge is dead UI. Recommend fixing. |
| 3 | Reviewer whose role:case_supervisor was removed after claiming is refused at decision time | VERIFIED | `DecisionFreshness.cs`, `Item.cshtml.cs` role check on POST, `role_not_held` reason in `ProblemReasons.cs`; `DecisionTimeRoleTests`, `OidcReauthTests`. Note WR-06: for Entra, roles come from cookie claims bounded by the 5-minute `auth_time` window, not a fresh directory lookup (comments overstate this). |
| 4 | Audit page: item history, four hashed fields, body_sha256, proposal_hash, fixture-trace banner; no dashboards/bulk/SLA/assignment | VERIFIED (warning WR-05) | `Audit.cshtml` lines 13-15 (fixture banner), 58-61 (body_sha256, proposal_hash), 65-69 (recompute check), 109 History; `SentinelUiTests` assert UI-06 surface. Decision/decider/rationale are shown only for approved items with an execution chain; rejected items do not show who decided or why (WR-05). |
| 5 | CI test runs real OIDC handler against mock-oauth2-server, every credential type against every route group | VERIFIED | `MockOidcFixture.cs`, `ReviewerOidcTests.cs`, `CredentialRouteMatrixTests.cs` exist; orchestrator reports full suite 1393 passed, 0 failed. |

**Score:** 5/5

## Requirements Coverage

Plan frontmatter IDs: 07-01 [UI-01, UI-02]; 07-02 [UI-03, UI-04, APR-03, UI-05]; 07-03 [UI-05]; 07-04 [UI-01]; 07-05 [APR-03, UI-04]; 07-06 [UI-06, UI-07, UI-01]. Union = UI-01..UI-07 and APR-03; all eight appear in REQUIREMENTS.md and in the phase mapping. No orphans.

| Req | Status | Evidence |
|-----|--------|----------|
| UI-01 | SATISFIED (warning WR-01/02) | OIDC handler + DevReviewer, startup guards |
| UI-02 | SATISFIED (warning CR-01) | Index page, tenant-filtered via QueueService |
| UI-03 | SATISFIED | Claim/Release forms, antiforgery, PRG tests |
| UI-04 | SATISFIED | Decide with required rationale |
| UI-05 | SATISFIED (warning WR-05) | Audit page hashes and fixture banner |
| UI-06 | SATISFIED | Sentinel UI tests; no dashboards or bulk actions |
| UI-07 | SATISFIED | Mock OIDC server matrix test |
| APR-03 | SATISFIED | Role checked at decision time (WR-06 caveat on comments) |

REQUIREMENTS.md checkboxes and traceability table still show Pending for all eight; the orchestrator should tick them on phase close.

## Anti-Patterns

No TBD/FIXME blockers examined beyond the review findings. Review findings WR-03, WR-04, WR-07 and IN-01..IN-09 are non-blocking hardening items (partial OIDC config ignored silently, audit bare 404, Entra cookie with no Entra scheme gives 500, and so on).

## Human Verification Required

1. Live Entra checks in `07-HUMAN-UAT.md` (items 0-4): non-blocking, external dependency.
2. Developer decision on CR-01 (see below).

## Gaps Summary

No roadmap criterion is outright failed. The verdict is human_needed rather than passed because of the pending live-Entra UAT and the CR-01 decision. My recommendation: fix CR-01 (list open and claimed items) and WR-01 (do not register the dev picker when OIDC is configured) before closing the phase; WR-05 and WR-02 can be tracked as follow-up. If CR-01 is judged to defeat "works the queue", reclassify to gaps_found.

---

_Verified: 2026-10-24_
_Verifier: Claude (gsd-verifier)_
