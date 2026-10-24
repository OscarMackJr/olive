# Phase 7: Reviewer UI and Entra OIDC - Context

**Gathered:** 2026-10-24
**Status:** Ready for planning

<domain>
## Phase Boundary

A reviewer signs in through OIDC (Entra in production, a dev fixture picker locally and in CI) and works the queue in a deliberately minimal Razor Pages UI. The UI has list, item (claim, release, decide) and audit pages. The approver role `role:case_supervisor` is verified at the moment of decision (APR-03). A CI test runs the real OpenIdConnect handler against `mock-oauth2-server` and tries every credential type against every route group (UI-07).

Requirements: UI-01..UI-07, APR-03.

Not in this phase: dashboards, bulk actions, SLA views, assignment rules (UI-06), Teams approval (D§11), any new production API surface, live Entra approvals (blocked until the `role:case_supervisor` group is named, which is an external dependency and does not block phase completion).

</domain>

<decisions>
## Implementation Decisions

Carried forward, not re-decided:
- Q13 / Phase 5 D-08: the reviewer's tenant comes only from the source-controlled map `config/reviewer-tenants.yaml`, one tenant per session, never from a header, form or query.
- Phase 5 D-01: the UI calls the existing `QueueService`. It has no logic path of its own.
- Phase 5 D-04 reason codes, D-05/D-19 role-gated payload, D-09 decide-time `required_role` gate in the kind registry, D-18 (self-approval identity check on approve only), D-21 (catalog `required_approval` = kind `required_role`).
- Phase 5 D-06 / Q14: self-approval is decided by oid+tid equality through `IUserIdentityResolver`, failing closed.
- Phase 6 D-01: an approve triggers simulated execution after commit, in the same request. Q18: the audit chain is shown through the `execution_chain` view and the UI audit page, with no new API endpoint.
- Q15 is resolved below (D-05..D-10). Graph is never called.

### Sign-in and session model
- **D-01:** **Cookie session with an OIDC challenge.** A dedicated reviewer cookie scheme holds the UI session. The OpenIdConnect handler signs in to it. At sign-in (`OnTokenValidated`), Entra claims are mapped to the existing `ReviewerClaims` shape: oid, tid, groups, the role, `auth_source = entra`, and the tenant from the Q13 map. `QueueService` then sees exactly what `DevReviewerAuthenticationHandler` emits today. Tokens are never stored (no `SaveTokens`), and nothing is re-validated per request beyond the cookie.
- **D-02:** **Dev sign-in is a fixture picker page.** `/dev/sign-in` lists the fixture reviewers from `IReviewerIdentitySource` and issues the **same** reviewer cookie with `auth_source = stub`. It is `INonProductionOnly` and barred in production by the existing startup guards (FND-05, Q17). The existing `X-Olive-Dev-Reviewer` header scheme stays for the JSON API (see D-15).
- **D-03:** **Session lifetime:** about 30 min idle with sliding expiry, and an absolute cap of about 8 h (the proposal expiry default). An explicit sign-out clears the cookie and, for Entra, also signs out of the IdP session.
- **D-04:** **Zero or several tenants:** authentication succeeds, but every queue page returns a plain 403 page ("no reviewer tenant for your account") with sign-out, using a static reason. This mirrors the API's 403. A cookie is still issued, so the misconfiguration is visible to the user.

### Role freshness at decision time (APR-03, Q15)
- **D-05:** **`max_age` re-challenge on decide.** When the approve or reject form is submitted, if the session's `auth_time` is older than the freshness window, the UI redirects to the IdP with `max_age` (or `prompt=login`), takes the fresh role claims from the returned token, and only then completes the decision. `auth_time` in the fresh token is the evidence. **The draft rationale and the chosen decision must survive the round-trip** (for example, held server-side or in protected state keyed to the item), and must never be logged or put in the redirect URL in clear text.
- **D-06:** **The freshness window is 5 minutes.** It is configurable downward. A code-level maximum stops configuration from widening it.
- **D-07:** **The role comes from the Entra app role (`roles` claim).** An app role on the Olive registration is assigned to the case_supervisor group and mapped to the kind registry's `required_role` value (`role:case_supervisor`). The tenant still comes from the `groups` claim through the Q13 map.
- **D-08:** **Groups overage fails closed.** If the groups claim is missing because of overage (`_claim_names` / `hasgroups` present), the session is treated as having zero tenants and gets the D-04 403 page with the static reason `groups_overage`. There is no Graph call. Operators fix this by filtering the token's groups to those assigned to the app.
- **D-09:** **A role missing at decide** gives a 403 page with the static reason `role_not_held`. The item **stays claimed** by that reviewer, who can then release it (Q7, claimant-only release, is unchanged). No `queue_item_event` is written for a refused attempt, consistent with the Phase 5 API.
- **D-10:** **The dev/stub path re-reads the reviewer fixture at decide**, rather than trusting cookie claims. Tests can then remove the role from a fixture after the claim and observe the refusal (Success Criterion 3) without an IdP. The mock-oauth2-server test proves the real `max_age` path (D-17).

