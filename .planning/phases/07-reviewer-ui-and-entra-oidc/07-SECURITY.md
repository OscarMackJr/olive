---
phase: 07
slug: reviewer-ui-and-entra-oidc
status: verified
threats_open: 0
asvs_level: 1
block_on: high
created: 2026-10-24
audited_against: main @ 432fb79 (includes CR-01 fix 06cf906 and WR-01 fix 33efa88)
---

# Phase 7 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Source of truth: the `<threat_model>` blocks in 07-01-PLAN.md through 07-06-PLAN.md (register authored at plan time).
> Each mitigation was checked against the code on current main, not against SUMMARY or plan text.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| browser -> Razor Pages | cookie, form fields, returnUrl, item id and resume key are untrusted | session cookie, antiforgery token, decision/rationale (sensitive free text) |
| cookie -> ReviewerContext | the tenant claim comes only from the server-side map | identity claims, tenant id |
| default AgentKey scheme -> non-agent routes | agent keys are not introspected outside agent routes | agent key (credential) |
| cookie claims -> QueueService (decide) | cookie roles are not trusted at decide without freshness | roles/groups |
| Audit store -> other tenants | tenant isolation on a new read path | proposal fields, hashes, execution chain |
| IdP -> Olive callback | id_token, code, state | tokens, authorization code (secret) |
| token claims -> ReviewerContext | claims decide identity, role and tenant | oid, tid, groups, roles, auth_time |
| environment -> startup | OIDC secret and authority from env | client secret (secret) |
| nuget.org -> build | one new first-party package | package binaries |
| Item page -> IdP redirect -> DraftStore | decision draft held server-side; opaque key in URL | rationale (sensitive), 256-bit key |
| rendered pages/headers/logs -> outside world | secrets must not cross | tokens, code, secret, rationale |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation / Evidence | Status |
|-----------|----------|-----------|-------------|-----------------------|--------|
| T-07-01 | Spoofing | /dev/sign-in picker | mitigate | `DevSignInGate : INonProductionOnly` (src/Olive.Host/Auth/DevSignInGate.cs:6). Registered only when `oidc is null` (src/Olive.Host/Program.cs:130-133, WR-01 fix). The page returns 404 without the gate on GET and POST (src/Olive.Host/Pages/Dev/SignIn.cshtml.cs:248,255). The form takes only a fixture id, and identity and tenant are re-resolved server-side (SignIn.cshtml.cs:256-267). FND-05 check: src/Olive.Host/Startup/StartupGuards.cs:159-209. Tests: `DevSignInTests.UI_01_production_refuses_to_start_with_the_dev_sign_in_gate_registered`, `DevSignInTests.UI_01_dev_sign_in_is_not_reachable_when_oidc_is_configured`. | closed |
| T-07-02 | Tampering | CSRF on picker and sign-out POSTs | mitigate | No `IgnoreAntiforgery`/`DisableAntiforgery` anywhere in src (grep: 0 hits). `__Host-olive-af`, Secure, SameSite Lax (Program.cs:115-120). `app.UseAntiforgery()` (Program.cs:143). Session cookie SameSite Lax (src/Olive.Host/Auth/ReviewerCookie.cs:34). Tests: `DevSignInTests.UI_01_post_without_an_antiforgery_token_is_400_and_issues_no_cookie`, `DevSignInTests.UI_01_sign_out_without_a_token_is_400`. | closed |
| T-07-03 | Spoofing | Open redirect via returnUrl (picker) | mitigate | `Url.IsLocalUrl(ReturnUrl) ? LocalRedirect : Redirect("/review")` (Pages/Dev/SignIn.cshtml.cs:269). Test: `DevSignInTests.UI_01_return_url_is_honoured_only_when_local`. | closed |
| T-07-04 | Elevation of Privilege | Session past absolute cap via sliding renewal | mitigate | `OnValidatePrincipal = EnforceAbsoluteCap` with injected TimeProvider; a missing or malformed start claim is rejected (ReviewerCookie.cs:26,41,58-68). Tests: `ReviewerSessionTests.UI_01_absolute_cap_rejects_a_session_older_than_8_hours_even_when_sliding_kept_it_alive`, `ReviewerSessionTests.UI_01_idle_session_over_30_minutes_is_rejected`. | closed |
| T-07-05 | Information Disclosure | 403 page and flash echo input | mitigate | 403 reason is only a `ProblemReasons` constant (src/Olive.Host/Pages/Review/ReviewerPageModel.cs:273-288). `FlashMessages.Resolve` returns null for unknown codes (src/Olive.Host/Pages/Shared/FlashMessages.cs:325). Tests: `DevSignInTests.UI_01_unknown_reviewer_id_issues_no_cookie_and_does_not_echo_the_value`, `ReviewerPagesListTests.UI_01_session_without_exactly_one_tenant_gets_403_with_a_static_reason_and_a_sign_out_button`. | closed |
| T-07-06 | Elevation of Privilege | Agent key or cookie on wrong route group | mitigate | AgentKey handler returns `NoResult` outside `/v1/proposals` and `/v1/catalog` (src/Olive.Host/Auth/AgentKeyAuthenticationHandler.cs:23-28). The cookie scheme is named only in the `ReviewerUi` policy (Program.cs:104). Queue endpoints pin the DevReviewer scheme (src/Olive.Host/Endpoints/QueueEndpoints.cs:15). Tests: `ReviewerSessionTests.UI_01_default_agent_scheme_ignores_non_agent_routes_and_never_introspects`, `ReviewerPagesListTests.UI_01_root_redirects_to_review_and_v1_routes_ignore_the_reviewer_cookie`, the plan-06 matrix (T-07-32). | closed |
| T-07-07 | Information Disclosure | XSS via item data | mitigate | No `Html.Raw`/`HtmlString`/`<script` in src/Olive.Host/Pages (grep: 0 hits). CSP `default-src 'self'` with no unsafe-inline (src/Olive.Host/Startup/SecurityHeaders.cs:6). Gate test: `UiSurfaceTests.UI_06_pages_carry_no_script_inline_style_raw_html_or_third_party_url`. | closed |
| T-07-08 | Elevation of Privilege | Stale role in cookie used at decide | mitigate | The decide handler calls `freshness.ResolveAsync` and never uses cookie roles directly (src/Olive.Host/Pages/Review/Item.cshtml.cs:157-182). The stub branch re-reads the fixture (src/Olive.Host/Auth/DecisionFreshness.cs:42-51). Plan 05 replaced the Entra deny-branch with the auth_time window (see T-07-26). Tests: `DecisionFreshnessTests.APR_03_role_removed_after_cookie_issue_is_reflected_at_decide_time`, `DecisionTimeRoleTests.APR_03_stub_role_removed_after_claim_gives_403_role_not_held_and_writes_no_event`. Note: WR-06, see Audit Notes. | closed |
| T-07-09 | Tampering | CSRF on claim/release/decide | mitigate | Global antiforgery with no opt-outs (as T-07-02). Test: `ReviewerPagesPostTests.UI_03_claim_without_token_is_400_and_with_token_redirects_and_flashes_once`. | closed |
| T-07-10 | Information Disclosure | Rationale/parameters echoed in flash, logs, TempData | mitigate | TempData holds only a code (Item.cshtml.cs:211-215). Form values are read from `Request.ReadFormAsync`, not bound as arguments, so they never reach Debug argument logs (Item.cshtml.cs:150-154). Tests: `SentinelUiTests.IDN_08_ui_every_page_type_has_the_strict_csp_no_store_and_leaks_no_secret`, `OidcReauthTests.APR_03_rationale_and_tokens_never_reach_urls_html_cookies_or_logs`. | closed |
| T-07-11 | Information Disclosure | Stored XSS via justification or parameters | mitigate | Razor-encoded `@payload.Justification` and parameter rows (src/Olive.Host/Pages/Review/Item.cshtml:60). No raw-HTML sinks. CSP as T-07-07. Test: `ReviewerPagesPostTests.UI_05_role_holder_sees_encoded_payload_and_non_holder_sees_refs_only`. | closed |
| T-07-12 | Spoofing | Cross-tenant item read | mitigate | All page reads go through `QueueService` with the `ReviewerContext` tenant (Item.cshtml.cs:65,68). The same `NotFoundPage()` serves missing and cross-tenant items (Item.cshtml.cs:217-223). Test: `ReviewerPagesPostTests.UI_03_missing_and_cross_tenant_items_give_identical_404_pages`. | closed |
| T-07-13 | Repudiation | Approval without execution record; refusal writing events | mitigate | Shared `ApprovalExecution.RunAsync` (src/Olive.Host/Endpoints/ApprovalExecution.cs:10-34) is used by both the page (Item.cshtml.cs:183) and the API (QueueEndpoints.cs:150). A role refusal writes nothing (Item.cshtml.cs:201-204). Tests: `ReviewerPagesDecideTests.UI_04_approve_with_rationale_decides_and_executes_exactly_once_in_simulation`, `DecisionTimeRoleTests.APR_03_stub_role_removed_after_claim_gives_403_role_not_held_and_writes_no_event`. | closed |
| T-07-14 | Information Disclosure | Cross-tenant audit read | mitigate | `PostgresAuditStore` runs inside `TenantUnitOfWork.ExecuteAsync(tenantId, ...)` (src/Olive.Persistence/PostgresAuditStore.cs:41-50). Both statements keep `tenant_id = @tenant_id` (src/Olive.Persistence/AuditSql.cs:6-12). `execution_chain` has `security_invoker = true` (sql/migrations/0009_execution_ledger.sql:36). Test: `AuditPageTests.UI_05_cross_tenant_and_unknown_item_ids_give_404`. | closed |
| T-07-15 | Information Disclosure | Parameters/justification shown to non-role reviewers | mitigate | `CanSeeFields` is set only on `PayloadResult.Found` (src/Olive.Host/Pages/Review/Audit.cshtml.cs:42-49). Parameters are parsed only when permitted (Audit.cshtml.cs:54). The view gates the fields block (Audit.cshtml:20-54). Test: `AuditPageTests.UI_05_reviewer_without_the_role_sees_hashes_and_indicator_but_no_field_values`. | closed |
| T-07-16 | Tampering | Tampered record trusted | mitigate | Server-side recompute that fails closed (src/Olive.Application/AuditHashCheck.cs:8-31; Audit.cshtml.cs:53). The view shows "Do not rely on this record" on mismatch (Audit.cshtml:69). Tests: `AuditHashCheckTests.UI_05_any_changed_or_malformed_field_does_not_match`, `AuditPageTests.UI_05_tampered_stored_justification_reads_does_not_match`. | closed |
| T-07-17 | Information Disclosure | Event DetailJson leaks free text | mitigate | `DetailJson` does not appear in Audit.cshtml (grep: 0 hits). The events table renders only Seq, EventType, FromState, ToState, Actor and CreatedAt (Audit.cshtml:129-137). | closed |
| T-07-18 | Tampering | Audit store writes | mitigate | AuditSql has two `select` statements only (AuditSql.cs:6-12). `IAuditStore` exposes only `GetAsync`. `olive_app` has only `grant select on execution_chain` (sql/migrations/0009_execution_ledger.sql:74-75). Test: `AuditPageTests.UI_05_audit_page_adds_no_http_endpoint_and_no_write_verbs`. | closed |
| T-07-19 | Spoofing | Login CSRF / token replay | mitigate | Code flow, `UsePkce = true`, built-in state/nonce/correlation with Secure+Lax correlation and nonce cookies (src/Olive.Host/Auth/ReviewerOidc.cs:33-46). Remote and authentication failures redirect to a static page (ReviewerOidc.cs:96-107). Test: `ReviewerOidcTests.UI_01_tampered_state_redirects_to_the_static_failed_page_with_no_session`. | closed |
| T-07-20 | Elevation of Privilege | Role injection via group named role:... | mitigate | `role:`-prefixed group values are dropped (src/Olive.Integrations.Entra/EntraClaimsMapper.cs:49). Roles are mapped only through `RoleAllowlist.FromRegistry` (EntraClaimsMapper.cs:11-26,51-57; ReviewerOidc.cs:56). Tests: `EntraClaimsMapperTests.UI_01_role_prefixed_group_values_are_dropped`, `UI_01_unlisted_role_is_dropped`, `UI_01_allowlist_is_derived_from_the_registry`. | closed |
| T-07-21 | Spoofing | Tenant confusion | mitigate | Tenant comes only from `IReviewerTenantMap.TenantsFor(groupValues)` and must resolve to exactly one tenant (EntraClaimsMapper.cs:60-65). `oid`/`tid` must be GUIDs (EntraClaimsMapper.cs:39). The tid pin applies when set (EntraClaimsMapper.cs:44). Overage gives no tenant (EntraClaimsMapper.cs:59-61,107-123). Tests: `EntraClaimsMapperTests.UI_01_wrong_tid_is_refused_and_matching_tid_accepted`, `UI_01_overage_by_hasgroups_gives_no_tenant`, `UI_01_zero_or_several_tenants_give_no_reviewer_tenant`, `ReviewerOidcTests.UI_01_groups_overage_gives_403_with_groups_overage`. Note: IN-08, see Audit Notes. | closed |
| T-07-22 | Information Disclosure | Tokens, code, secret in cookie, logs or HTML | mitigate | `SaveTokens = false`, `MapInboundClaims = false`, and the principal is replaced with Olive-only claims (ReviewerOidc.cs:36-37,86-89). The secret is wrapped in `OidcClientSecret`, a redacting sealed class (src/Olive.Domain/OidcClientSecret.cs:7-28; src/Olive.Host/Startup/OidcConfig.cs:20). The failure page is static (src/Olive.Host/Pages/Account/Failed.cshtml.cs:6). The Hosting request-start log is filtered to Warning and the OIDC handler to Information (Program.cs:23,25). Tests: `ReviewerOidcTests.UI_01_tokens_code_and_secret_never_reach_cookies_html_or_logs`, `SecretTypeTests.FND_06_secret_never_renders_in_any_format`, `SentinelUiTests.IDN_08_ui_every_page_type_has_the_strict_csp_no_store_and_leaks_no_secret`. Note: IN-03, see Audit Notes. | closed |
| T-07-23 | Spoofing | Open redirect via returnUrl (/auth/sign-in) | mitigate | `Url.IsLocalUrl(returnUrl) ? returnUrl : "/review"` (src/Olive.Host/Pages/Account/SignIn.cshtml.cs:18). Test: `ReviewerOidcTests.UI_01_sign_in_page_ignores_a_non_local_return_url`. | closed |
| T-07-24 | Elevation of Privilege | Production starts with dev paths or weak OIDC config | mitigate | `CheckOidcConfiguration` requires authority, client id and secret, an absolute https authority and no `DevSignInGate`, all with static messages (StartupGuards.cs:93-124). It is wired into the startup filter (src/Olive.Host/Startup/StartupGuardFilter.cs:19). `CheckNoNonProductionComponents` (FND-05) and `CheckDevReviewerNotInProduction` (Q17) run before it (StartupGuardFilter.cs:17-18). Tests: `OidcConfigGuardTests.UI_01_missing_or_blank_setting_refuses_production`, `UI_01_non_https_authority_refuses_production_without_echoing_it`, `UI_01_registered_dev_sign_in_gate_refuses_production`, `UI_01_production_host_without_oidc_authority_refuses_with_ui_01_after_other_guards_pass`, `CredentialRouteMatrixTests.UI_07_production_refuses_dev_sign_in_gate_and_http_authority`. Note: WR-02/WR-03, see Audit Notes. | closed |
| T-07-25 | Tampering | Supply chain for the new package | mitigate | The only new package is `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.13 (Directory.Packages.props:19). It is in the Olive.Host allowlist (tests/Olive.Tests/ArchitectureTests.cs:34; `FND_02_direct_packages_match_allowlist_in_lockfiles`) and in src/Olive.Host/packages.lock.json. `RestorePackagesWithLockFile` (Directory.Build.props:7). CI runs `dotnet restore --locked-mode` (.github/workflows/ci.yml:28) and the vulnerable-package gate (ci.yml:45-48, build/vulnerable-gate.sh). | closed |
| T-07-SC | Tampering | npm/pip/cargo/nuget installs | mitigate | Directory.Packages.props adds no package beyond the audited OpenIdConnect entry. `Testcontainers` 4.16.0 is pinned (Directory.Packages.props:20). The direct-package allowlist test (`ArchitectureTests.FND_02_direct_packages_match_allowlist_in_lockfiles`) fails on any unlisted package, which enforces the human checkpoint. | closed |
| T-07-26 | Elevation of Privilege | Stale role used to approve after removal | mitigate | 5-minute cap, and a missing, zero, unparseable or future `auth_time` counts as Stale (DecisionFreshness.cs:30,33,54-68). A stale decide challenges with `prompt=login` and `max_age=0` (Item.cshtml.cs:158-171). The fresh token's mapped roles feed `DecideAsync` (Item.cshtml.cs:182). Tests: `DecisionFreshnessEntraTests.APR_03_entra_auth_time_six_minutes_old_is_stale`, `APR_03_entra_missing_auth_time_is_stale`, `APR_03_configured_thirty_minutes_clamps_to_five`, `OidcReauthTests.UI_04_APR_03_stale_session_decide_is_re_challenged_with_prompt_login_and_max_age_zero`, `OidcReauthTests.APR_03_role_removed_in_the_fresh_token_gives_403_role_not_held_and_the_item_stays_claimed`. | closed |
| T-07-27 | Spoofing | Silent SSO treated as re-authentication | mitigate | `OnTokenValidated` requires `auth_time > 0`, `auth_time >= issued - 60 s` and not more than 60 s in the future, and otherwise calls `ctx.Fail("reauth_evidence_missing")` (ReviewerOidc.cs:59-70). The live Entra check is in 07-HUMAN-UAT.md item 1 (deferred). Test: `OidcReauthTests.APR_03_stale_auth_time_evidence_fails_closed_and_the_prior_session_is_kept`. | closed |
| T-07-28 | Information Disclosure | Rationale in URL, logs or TempData | mitigate | Server-side `DraftStore` (src/Olive.Host/Auth/DraftStore.cs). Only the opaque 32-byte base64url key goes in the URL (DraftStore.cs:27; Item.cshtml.cs:166-167). `DraftSnapshot.ToString()` returns `[REDACTED]` (DraftStore.cs:10). Tests: `DraftStoreTests.APR_03_key_is_43_chars_base64url_and_unique`, `DraftStoreTests.APR_03_snapshot_to_string_is_redacted`, `OidcReauthTests.APR_03_rationale_and_tokens_never_reach_urls_html_cookies_or_logs`. | closed |
| T-07-29 | Tampering | Draft replay or hijack across users | mitigate | Single use with oid and item binding, and a mismatch does not consume the draft (DraftStore.cs:49-65). 10-minute TTL (DraftStore.cs:16). Global cap (DraftStore.cs:17,36-40). The rationale is capped at 4000 characters before `Put` (Item.cshtml.cs:161). Take is used only for the claimant (Item.cshtml.cs:95). Tests: `DraftStoreTests.APR_03_draft_is_single_use`, `APR_03_draft_expires_at_ten_minutes`, `APR_03_wrong_oid_or_item_gets_nothing_and_does_not_consume_the_draft`, `OidcReauthTests.APR_03_another_reviewers_resume_key_is_unavailable_and_the_owner_can_still_use_it_once`. Note: IN-01, see Audit Notes. | closed |
| T-07-30 | Tampering | State-changing GET after IdP round trip | mitigate | The resume GET only sets the `DraftDecision`/`DraftRationale` display properties and calls no QueueService mutator (Item.cshtml.cs:91-106). Deciding happens only in the antiforgery-protected `OnPostDecideAsync`. Test: `OidcReauthTests.UI_04_resume_get_never_decides`. | closed |
| T-07-31 | Elevation of Privilege | Re-auth extends 8 h cap or switches identity | mitigate | On a re-challenge, a different oid or a missing prior `session_start` fails, and the original `session_start` is preserved (ReviewerOidc.cs:72-83,87-89). Test: `OidcReauthTests.APR_03_re_authentication_as_a_different_oid_fails_closed_and_the_cookie_is_unchanged`. | closed |
| T-07-32 | Elevation of Privilege | Credential admitted on wrong route group | mitigate | Table-driven 7 x 5 matrix with intended-admission expectations: `CredentialRouteMatrixTests.UI_07_only_the_intended_credential_route_pairings_are_admitted` and `UI_07_cell_table_covers_seven_credentials_by_five_routes` (tests/Olive.Tests/CredentialRouteMatrixTests.cs:161,196). After WR-01, the `/dev/sign-in` column expects 404 in OIDC mode. | closed |
| T-07-33 | Elevation of Privilege | Agent key introspected/accepted on reviewer/UI routes | mitigate | Introspector call count is asserted unchanged on non-agent routes (CredentialRouteMatrixTests.cs:171,185-189). The code path is AgentKeyAuthenticationHandler.cs:23-28. The 403 `principal_not_permitted` cell maps to AgentKeyAuthenticationHandler.cs:49-53. | closed |
| T-07-34 | Information Disclosure | Secrets/rationale in any response, header, cookie or log | mitigate | Consolidated scan across all page types, including error pages, on both stub-mode and OIDC-mode hosts after WR-01: `SentinelUiTests.IDN_08_ui_every_page_type_has_the_strict_csp_no_store_and_leaks_no_secret` (tests/Olive.Tests/SentinelUiTests.cs:45). | closed |
| T-07-35 | Tampering | UI surface widens or loads third-party scripts | mitigate | `UiSurfaceTests.UI_06_the_only_non_api_routes_are_the_nine_reviewer_pages` (route-set equality), `UI_06_no_page_or_asset_file_name_mentions_a_banned_feature`, `UI_06_pages_carry_no_script_inline_style_raw_html_or_third_party_url`, `UI_06_page_models_take_no_database_or_http_types`. The CSP has no third-party origin (SecurityHeaders.cs:6). | closed |
| T-07-36 | Spoofing | Dev sign-in reachable in production | mitigate | Three independent layers: (1) the gate is absent when OIDC is configured, which production requires (Program.cs:130-133). (2) FND-05 refuses `INonProductionOnly` in production (StartupGuards.cs:159-209). (3) The UI-01 check refuses a registered gate (StartupGuards.cs:120-123). Tests: `CredentialRouteMatrixTests.UI_07_production_refuses_dev_sign_in_gate_and_http_authority`, `DevSignInTests.UI_01_production_refuses_to_start_with_the_dev_sign_in_gate_registered`, `DevSignInTests.UI_01_dev_sign_in_is_not_reachable_when_oidc_is_configured`. | closed |
| T-07-37 | Spoofing | Live Entra ignores max_age or omits auth_time | accept | Accepted risk AR-07-01 (below). The code-side fail-closed rule is verified under T-07-27 (ReviewerOidc.cs:59-70). Live behaviour is 07-HUMAN-UAT.md item 1, status `deferred: external dependency`. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-07-01 | T-07-37 | Live Microsoft Entra may ignore `max_age=0` or leave `auth_time` out of the ID token, so `prompt=login` might be satisfied silently. The residual risk is bounded in code. `OnTokenValidated` fails closed (`reauth_evidence_missing`, redirect to `/auth/failed`, prior session kept) when `auth_time` is absent, zero, older than the challenge minus 60 s, or future-dated (src/Olive.Host/Auth/ReviewerOidc.cs:59-70; test `OidcReauthTests.APR_03_stale_auth_time_evidence_fails_closed_and_the_prior_session_is_kept`). The worst case is availability (reviewers cannot complete a re-challenged decision), not unauthorised approval. Live verification needs a real Entra tenant and the supervisor group, which do not exist yet. It is tracked as 07-HUMAN-UAT.md item 1 (`deferred: external dependency`). If live Entra omits `auth_time`, a fallback must be decided per RESEARCH Open Question 1, without loosening the rule. | Plan-time disposition (07-06-PLAN.md threat model); manual UAT deferral by user decision 2026-10-24 | 2026-10-24 |

*Accepted risks do not resurface in future audit runs.*

---

## Unregistered Flags

None. Every SUMMARY `## Threat Flags` section (07-01 to 07-06) reports "None beyond the plan's threat model".