### Pages and content
- **D-11:** **Three page types:**
  - `/review`: open items for the session's tenant, oldest first, with IR-1 fields only (Phase 5 D-14).
  - `/review/items/{id}`: payload, claim and release buttons, and a decide form (approve/reject plus a required rationale) shown only to the claimant.
  - `/review/items/{id}/audit`: history, hashes and the execution chain.
  All three call `QueueService`. Every POST uses antiforgery and post-redirect-get (UI-03).
- **D-12:** **The payload is shown as rendered, HTML-encoded fields:** action_type, on_behalf_of, expires_at with time remaining, trace_ref, justification, and parameters as a key/value table. Only holders of the kind's `required_role` can see it (Phase 5 D-19). Others see only the item refs. A banner appears when `trace_verification = fixture` (UI-05).
- **D-13:** **The audit page shows the hashes and recomputes them.** It shows the four hashed fields (action_type, party_ref, parameters, justification), the stored `body_sha256` and `proposal_hash`, and a server-side "recomputed proposal_hash matches" indicator using the existing `Olive.Domain.Jcs`. It also shows the `queue_item_event` history, seq-ordered.
- **D-14:** **For decided action items, the audit page shows the `execution_chain` row:** the policy decision, approver (`decided_by`), rationale, `auth_source`, `decided_at`, and the started/finished ledger rows (`execution_mode = simulated`, outcome, reason, times). It is read through the tenant unit of work. It adds no new API endpoint and does not show parameters beyond D-12.
- **D-19 (errors):** **Conflicts use PRG with a one-time static flash message** keyed on the Phase 5 reason codes (`proposal_expired`, `already_claimed`, `not_claimant`, `decision_conflict`, `role_not_held`, `approval_identity_unresolved`, …). Flash messages never echo the rationale or parameters. A missing or cross-tenant item gives 404, which can't be told apart.
- **D-20 (styling):** **Plain semantic HTML and one small static stylesheet.** No JS framework, no CDN, no Bootstrap. A CSP with `default-src 'self'`. Razor encoding everywhere, no `Html.Raw` on proposal data.

### Credential × route matrix and config
- **D-15:** **The JSON reviewer API (`/v1/queue/items/*`) keeps the DevReviewer header only.** The UI cookie is refused on every `/v1/*` route, so antiforgery and CSRF concerns never reach the API. The Razor pages call `QueueService` in-process, not the JSON API. In production, the reviewer JSON API has no accepted scheme (401/403). No JwtBearer is added.
- **D-16:** **The UI-07 matrix uses five route groups:**
  1. `/v1/proposals` (agent)
  2. `/v1/catalog`
  3. `/v1/queue/items` (reviewer JSON)
  4. `/review/*` (UI pages)
  5. `/dev/sign-in` (non-production only)

  It tests six credential types: none, agent key, DevReviewer header, dev cookie, OIDC cookie from mock-oauth2-server, and a forged or expired cookie. Expected outcomes are a table-driven fixture. Only the intended pairings are admitted. An agent key on reviewer routes still gets `403 principal_not_permitted` (Phase 5 D-02).
- **D-17:** **mock-oauth2-server runs via Testcontainers** (`ghcr.io/navikt/mock-oauth2-server`, pinned by digest). The test drives the authorize → callback flow through the real OpenIdConnect handler. Per-request claim config supplies `roles`, `groups`, `auth_time` and the overage marker. This covers: role present, role removed after claim (refused at decide), stale `auth_time` (re-challenge), overage (403), and a tenant mapping to zero or several tenants. The host runs with `OLIVE_ENVIRONMENT=ci`. Any HTTP client use stays in the test project.
- **D-18:** **Production OIDC config comes from environment variables** (and `.env` locally): `OLIVE_OIDC_AUTHORITY`, `OLIVE_OIDC_CLIENT_ID`, and a client secret or certificate. In production, a startup guard fails if any is missing, if the authority isn't `https`, or if DevReviewer or the dev sign-in page is registered. The client secret is a sealed, redacting type like the session key. It is a read/sign-in credential, not a write credential, but it must be listed in the Phase 9 credential audit (EVD-06).