---

## Audit Notes (non-blocking, from 07-REVIEW.md deferred findings)

All of these were checked against current main. None of them removes a declared mitigation, so they do not reopen any threat. They are recorded so the residual risk is visible.

- **WR-02 (affects T-07-24, T-07-36):** the shipped `Program.cs` composition always registers `FixtureReviewerIdentitySource` (Program.cs:102), the `DevReviewer` scheme (Program.cs:95) and the Phase 1 stub/fixture resolvers. A production start therefore always stops at FND-05 (StartupGuardFilter.cs:17) before `CheckOidcConfiguration` runs (StartupGuardFilter.cs:19). This fails closed and is consistent with Phase 1 being simulation-only. The UI-01 checks are proven only by tests that build their own service collections (`OidcConfigGuardTests.UI_01_production_host_without_oidc_authority_refuses_with_ui_01_after_other_guards_pass` removes the non-production parts). No test boots the real composition in production and proves it passes UI-01. Track this for the Phase 9 production composition.
- **WR-03 (affects T-07-24 outside production):** `OidcConfig.Read` returns null on a partial configuration (OidcConfig.cs:15-18). A non-production host then silently falls back to stub mode with the dev picker. Production is unaffected (UI-01 and FND-05).
- **WR-01 residual (affects T-07-01/T-07-08):** with OIDC configured, a `auth_source=stub` cookie is still treated as Fresh by `DecisionFreshness` (DecisionFreshness.cs:42-51). Minting one requires the host's data-protection key. A defence-in-depth follow-up could refuse stub cookies whenever OIDC is configured.
- **WR-06 (affects T-07-08/T-07-26):** Entra freshness uses the cookie's groups and roles, bounded by `auth_time` within 5 minutes plus 60 s skew. It does not re-resolve them from the directory. The declared T-07-26 mitigation (auth_time window, then the fresh token's roles) is implemented as written. The D-10 comment in DecisionFreshness.cs:8 overstates the property.
- **WR-07:** an Entra cookie that survives a restart without OIDC settings produces a 500 on a Stale challenge (Item.cshtml.cs:171). This fails closed, because nothing is decided.
- **IN-01 (T-07-29):** the DraftStore cap is global (1000), so one reviewer can evict other reviewers' drafts. The impact is availability only.
- **IN-03 (T-07-22):** the log-level filters are provider-less rules, so a provider-specific config override could re-enable the request-start lines that carry `?code=`. They hold under the default configuration and the sentinel tests.
- **IN-05:** the TempData cookie lacks the `__Host-` and Secure hardening. It carries only static codes.
- **IN-08 (T-07-21):** production does not require `OLIVE_OIDC_TENANT_ID`. Issuer validation on a single-tenant authority pins the tenant.

---

## Verification Run

- Targeted security unit tests run during this audit (no Docker needed): EntraClaimsMapperTests, DraftStoreTests, OidcConfigGuardTests, AuditHashCheckTests, SecretTypeTests, UiSurfaceTests, DecisionFreshness*, ArchitectureTests. Result: **100 passed, 0 failed**.
- The database- and IdP-backed tests (DevSignInTests, ReviewerOidcTests, OidcReauthTests, CredentialRouteMatrixTests, SentinelUiTests, AuditPageTests, StartupGuardTests) were not re-run here because they need Testcontainers. The last full-suite result is 1394 passed, 0 failed (07-REVIEW-FIX.md, after 33efa88).

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-24 | 38 | 38 (37 mitigate verified, 1 accepted) | 0 | gsd-security-auditor (Claude), ASVS L1, block_on: high |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-24