### Claude's Discretion
- Cookie hardening details: name, `SameSite` (Lax is likely needed for the OIDC form_post/redirect), `Secure`/HTTPS in local dev (dev certs vs a ci/local exception), and the data-protection key location.
- How the rationale draft survives the `max_age` round-trip (D-05): a server-side short-TTL store, or data-protected state. It must not be logged or put in a URL.
- Claim-mapping code location (for example, an `EntraClaimsMapper` in `Olive.Integrations.Entra`, with `ReviewerClaims` staying the single claims contract), and how `OnTokenValidated` uses `IReviewerTenantMap`.
- How the decide-time freshness check is exposed to `QueueService` without leaking HTTP concepts into Application, for example a "fresh role" input on `ReviewerContext`, or a port that the stub implements by re-reading the fixture (D-10).
- Which oid/tid claim types Entra uses (`oid`/`tid` vs the long URIs) and how inbound claim mapping is disabled.
- Exact page routes, the flash mechanism (TempData vs a cookie), and the `ProblemReasons` additions (`role_not_held`, `groups_overage`, `no_reviewer_tenant`).
- Whether Razor Pages live in `Olive.Host` or a separate UI project, within the FND-01 dependency graph and package allowlist (`Microsoft.AspNetCore.Authentication.OpenIdConnect` must be added to the allowlist and lockfiles).
- Research topics already flagged in ROADMAP: `max_age` behaviour on Entra, group overage, oid mapping, SameSite and HTTPS.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Ratified decisions
- `specs/DECISION_olive-poc2-phase1_v0.5.md` §8 (Approvers: Entra OIDC, role checked at decision time, no self-approval, rationale required) and §11 (Queue frontend: minimal web UI, no Teams approval)
- `specs/OLIVE_CHARTER_v0.4.md` §4: deliberately minimal queue; REQ-QUE-03 (decided_by and rationale)
- `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`: IR-1 item model the UI renders

### Planning
- `.planning/REQUIREMENTS.md`: UI-01..07, APR-03; rulings Q13, Q15, Q17, Q18
- `.planning/ROADMAP.md` Phase 7: success criteria and research flags
- `.planning/phases/05-generic-review-queue-and-action-approval/05-CONTEXT.md`: D-01..D-21 (reviewer surface, DevReviewer, tenant map, role gate, reason codes)
- `.planning/phases/06-simulated-execution-and-audit-chain/06-CONTEXT.md`: D-01 (execute after approve), D-14 (chain view columns)

### Config and contracts
- `config/reviewer-tenants.yaml`: Q13 group → tenant map
- `config/queue-kinds.yaml`: kind registry with `required_role`
- `sql/migrations/0009_execution_ledger.sql`: `execution_chain` view (security_invoker)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/Olive.Host/Auth/DevReviewerAuthenticationHandler.cs`: the claim emission the OIDC and dev-cookie paths must match. Its `HandleChallengeAsync` gives agent-key → 403 caller separation.
- `src/Olive.Host/Auth/ReviewerClaims.cs`: `ToPrincipal` / `ToReviewerContext`. This is the single claims contract; reuse it for the cookie and OIDC paths.
- `src/Olive.Application/QueueService.cs`: list, claim, release, decide, history and payload. The Razor pages call it directly.
- `src/Olive.Application/IReviewerIdentitySource.cs` with `src/Olive.Integrations.Entra/FixtureReviewerIdentitySource.cs`: fixture reviewers for the picker page and the D-10 re-read.
- `src/Olive.Application/IReviewerTenantMap.cs`: the Q13 map, used in `OnTokenValidated`.
- `src/Olive.Domain/Jcs/Jcs.cs`: recomputes `proposal_hash` on the audit page.
- `src/Olive.Contracts/ProblemReasons.cs`: static reason codes for flash messages and 403 pages.
- `src/Olive.Host/Startup/StartupGuards.cs` and `INonProductionOnly`: extend them for the dev sign-in page and the OIDC config guard.

### Established Patterns
- Authentication schemes are registered in `src/Olive.Host/Program.cs` (AgentKey default, DevReviewer). Add the cookie and OpenIdConnect schemes, and route-group-specific policies so cookies never authenticate `/v1/*`.
- Static, never-echoing reason codes. No rationale, parameters or tokens in logs (sentinel scan, IDN-08). Extend the sentinel scan to UI HTML responses and logs for the rationale sentinel.
- Testcontainers fixtures already exist for Postgres. Add mock-oauth2-server the same way.
- Package allowlist and BannedApiAnalyzers (FND-02) apply. New packages need allowlist and lockfile updates.

### Integration Points
- `Program.cs`: `AddRazorPages`, antiforgery, the cookie and OIDC schemes, CSP headers, `MapRazorPages`.
- The decide flow calls `QueueService.DecideAsync`, which then triggers execution after commit (Phase 6). The UI must not bypass that path.
- The audit page reads `execution_chain` through the tenant unit of work, which may need a small read method on a store.

</code_context>

<specifics>
## Specific Ideas

- Evidence for Success Criterion 3 runs through both paths: the stub (fixture re-read after the claim) and mock-oauth2-server (the role claim removed, plus a stale `auth_time` forcing a re-challenge).
- A visible fixture-trace banner on item and audit pages, matching the beat 2 transcript's plain statement that trace verification used fixtures.

</specifics>

<deferred>
## Deferred Ideas

- A production reviewer JSON API (Entra JwtBearer on `/v1/queue/items`): no Phase 1 consumer. Revisit if bluto or another client needs it.
- Teams notifications linking to the UI: P2-04.
- Auto-release on a role-missing refusal: rejected for now. It would extend Q7.

</deferred>

---

*Phase: 07-reviewer-ui-and-entra-oidc*
*Context gathered: 2026-10-24*
