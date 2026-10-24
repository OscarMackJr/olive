# Phase 7: Reviewer UI and Entra OIDC - Research

**Researched:** 2026-10-24
**Domain:** ASP.NET Core 10 Razor Pages + cookie session + OpenIdConnect handler (Entra), decision-time role freshness, CI against mock-oauth2-server
**Confidence:** MEDIUM (ASP.NET / mock server mechanics HIGH; live Entra behaviour of `max_age` and `auth_time` is undocumented and needs a real-tenant check)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
Carried forward, not re-decided:
- Q13 / Phase 5 D-08: the reviewer's tenant comes only from the source-controlled map `config/reviewer-tenants.yaml`, one tenant per session, never from a header, form or query.
- Phase 5 D-01: the UI calls the existing `QueueService`. It has no logic path of its own.
- Phase 5 D-04 reason codes, D-05/D-19 role-gated payload, D-09 decide-time `required_role` gate in the kind registry, D-18 (self-approval identity check on approve only), D-21 (catalog `required_approval` = kind `required_role`).
- Phase 5 D-06 / Q14: self-approval is decided by oid+tid equality through `IUserIdentityResolver`, failing closed.
- Phase 6 D-01: an approve triggers simulated execution after commit, in the same request. Q18: the audit chain is shown through the `execution_chain` view and the UI audit page, with no new API endpoint.
- Q15 is resolved below (D-05..D-10). Graph is never called.

Sign-in and session model
- **D-01:** Cookie session with an OIDC challenge. A dedicated reviewer cookie scheme holds the UI session. The OpenIdConnect handler signs in to it. At sign-in (`OnTokenValidated`), Entra claims are mapped to the existing `ReviewerClaims` shape: oid, tid, groups, the role, `auth_source = entra`, and the tenant from the Q13 map. `QueueService` then sees exactly what `DevReviewerAuthenticationHandler` emits today. Tokens are never stored (no `SaveTokens`), and nothing is re-validated per request beyond the cookie.
- **D-02:** Dev sign-in is a fixture picker page. `/dev/sign-in` lists the fixture reviewers from `IReviewerIdentitySource` and issues the same reviewer cookie with `auth_source = stub`. It is `INonProductionOnly` and barred in production by the existing startup guards (FND-05, Q17). The existing `X-Olive-Dev-Reviewer` header scheme stays for the JSON API (see D-15).
- **D-03:** Session lifetime: about 30 min idle with sliding expiry, and an absolute cap of about 8 h (the proposal expiry default). An explicit sign-out clears the cookie and, for Entra, also signs out of the IdP session.
- **D-04:** Zero or several tenants: authentication succeeds, but every queue page returns a plain 403 page ("no reviewer tenant for your account") with sign-out, using a static reason. This mirrors the API's 403. A cookie is still issued, so the misconfiguration is visible to the user.

Role freshness at decision time (APR-03, Q15)
- **D-05:** `max_age` re-challenge on decide. When the approve or reject form is submitted, if the session's `auth_time` is older than the freshness window, the UI redirects to the IdP with `max_age` (or `prompt=login`), takes the fresh role claims from the returned token, and only then completes the decision. `auth_time` in the fresh token is the evidence. The draft rationale and the chosen decision must survive the round-trip (for example, held server-side or in protected state keyed to the item), and must never be logged or put in the redirect URL in clear text.
- **D-06:** The freshness window is 5 minutes. It is configurable downward. A code-level maximum stops configuration from widening it.
- **D-07:** The role comes from the Entra app role (`roles` claim). An app role on the Olive registration is assigned to the case_supervisor group and mapped to the kind registry's `required_role` value (`role:case_supervisor`). The tenant still comes from the `groups` claim through the Q13 map.
- **D-08:** Groups overage fails closed. If the groups claim is missing because of overage (`_claim_names` / `hasgroups` present), the session is treated as having zero tenants and gets the D-04 403 page with the static reason `groups_overage`. There is no Graph call. Operators fix this by filtering the token's groups to those assigned to the app.
- **D-09:** A role missing at decide gives a 403 page with the static reason `role_not_held`. The item stays claimed by that reviewer, who can then release it (Q7, claimant-only release, is unchanged). No `queue_item_event` is written for a refused attempt, consistent with the Phase 5 API.
- **D-10:** The dev/stub path re-reads the reviewer fixture at decide, rather than trusting cookie claims. Tests can then remove the role from a fixture after the claim and observe the refusal (Success Criterion 3) without an IdP. The mock-oauth2-server test proves the real `max_age` path (D-17).

Pages and content
- **D-11:** Three page types: `/review` (open items for the session's tenant, oldest first, IR-1 fields only); `/review/items/{id}` (payload, claim and release buttons, decide form shown only to the claimant); `/review/items/{id}/audit` (history, hashes, execution chain). All three call `QueueService`. Every POST uses antiforgery and post-redirect-get (UI-03).
- **D-12:** The payload is shown as rendered, HTML-encoded fields: action_type, on_behalf_of, expires_at with time remaining, trace_ref, justification, and parameters as a key/value table. Only holders of the kind's `required_role` can see it (Phase 5 D-19). Others see only the item refs. A banner appears when `trace_verification = fixture` (UI-05).
- **D-13:** The audit page shows the hashes and recomputes them. It shows the four hashed fields (action_type, party_ref, parameters, justification), the stored `body_sha256` and `proposal_hash`, and a server-side "recomputed proposal_hash matches" indicator using the existing `Olive.Domain.Jcs`. It also shows the `queue_item_event` history, seq-ordered.
- **D-14:** For decided action items, the audit page shows the `execution_chain` row: the policy decision, approver (`decided_by`), rationale, `auth_source`, `decided_at`, and the started/finished ledger rows (`execution_mode = simulated`, outcome, reason, times). It is read through the tenant unit of work. It adds no new API endpoint and does not show parameters beyond D-12.
- **D-19 (errors):** Conflicts use PRG with a one-time static flash message keyed on the Phase 5 reason codes (`proposal_expired`, `already_claimed`, `not_claimant`, `decision_conflict`, `role_not_held`, `approval_identity_unresolved`, ...). Flash messages never echo the rationale or parameters. A missing or cross-tenant item gives 404, which can't be told apart.
- **D-20 (styling):** Plain semantic HTML and one small static stylesheet. No JS framework, no CDN, no Bootstrap. A CSP with `default-src 'self'`. Razor encoding everywhere, no `Html.Raw` on proposal data.

Credential x route matrix and config
- **D-15:** The JSON reviewer API (`/v1/queue/items/*`) keeps the DevReviewer header only. The UI cookie is refused on every `/v1/*` route, so antiforgery and CSRF concerns never reach the API. The Razor pages call `QueueService` in-process, not the JSON API. In production, the reviewer JSON API has no accepted scheme (401/403). No JwtBearer is added.
- **D-16:** The UI-07 matrix uses five route groups: (1) `/v1/proposals` (agent), (2) `/v1/catalog`, (3) `/v1/queue/items` (reviewer JSON), (4) `/review/*` (UI pages), (5) `/dev/sign-in` (non-production only). It tests six credential types: none, agent key, DevReviewer header, dev cookie, OIDC cookie from mock-oauth2-server, and a forged or expired cookie. Expected outcomes are a table-driven fixture. Only the intended pairings are admitted. An agent key on reviewer routes still gets `403 principal_not_permitted` (Phase 5 D-02).
- **D-17:** mock-oauth2-server runs via Testcontainers (`ghcr.io/navikt/mock-oauth2-server`, pinned by digest). The test drives the authorize -> callback flow through the real OpenIdConnect handler. Per-request claim config supplies `roles`, `groups`, `auth_time` and the overage marker. This covers: role present, role removed after claim (refused at decide), stale `auth_time` (re-challenge), overage (403), and a tenant mapping to zero or several tenants. The host runs with `OLIVE_ENVIRONMENT=ci`. Any HTTP client use stays in the test project.
- **D-18:** Production OIDC config comes from environment variables (and `.env` locally): `OLIVE_OIDC_AUTHORITY`, `OLIVE_OIDC_CLIENT_ID`, and a client secret or certificate. In production, a startup guard fails if any is missing, if the authority isn't `https`, or if DevReviewer or the dev sign-in page is registered. The client secret is a sealed, redacting type like the session key. It is a read/sign-in credential, not a write credential, but it must be listed in the Phase 9 credential audit (EVD-06).

### Claude's Discretion
- Cookie hardening details: name, `SameSite` (Lax is likely needed for the OIDC form_post/redirect), `Secure`/HTTPS in local dev (dev certs vs a ci/local exception), and the data-protection key location.
- How the rationale draft survives the `max_age` round-trip (D-05): a server-side short-TTL store, or data-protected state. It must not be logged or put in a URL.
- Claim-mapping code location (for example, an `EntraClaimsMapper` in `Olive.Integrations.Entra`, with `ReviewerClaims` staying the single claims contract), and how `OnTokenValidated` uses `IReviewerTenantMap`.
- How the decide-time freshness check is exposed to `QueueService` without leaking HTTP concepts into Application, for example a "fresh role" input on `ReviewerContext`, or a port that the stub implements by re-reading the fixture (D-10).
- Which oid/tid claim types Entra uses (`oid`/`tid` vs the long URIs) and how inbound claim mapping is disabled.
- Exact page routes, the flash mechanism (TempData vs a cookie), and the `ProblemReasons` additions (`role_not_held`, `groups_overage`, `no_reviewer_tenant`).
- Whether Razor Pages live in `Olive.Host` or a separate UI project, within the FND-01 dependency graph and package allowlist (`Microsoft.AspNetCore.Authentication.OpenIdConnect` must be added to the allowlist and lockfiles).
- Research topics already flagged in ROADMAP: `max_age` behaviour on Entra, group overage, oid mapping, SameSite and HTTPS.

### Deferred Ideas (OUT OF SCOPE)
- A production reviewer JSON API (Entra JwtBearer on `/v1/queue/items`): no Phase 1 consumer. Revisit if bluto or another client needs it.
- Teams notifications linking to the UI: P2-04.
- Auto-release on a role-missing refusal: rejected for now. It would extend Q7.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| UI-01 | Sign in through the OpenIdConnect handler (Entra behind an interface); DevReviewer IdP for local/CI, barred in production | Cookie + OIDC wiring, `EntraClaimsMapper`, conditional registration, production guard (Architecture Patterns 1, 2, 7) |
| UI-02 | Razor Pages list of open items in the reviewer's tenant | `QueueService.ListAsync`, folder-level `AuthorizeFolder` (Pattern 3) |
| UI-03 | Claim/release POSTs with antiforgery + PRG | Razor Pages auto-validates antiforgery on POST; PRG + flash (Pattern 4) |
| UI-04 | Approve/reject with required rationale | `QueueService.DecideAsync`, decide-time freshness (Pattern 5) |
| UI-05 | Audit view: four hashed fields, `body_sha256`, `proposal_hash`, fixture banner | New read-only audit store method (ProposalRecord lacks the hashes), `Jcs.ProposalHash` (Pattern 6) |
| UI-06 | No dashboards/bulk/SLA/assignment | Three pages only; architecture test on the page set (Validation) |
| UI-07 | CI test of the real OIDC handler vs mock-oauth2-server, all credential types x all route groups | mock-oauth2-server per-issuer scenarios, manual redirect driving, 6x5 matrix (Pattern 8, Code Examples) |
| APR-03 | `role:case_supervisor` checked at decision time, not claim time | Fresh-token re-challenge (`prompt=login` + `max_age=0`) + stub fixture re-read (Pattern 5) |
</phase_requirements>

## Summary

Phase 7 is almost entirely a Host-layer phase. `QueueService` already enforces the decide-time role check (`reviewer.Groups.Contains(definition.RequiredRole)`), the self-approval check, claimant-only release and all reason codes. The work is (a) producing a correct `ReviewerContext` for a cookie session, (b) making that context fresh at decide time, (c) rendering three pages, and (d) a 30-cell auth matrix test. Only one new NuGet package is needed: `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.13 (Host allowlist + `Directory.Packages.props` + lockfile). Razor Pages and antiforgery ship in the `Microsoft.NET.Sdk.Web` framework that Olive.Host already uses.

The most important research findings are risks to the CONTEXT decisions, not confirmations. (1) Microsoft's documented authorize-request parameters for Entra list `prompt` (`login`, `none`, `consent`, `select_account`), `login_hint` and `domain_hint`, but not `max_age`; `auth_time` is only an optional claim that must be configured on the app registration. So the "fresh `auth_time` is the evidence" design cannot be proven against Entra from docs. Recommended design: send both `prompt=login` and `max_age=0`, and fail closed unless the returned token carries an `auth_time` at or after the moment the challenge was issued (the app registration must enable the `auth_time` optional claim). (2) The OIDC handler does not itself compare `auth_time` to `max_age`; Olive must. (3) The handler's defaults are `response_type=id_token` and `response_mode=form_post`, which force `SameSite=None; Secure` correlation cookies; use `code` + `query` + PKCE so Lax works. (4) The `groups` claim carries group object IDs (GUIDs) by default, while `config/reviewer-tenants.yaml` is keyed by names like `tenant-alpha-reviewers`; the production map needs object IDs (or Entra app roles) and this must be decided before live use.

**Primary recommendation:** Put Razor Pages, the cookie scheme, the OIDC scheme and an `EntraClaimsMapper` in `Olive.Host` (and `Olive.Integrations.Entra` for the mapper); use `code` + `query` + PKCE; on a stale `auth_time` at decide, stash the draft in a server-side single-use store, challenge with `prompt=login` + `max_age=0`, validate `auth_time >= challenge_issued_at` in `OnTokenValidated`, then return to the item page with the draft restored so the reviewer confirms with a normal POST under fresh claims.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| OIDC sign-in, token validation, cookie issue | Frontend Server (Olive.Host) | IdP (Entra / mock) | Server-side confidential client; tokens never reach the browser or storage |
| Claim mapping (Entra claims -> `ReviewerClaims`) | Frontend Server (Host / Integrations.Entra) | — | `ReviewerClaims` stays the single claims contract; tenant from `IReviewerTenantMap` only |
| Decide-time role/freshness gate | Frontend Server (Host: freshness + re-challenge) | API/Backend (`QueueService`: role check) | Freshness is an HTTP/IdP concept and stays out of Application; the role check already lives in `QueueService` |
| Queue operations, role-gated payload, reason codes | API / Backend (`QueueService`) | Database (RLS + store) | Existing Phase 5 logic; the UI has no logic path of its own |
| Page rendering, flash, antiforgery, CSP | Frontend Server (Razor Pages) | Browser (HTML only) | No JS framework; Razor encoding |
| Audit read (hashes, chain, history) | Database / Storage (tenant UoW, `execution_chain`) | Frontend Server | Read-only new store method; no new HTTP API |
| Rationale draft survival across re-challenge | Frontend Server (in-memory single-use store) | — | Must not be in URL or logs |
| Route-group credential separation | Frontend Server (auth policies per group) | — | Explicit `AuthenticationSchemes` per policy; cookie never authenticates `/v1/*` |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.13 | OIDC handler (code flow, PKCE, nonce, logout) | The one new package. Matches the ASP.NET 10 runtime already pinned via `Microsoft.AspNetCore.Mvc.Testing` 10.0.13. [VERIFIED: nuget.org flat-container index lists 10.0.13 as the latest 10.0.x; `dotnet package search` returned 10.0.13] |
| Razor Pages, cookie auth, antiforgery, data protection | in `Microsoft.NET.Sdk.Web` | UI, session, CSRF | Already available to `Olive.Host`; no package needed [CITED: learn.microsoft.com ASP.NET Core docs; Olive.Host.csproj uses Sdk.Web] |

### Supporting (tests only)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Testcontainers (core) | 4.16.0 | `ContainerBuilder` for mock-oauth2-server | Already a transitive dependency of `Testcontainers.PostgreSql` 4.16.0; add an explicit `PackageVersion`/`PackageReference` in the test project only [VERIFIED: nuget flat-container lists 4.16.0] |
| ghcr.io/navikt/mock-oauth2-server | 6.1.3 (latest release 2026-10-23) | Mock IdP | Pin by digest. Index digest `sha256:2a813381bf282da46ff36ff386059e7903e25865056af8149f4fb3f1441916b8` (linux/amd64 manifest `sha256:95159e47ce682b41841e2671c36ebfa509d82b62c8b8fb4f18b772ccea3e404d`) [VERIFIED: `docker buildx imagetools inspect`, GitHub releases API] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Cookie + OIDC | Microsoft.Identity.Web | Adds a package and abstractions Olive does not need; the plain handler is enough and the allowlist stays at one addition |
| `max_age`/`prompt=login` fresh token | Graph `checkMemberObjects` / `appRoleAssignments` | Real-time, but needs a read credential, outbound HTTP (banned by REQ-ACT-04 analyzers) and is excluded by Q15 |
| Server-side draft store | Data-protected state in OIDC `state` | Fits only if the rationale (up to 4000 chars) stays under URL limits; it also puts (encrypted) rationale in a URL, which D-05 forbids |

**Installation:**
```bash
# Directory.Packages.props: <PackageVersion Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="10.0.13" />
# src/Olive.Host/Olive.Host.csproj: <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" />
dotnet restore --force-evaluate     # regenerates packages.lock.json for Olive.Host (CI uses --locked-mode)
# tests/Olive.Tests: <PackageReference Include="Testcontainers" />  (+ PackageVersion 4.16.0)
# tests/Olive.Tests/ArchitectureTests.cs: PackageAllowlist["Olive.Host"] = ["Microsoft.AspNetCore.Authentication.OpenIdConnect", "Npgsql"]
```

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| Microsoft.AspNetCore.Authentication.OpenIdConnect | NuGet | first-party ASP.NET Core package, many years | very high | github.com/dotnet/aspnetcore | not applicable (slopcheck targets PyPI/npm; install attempted, NuGet not covered) | Approved: first-party Microsoft package, version confirmed on the registry and `dotnet package search` |
| Testcontainers | NuGet | years | very high | github.com/testcontainers/testcontainers-dotnet | not applicable | Approved: already a transitive dependency of the pinned `Testcontainers.PostgreSql` 4.16.0 |

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none
Note: NuGet has no postinstall scripts. The CI "Vulnerable package gate" (`dotnet list package --vulnerable --include-transitive`) must be re-run after the restore because the OIDC package pulls `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` transitively (central transitive pinning is already enabled in `Directory.Packages.props`).

## Architecture Patterns

### System Architecture Diagram

```
Browser
  |  GET /review/*  (reviewer cookie, SameSite=Lax, Secure, HttpOnly)
  v
[Kestrel] -> UseExceptionHandler -> UseStaticFiles -> CSP/headers -> UseAuthentication (default scheme AgentKey: skip non-/v1)
  |                                                   -> UseAuthorization
  |-- /v1/proposals, /v1/catalog -----> AgentKey scheme only (cookie never evaluated)
  |-- /v1/queue/items ----------------> DevReviewer header scheme only (non-prod); prod: no scheme -> 401/403
  |-- /dev/sign-in (non-prod only) ---> picker -> issues reviewer cookie (auth_source=stub)
  |-- /auth/sign-in ------------------> Challenge("Entra")  ----> IdP /authorize (code, query, PKCE, nonce)
  |       ^                                                           |
  |       +---- /signin-oidc (handler) <---- code ---- redirect ------+
  |             token endpoint (handler backchannel) -> id_token validated
  |             OnTokenValidated: EntraClaimsMapper -> ReviewerClaims.ToPrincipal(entra) + olive:auth_time, session_start
  |                              reauth? check auth_time >= challenge_issued_at else fail
  |             cookie issued -> redirect to ReturnUrl
  |-- /review/** (policy ReviewerUi: cookie scheme only, RequireAuthenticatedUser)
         Page handler:
           principal -> ReviewerContext (null tenant => 403 page: no_reviewer_tenant / groups_overage)
           Claim/Release  -> QueueService -> PRG + flash(reason)
           Decide POST    -> DecisionFreshness.Resolve(principal)
                               stub : re-read fixture (IReviewerIdentitySource) -> fresh groups
                               entra: auth_time within window? yes -> cookie claims
                                      no -> DraftStore.Put(oid,item,decision,rationale) -> Challenge(prompt=login,max_age=0)
                             -> QueueService.DecideAsync(ReviewerContext, ...) -> role check, identity check, store.DecideAsync
                             -> (Phase 6) ExecutionService after commit -> PRG to item page
         Audit GET -> QueueService.EventsAsync + AuditReadStore (proposal row + execution_chain via TenantUnitOfWork)
                      + Jcs.ProposalHash recompute
```

### Recommended Project Structure
```
src/Olive.Host/
├── Auth/
│   ├── ReviewerCookie.cs            # scheme name, cookie options, absolute-cap validator
│   ├── ReviewerOidc.cs              # AddReviewerOidc(): options, events (OnTokenValidated, OnRedirectToIdentityProvider, OnRemoteFailure)
│   ├── DecisionFreshness.cs         # stub fixture re-read / entra auth_time window
│   └── DraftStore.cs                # single-use, TTL, oid-bound, redacting
├── Pages/ (Razor)
│   ├── Review/Index.cshtml(.cs), Review/Item.cshtml(.cs), Review/Audit.cshtml(.cs), Review/Forbidden.cshtml
│   ├── Dev/SignIn.cshtml(.cs)       # non-prod only
│   └── Shared/_Layout.cshtml
├── Startup/OidcConfig.cs            # env vars -> options; sealed redacting ClientSecret; production guard
└── wwwroot/olive.css
src/Olive.Integrations.Entra/EntraClaimsMapper.cs   # pure function: claims -> ReviewerIdentity + flags (groups_overage)
src/Olive.Application/IAuditStore.cs (+ Persistence/PostgresAuditStore.cs)  # tenant-UoW read of proposal hashes + execution_chain
```
Pages stay in `Olive.Host` (Sdk.Web already; no new project, FND-01 graph unchanged, still 11 projects).

### Pattern 1: Reviewer cookie + OIDC, code flow with query response
**What:** Two schemes. `OliveReviewer` (cookie) is the session. `Entra` (OIDC) has `SignInScheme = OliveReviewer`.
**Why not defaults:** handler defaults are `ResponseType=IdToken` and `ResponseMode=FormPost` [VERIFIED: dotnet/aspnetcore `OpenIdConnectOptions.cs`]. With `form_post` the correlation and nonce cookies are `SameSite=None` (browsers then require `Secure`) [VERIFIED: same file, nonce cookie `SameSite = SameSiteMode.None`]. Entra also needs "ID tokens" issuance enabled for `id_token` response types [CITED: learn.microsoft.com/entra/identity-platform/v2-protocols-oidc]. With `code` + `query` + PKCE (`UsePkce` defaults true for code), the callback is a top-level GET and Lax cookies are sent, so the correlation/nonce cookies can be Lax.
**Example:**
```csharp
// Source: dotnet/aspnetcore OpenIdConnectOptions defaults (verified) + Microsoft Learn OIDC docs
services.AddAuthentication()   // keep AgentKey as the default scheme as today
  .AddCookie(ReviewerCookie.Scheme, o => {
      o.Cookie.Name = "__Host-olive-reviewer";          // requires Secure + Path=/ + no Domain
      o.Cookie.HttpOnly = true;
      o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
      o.Cookie.SameSite = SameSiteMode.Lax;             // not Strict: top-level navigation from outside (links, IdP) must carry it
      o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = true;
      o.LoginPath = "/auth/sign-in";                    // challenge target for browsers
      o.Events.OnValidatePrincipal = ReviewerCookie.EnforceAbsoluteCap;   // see Pitfall 3
  })
  .AddOpenIdConnect("Entra", o => {
      o.SignInScheme = ReviewerCookie.Scheme;
      o.Authority = cfg.Authority;                      // https://login.microsoftonline.com/<tenant-guid>/v2.0 (never /common)
      o.ClientId = cfg.ClientId; o.ClientSecret = cfg.Secret.Reveal();
      o.ResponseType = OpenIdConnectResponseType.Code;
      o.ResponseMode = OpenIdConnectResponseMode.Query;
      o.UsePkce = true;
      o.MapInboundClaims = false;                       // raw "oid", "tid", "roles", "groups"
      o.SaveTokens = false;                             // default; never store tokens
      o.GetClaimsFromUserInfoEndpoint = false;
      o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
      o.CorrelationCookie.SameSite = SameSiteMode.Lax; o.NonceCookie.SameSite = SameSiteMode.Lax;
      o.CorrelationCookie.SecurePolicy = o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
      o.RequireHttpsMetadata = cfg.RequireHttps;        // true in production; false only for ci mock (http)
      o.Events.OnTokenValidated = ctx => { /* map + reauth check, Pattern 2 and 5 */ return Task.CompletedTask; };
      o.Events.OnRemoteFailure = ctx => { ctx.Response.Redirect("/auth/failed"); ctx.HandleResponse(); return Task.CompletedTask; }; // static page; never echo exception text
  });
```
The mock server's issuer is `http://...` in CI, so `RequireHttpsMetadata=false` is allowed only when `OLIVE_ENVIRONMENT` is `ci` or `local` and the production guard (D-18) asserts it is `true` in production.

### Pattern 2: Claim mapping (`EntraClaimsMapper`, pure and unit-testable)
- Input: id_token claims (raw names because `MapInboundClaims=false`). Output: `ReviewerIdentity(ReviewerId=oid, Oid, Tid, Upn=preferred_username, Groups)` plus flags.
- `Oid` = `oid`, `Tid` = `tid` (GUID strings; require both present and a valid GUID, and `tid` must equal the tenant in the configured authority). [CITED: ID token claims reference: `oid` immutable user identifier, `tid` tenant]. Never use `sub` (pairwise per app), `upn`, `email` or `preferred_username` for identity.
- `Groups` = values of the `groups` claim that do NOT start with `role:`, plus the `roles` claim values that appear in an explicit allowlist (the kind registry's `required_role` set: `role:case_supervisor`, `role:identity_steward`). Reason: `QueueService` checks `reviewer.Groups.Contains(requiredRole)`, so a group whose display name is `role:case_supervisor` (possible with `cloud_displayname`) would otherwise grant the role. Decision for the planner: either (a) set the Entra app role `value` to exactly `role:case_supervisor`, or (b) name it `case_supervisor` and map `case_supervisor -> role:case_supervisor` in the mapper. Option (b) avoids relying on the characters Entra allows in an app role value [ASSUMED, see A3].
- Overage detection: `hasgroups` claim present, or `_claim_names` containing a `groups` key [CITED: ID token claims reference, "Groups overage claim"]. With `JsonWebTokenHandler` a JSON-object claim arrives as a claim whose value is a JSON string, so match `_claim_names` by `Contains("\"groups\"")` or parse it with `JsonDocument`. Result: zero tenants and reason `groups_overage`.
- Tenant: `tenantMap.TenantsFor(groups).Distinct()`; exactly one -> tenant claim; otherwise none -> D-04 page with `no_reviewer_tenant` (or `groups_overage`).
- Add `olive:auth_time` (unix seconds from token `auth_time`, `0` when absent) and `olive:session_start` (now) as extra claims alongside `ReviewerClaims.ToPrincipal(identity, tenant, QueueStates.Entra, scheme)`. Replace `ctx.Principal` in `OnTokenValidated` so the cookie serialises only Olive claims (no raw token claims, no tokens).

### Pattern 3: Route-group isolation
- Keep `AgentKey` as the default scheme but make `AgentKeyAuthenticationHandler.HandleAuthenticateAsync` return `NoResult` for any path not under `/v1/proposals` or `/v1/catalog` (today it skips only `/v1/queue`). Otherwise `UseAuthentication` introspects an agent key sent to `/review/*` or `/dev/*` (the existing T-05-23 concern, extended).
- `/review/**`: `options.Conventions.AuthorizeFolder("/Review", "ReviewerUi")` with policy `AddAuthenticationSchemes("OliveReviewer").RequireAuthenticatedUser()`. A missing or forged/expired cookie challenges the cookie scheme, which redirects (302) to `/auth/sign-in` (Entra) or `/dev/sign-in` (non-production).
- `/v1/queue/items` keeps `AuthenticationSchemes = DevReviewer` (unchanged). Because the cookie scheme is not named in any `/v1/*` policy, a cookie sent there is never evaluated. [CITED: ASP.NET Core authorization docs, policy-level `AuthenticationSchemes`]
- `/dev/sign-in`: only mapped (`MapGet`/Razor page convention) when `!env.IsProduction` where `env = StartupGuards.ResolveEnvironment(builder.Configuration)`. Note `Program.cs` forces the ASP.NET host environment to Production, so the check must use `OliveEnvironment`, not `IWebHostEnvironment`.
- DevReviewer scheme registration currently happens unconditionally and `CheckDevReviewerNotInProduction` throws if it exists in production, so today a production host cannot start. Phase 7 must register `DevReviewer` and the picker only when `!env.IsProduction` (keep the guard as a backstop) and add the D-18 OIDC guard.

### Pattern 4: PRG, antiforgery, flash
- Razor Pages validate antiforgery tokens on POST handlers by default [CITED: learn.microsoft.com/aspnet/core/security/anti-request-forgery]; do not add `[IgnoreAntiforgeryToken]`. Set `AddAntiforgery(o => { o.Cookie.Name = "__Host-olive-af"; o.Cookie.SecurePolicy = Always; })`.
- Every POST handler ends with `RedirectToPage(...)` (303/302), never `Page()`. Flash via TempData with the cookie provider (`AddRazorPages().AddCookieTempDataProvider()` is the default) storing only a reason-code string; the page maps code -> static text. Never put rationale or exception text in TempData.
- Sign-out must be a POST with antiforgery: `SignOut(new AuthenticationProperties{RedirectUri="/signed-out"}, ReviewerCookie.Scheme, "Entra")`; in non-production stub sessions sign out of the cookie only.

### Pattern 5: Decide-time freshness (APR-03) - the core design
**Mechanism.** The role that `QueueService` checks comes from the `ReviewerContext.Groups` built for the decide call.
1. **Stub/dev path (D-10):** `DecisionFreshness` reads `olive:reviewer_id` from the cookie (add this claim in the picker only) and calls `IReviewerIdentitySource.Find(...)` again; builds `ReviewerContext` from the current fixture (`Groups` incl. role). Removing the role from the fixture after claim therefore produces `DecisionResult.RoleNotPermitted` -> 403 page `role_not_held`, item stays claimed (D-09). No re-challenge.
2. **Entra path:** if `now - olive:auth_time <= window` use cookie claims. Otherwise: `DraftStore.Put(...)`, then `Challenge(props, "Entra")` with `props.SetParameter(OpenIdConnectParameterNames.Prompt, "login")`, `props.SetParameter(OpenIdConnectParameterNames.MaxAge, TimeSpan.Zero)` (handler writes `max_age=0`), `props.Items["olive.reauth"]=<challenge_issued_at unix>`, `props.RedirectUri="/review/items/{id}?resume={draftId}"`. [VERIFIED: handler reads `Prompt` and `MaxAge` from `AuthenticationProperties` parameters, dotnet/aspnetcore `OpenIdConnectHandler.cs` lines 415, 438].
3. **Evidence check in `OnTokenValidated` when `olive.reauth` is set:** require the token's `auth_time` to exist and be `>= challenge_issued_at - 60s skew`, and `iat` recent; otherwise fail the sign-in with `OnRemoteFailure` path (fail closed, session not replaced). The handler itself does not do this: `OpenIdConnectProtocolValidator.RequireAuthTime` defaults to false and `max_age` is never compared with `auth_time` by the handler [VERIFIED: IdentityModel `OpenIdConnectProtocolValidator.cs`, `RequireAuthTime = false`; aspnetcore handler only writes `message.MaxAge`].
4. After the callback the cookie holds the fresh roles. The resume GET shows the item page with the draft restored from `DraftStore` (single use, bound to the same `oid`, TTL 10 min). The reviewer confirms with a normal antiforgery POST; the decide now passes the window check and `QueueService` evaluates the fresh `Groups`. This keeps one decide code path and no state-changing GET.
5. Window: `min(configured, 5 min)`; configuration can only narrow (code constant `MaxFreshness = 5 min`). A missing `auth_time` counts as stale (age = infinity) so the first decide after a plain sign-in re-challenges unless the tenant emits `auth_time`.

**Why `prompt=login` plus `max_age=0` for Entra.** Microsoft documents `prompt=login` ("forces the user to enter their credentials on that request, which negates single sign-on") and lists `login`, `none`, `consent`, `select_account` as the only valid prompt values; `max_age` is not in the documented authorize parameter list [CITED: learn.microsoft.com/entra/identity-platform/v2-protocols-oidc]. `auth_time` is an optional JWT claim [CITED: learn.microsoft.com/entra/identity-platform/optional-claims-reference] and is not in the default ID token claim set [CITED: id-token-claims-reference]. So: send both parameters (spec-compliant providers honour `max_age`, Entra honours `prompt=login`), enable the `auth_time` optional claim on the ID token in the app manifest (`optionalClaims.idToken: [{"name":"auth_time"}]`), and treat anything else as stale. Whether Entra emits `auth_time` after `prompt=login` and ignores or accepts `max_age` is **unverified** (A1) and is the single most important live check, listed as a human-verify item in Open Questions.

**Why a fresh token carries current roles.** The `roles` claim reflects assignments when the token is issued; Microsoft states group data in tokens is only current at issuance [CITED: Microsoft Learn "configure tokens, group claims and app roles"; surfaced via search, see Sources]. Combined with `prompt=login` forcing a new authentication, the returned `roles` is the current assignment. Group-to-app-role assignment needs Entra ID P1 or higher [CITED: same source]. Expect a short Entra propagation delay after an admin removes the assignment (A2).

**Rejected:** Graph (Q15 forbids; needs outbound HTTP and a read credential, and `HttpClient` is banned by `BannedSymbols.txt`). Per-request re-validation against the IdP (no refresh tokens are stored). Silent `prompt=none` renewal (cannot prove user presence; `max_age` is the verifiable option, per the OIDC spec and Auth0's explanation of why `prompt=login` alone gives no proof).

### Pattern 6: Audit page data
`ProposalRecord` (from `IProposalStore.GetAsync`) has the four hashed fields and `TraceVerification` but **not** `body_sha256`, `proposal_hash` or `canonical_body`. Add a read-only `IAuditStore` (Application interface, Persistence implementation using `TenantUnitOfWork`) returning: proposal fields, `body_sha256`, `proposal_hash`, `trace_verification`, plus the `execution_chain` row selected by `(tenant_id, proposal_id)` (the view is `security_invoker`, grants `select` to `olive_app`, [VERIFIED: `sql/migrations/0009_execution_ledger.sql`]). Keep `where tenant_id = @t` in the SQL even though RLS is forced (project rule). Recompute with `Jcs.ProposalHash(JsonElement)` after building a JSON object from the four stored fields; compare ordinal to `proposal_hash`. Show "matches / does not match" only; never render a tampered value as trusted. Events come from `QueueService.EventsAsync` (seq ordered). `DetailJson` from events must be rendered encoded and only if static; do not print it raw.

### Pattern 7: Config and guard (D-18)
`OLIVE_OIDC_AUTHORITY`, `OLIVE_OIDC_CLIENT_ID`, `OLIVE_OIDC_CLIENT_SECRET` (read from configuration; the secret type is `sealed` with redacting `ToString` and `RedactingJsonConverter`, mirroring `SessionKey`). Guard `CheckOidcConfiguration(config, env)` mirrors existing guards: in production fail with a static message if any value missing, authority not `https`, authority host not `login.microsoftonline.com` unless explicitly allowed (planner choice), `DevReviewer` or the picker registered (reuse `CheckDevReviewerNotInProduction`, add a check for the picker endpoint/page). Static messages only; never include values. Add `OLIVE_OIDC_*` and `OLIVE_DATA_PROTECTION_PATH` to `.env.example`. Persist data-protection keys (`AddDataProtection().SetApplicationName("olive").PersistKeysToFileSystem(...)`), required in production, otherwise cookies and OIDC state become undecryptable on restart [CITED: ASP.NET Core data protection docs].

### Pattern 8: mock-oauth2-server via Testcontainers (UI-07)
- Image `ghcr.io/navikt/mock-oauth2-server@sha256:2a8133...` (index digest above), port 8080, env `JSON_CONFIG` (takes precedence over `JSON_CONFIG_PATH`) with `"interactiveLogin": false` so `/authorize` redirects straight back with a code [CITED: github.com/navikt/mock-oauth2-server README].
- **Issuer id is the first path segment of any URL**, so one container serves many scenarios: `Authority = http://localhost:{port}/{scenario}`. Define `tokenCallbacks` per `issuerId` with a `requestMapping` on `grant_type=authorization_code` returning fixed claims. Scenarios: `role-present`, `role-absent`, `overage` (`_claim_names`, `hasgroups`), `no-tenant`, `two-tenant`, `stale-auth-time` (fixed old `auth_time`), `fresh`.
- `auth_time` behaviour of the mock [VERIFIED: source `AuthorizationCodeHandler.kt`, `OAuth2TokenProvider.kt`, `IdTokenRequiredClaimsIntegrationTest.kt`]: it adds `auth_time` = code-issue time **only when the authorize request carried `max_age >= 0`**; claims returned by the token callback are merged after it and override it; the mock does not model SSO sessions or staleness. Consequences: initial sign-in without `max_age` yields no `auth_time` (-> stale by the rule above), so the first decide naturally triggers the re-challenge, the re-challenge request carries `max_age=0` and gets `auth_time=now`; the `stale-auth-time` scenario pins `auth_time` to an old epoch to prove that a re-auth whose evidence is stale is refused. The mock does not validate `prompt`.
- Role removed after claim (recommended test design, see A5): the mock cannot return different claims for two token requests of the same issuer. So seed the session with a cookie principal produced by the real mapper (role present, `auth_time=0`, minted with the test host's own data-protection provider), claim an item, then POST the decision. The stale session triggers the re-challenge to the `role-absent` issuer; the real handler returns a role-less token; the decide is then refused with `role_not_held` and the item stays claimed. A second scenario (`stale-auth-time`, pinned old `auth_time`) proves a re-auth with stale evidence is refused.
- Driving the flow without a browser: test `HttpClient` (allowed in tests) with `AllowAutoRedirect=false` and a shared `CookieContainer` per leg. The host under `WebApplicationFactory` is reached with a client whose `BaseAddress` is `https://localhost/` (so `Secure` cookies are accepted by the container). Steps: GET `/auth/sign-in` -> 302 to `http://localhost:{mockPort}/{scenario}/authorize?...` (copy the correlation + nonce cookies) -> follow the URL with a plain client -> 302 to `https://localhost/signin-oidc?code=..&state=..` -> replay that path on the host client with the cookies -> 302 to the return URL with the reviewer cookie set.
- The OIDC backchannel (discovery, JWKS, token) is real HTTP from the handler to the container; it is not Olive code, so `BannedSymbols.txt` and the `REQ_ACT_04` assembly-reference test are unaffected as long as no Olive source references `System.Net.Http` types (do not touch `OpenIdConnectOptions.Backchannel*`).
- `OliveApiFactory` needs an overload to inject `OLIVE_OIDC_*` config, `OLIVE_ENVIRONMENT=ci`, and the base address; it already sets `OLIVE_ENVIRONMENT=ci` by default and swaps ports via `ConfigureTestServices`.

### Anti-Patterns to Avoid
- **Trusting cookie roles at decide:** the cookie is up to 30 min idle / 8 h absolute old. The role in it is only as fresh as `auth_time` (Entra) or not trusted at all (stub re-read).
- **`SaveTokens=true`, logging `ProtocolMessage`, or an `OnTokenValidated` that copies the raw claim set into the cookie:** violates "tokens never logged or stored" and bloats the cookie past browser limits (overage).
- **Default `MapInboundClaims=true`:** claim types become long URIs and `oid`/`tid` lookups silently return null, which then looks like "no reviewer".
- **Acting on a GET after the IdP round-trip:** do not execute the decision from the resume GET.
- **Using `IWebHostEnvironment` for the production check:** it is forced to Production in `Program.cs`.
- **A second claims contract:** no new claim names for the cookie except `olive:auth_time`, `olive:session_start`, `olive:reviewer_id` (stub only).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| OIDC redirect, state, nonce, PKCE, id_token signature/issuer/audience/lifetime validation | Custom OAuth code | `OpenIdConnectHandler` | Subtle security surface; already validates nonce/state/correlation |
| CSRF protection | Custom tokens | Built-in antiforgery (auto-validated on Razor Pages POST) | Handles token/cookie pairing |
| Cookie encryption/expiry | Custom session store | Cookie auth + data protection | Standard, sliding expiry built in |
| Session absolute cap | Relying on `ExpiresUtc` | `OnValidatePrincipal` + `olive:session_start` claim | Sliding renewal resets `IssuedUtc` (Pitfall 3) |
| JSON canonicalisation for the hash indicator | New canonicaliser | Existing `Olive.Domain.Jcs.ProposalHash` | RFC 8785 vectors already tested |
| Group -> tenant mapping | Anything new | Existing `IReviewerTenantMap` | Q13 ruling; source-controlled |
| Redacting secret type | Plain `string` | Pattern of `SessionKey` / `RedactingJsonConverter` | Prevents leakage via logging/serialisation |
| Role/identity checks | Anything in the page code | `QueueService.DecideAsync` | D-01: no logic path of its own |

**Key insight:** every authorization rule already exists in `QueueService`; the risk in this phase is feeding it stale or wrongly-derived inputs (groups, tenant, oid) and exposing new HTTP surface that skips the Phase 5 guarantees.

## Runtime State Inventory
Not a rename/refactor/migration phase - section omitted.

## Common Pitfalls

### Pitfall 1: Handler defaults (id_token + form_post)
**What goes wrong:** Defaults are `ResponseType=IdToken`, `ResponseMode=FormPost`, correlation/nonce cookies `SameSite=None`. Over plain `http://localhost` browsers drop `SameSite=None` cookies without `Secure`; Entra additionally requires ID-token issuance to be enabled for `id_token` responses.
**How to avoid:** Set `Code` + `Query` + PKCE and Lax correlation/nonce cookies (Pattern 1); `Secure` always, local runs use an https profile or `localhost` (browsers treat `localhost` as secure for Secure cookies - Chrome/Firefox behaviour, [ASSUMED] A4).
**Warning signs:** "Correlation failed" / "Unable to unprotect the message.State" on callback.

### Pitfall 2: `max_age` not enforced by anyone
**What goes wrong:** Handler sends `max_age` but never checks `auth_time`; Entra may ignore `max_age` and may omit `auth_time`; a silent SSO token then looks like a successful "re-challenge".
**How to avoid:** `prompt=login`, enable `auth_time` optional claim, require `auth_time >= challenge_issued_at - skew` in `OnTokenValidated`, fail closed when absent; verify against a real tenant before declaring APR-03 live-ready.
**Warning signs:** decide succeeds immediately after re-challenge without a credential prompt.

### Pitfall 3: Sliding expiration resets `IssuedUtc`
**What goes wrong:** An "8 h absolute cap" implemented with `IssuedUtc`/`ExpiresUtc` is extended on every renewal.
**How to avoid:** Store `olive:session_start` in the principal at sign-in; `OnValidatePrincipal` calls `RejectPrincipal()` + `SignOutAsync` when `now - session_start > 8h` (inject `TimeProvider` so the test uses `ManualTimeProvider`).

### Pitfall 4: `groups` claim contains GUIDs, the map contains names
**What goes wrong:** `config/reviewer-tenants.yaml` keys are `tenant-alpha-reviewers` etc. Entra emits group **object IDs** by default; display names are available only for cloud-only groups assigned to the app via `cloud_displayname` with `groupMembershipClaims=ApplicationGroup`, and `sAMAccountName` only for AD-synced groups [CITED: learn.microsoft.com/entra/identity/hybrid/connect/how-to-connect-fed-group-claims]. A name-keyed map would map every real user to zero tenants.
**How to avoid:** Production map keyed by object ID (the file is source-controlled and change-managed, so a per-environment file or both keys is a planning decision); tests keep names via the fixtures and mock claims. Surface this in Open Questions (Q-A).

### Pitfall 5: Overage and cookie size
**What goes wrong:** >200 groups -> no `groups` claim, `_claim_names`/`hasgroups` present [CITED]. Separately, copying many groups into the cookie can exceed the ~4 KB cookie limit (chunking is automatic but large).
**How to avoid:** Set `groupMembershipClaims=ApplicationGroup` (only groups assigned to the app; user must be a direct member, no nesting) [CITED], keep only the group values that match the tenant map or the role allowlist when building the cookie principal (the mapper can drop everything else), and fail closed on overage as decided (D-08).

### Pitfall 6: CSP `form-action` blocks the IdP redirect
**What goes wrong:** A CSP with `form-action 'self'` can cause Chrome to block the 302 that follows a form POST when the target is the IdP (decide re-challenge, sign-out). Firefox historically differs.
**How to avoid:** Use `default-src 'self'; frame-ancestors 'none'; base-uri 'self'` and do not set `form-action` (or add the IdP origin). No headless-browser test exists to catch this, so record it as a manual UAT step. [ASSUMED] A6.

### Pitfall 7: Secure cookies in WebApplicationFactory
**What goes wrong:** `HttpClient` + `CookieContainer` ignore `Secure` cookies on `http://` base addresses, so sign-in "works" but the session cookie is never replayed.
**How to avoid:** Create test clients with `BaseAddress = new Uri("https://localhost")`; keep `SecurePolicy=Always` in all environments so the test exercises production behaviour.

### Pitfall 8: Default AgentKey handler runs on every request
**What goes wrong:** `UseAuthentication` authenticates the default scheme (AgentKey) on `/review` and `/dev` too, calling the introspector for an agent key sent there and adding avoidable calls to the matrix.
**How to avoid:** Pattern 3 (return `NoResult` outside the agent route groups).

### Pitfall 9: Token/rationale leakage in logs and HTML
**What goes wrong:** Handler debug/trace logs, exception pages, flash/TempData, error pages echoing `error_description`.
**How to avoid:** Static pages for remote failure; no `ProtocolMessage` logging; extend `SentinelScan` to cover UI HTML and captured logs for (a) a rationale sentinel, (b) the mock's id_token/code strings, (c) client secret. Existing `InMemoryLogCapture` records at Trace level.

### Pitfall 10: Double decide / refresh on the resume page
**What goes wrong:** Reloading the resume URL re-applies the draft or leaks it to a second user.
**How to avoid:** Draft is single-use (removed on read), bound to `oid`, TTL 10 min, never serialised; key is 32 random bytes (Base64Url).

### Pitfall 11: Postgres store `DecideAsync` unchanged, but Phase 6 execution runs in the same request
The decide POST now triggers simulated execution after commit (Phase 6 D-01). A page handler that catches exceptions broadly could show success while execution failed. Surface only the `DecisionResult`; execution outcomes appear on the audit page from the ledger.

## Code Examples

### Re-challenge on a stale session
```csharp
// Source: ASP.NET Core OpenIdConnectHandler reads Prompt and MaxAge from AuthenticationProperties (verified in source)
var props = new AuthenticationProperties { RedirectUri = $"/review/items/{id}?resume={draftId}" };
props.SetParameter(OpenIdConnectParameterNames.Prompt, "login");
props.SetParameter(OpenIdConnectParameterNames.MaxAge, TimeSpan.Zero);
props.Items["olive.reauth"] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
return Challenge(props, "Entra");
```

### Evidence check in OnTokenValidated
```csharp
// Fail closed; static outcome, no claim values in the message.
if (ctx.Properties!.Items.TryGetValue("olive.reauth", out var issued))
{
    var authTime = ctx.Principal!.FindFirst("auth_time") is { } c && long.TryParse(c.Value, out var t) ? t : 0;
    if (authTime < long.Parse(issued!, CultureInfo.InvariantCulture) - 60) { ctx.Fail("reauth_evidence_missing"); return Task.CompletedTask; }
}
var mapped = EntraClaimsMapper.Map(ctx.Principal!, tenantMap, roleAllowlist);   // pure
ctx.Principal = ReviewerClaims.ToPrincipal(mapped.Identity, mapped.TenantId, QueueStates.Entra, ReviewerCookie.Scheme)
                    .WithClaims(("olive:auth_time", authTime), ("olive:session_start", now), ("olive:no_tenant_reason", mapped.Reason));
```
`ctx.Fail(string)` surfaces through `OnAuthenticationFailed`/`OnRemoteFailure`; keep that handler static (Pattern 1).

### mock-oauth2-server container (tests)
```csharp
// Source: navikt/mock-oauth2-server README (JSON_CONFIG, interactiveLogin, issuer id = first path segment)
var container = new ContainerBuilder()
    .WithImage("ghcr.io/navikt/mock-oauth2-server@sha256:2a813381bf282da46ff36ff386059e7903e25865056af8149f4fb3f1441916b8")
    .WithPortBinding(8080, true)
    .WithEnvironment("JSON_CONFIG", Json)   // {"interactiveLogin":false,"tokenCallbacks":[{"issuerId":"role-present","requestMappings":[{"requestParam":"grant_type","match":"authorization_code","claims":{"sub":"...","oid":"...","tid":"...","groups":["..."],"roles":["case_supervisor"]}}]}, ...]}
    .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/role-present/.well-known/openid-configuration")))
    .Build();
var authority = $"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}/role-present";
```
Confirm the exact `requestMappings` field names against the README when writing the config (README excerpt used here shows `requestParam`, `match`, `claims`).

### Matrix fixture shape (UI-07)
```csharp
// 6 credentials x 5 route groups; expected status/location per cell in one table; unlisted pairings must be refused.
public static TheoryData<Cred, Route, Expected> Cells => ...;
// Expected examples: (DevCookie, /v1/queue/items) -> 401 reviewer_unauthenticated; (AgentKey, /review) -> 302 sign-in;
// (AgentKey, /v1/queue/items) -> 403 principal_not_permitted; (OidcCookie, /dev/sign-in) -> 404 in prod-like ci? see Open Question Q-D.
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `System.IdentityModel.Tokens.Jwt` validator in OIDC handler | `JsonWebTokenHandler` by default (`UseSecurityTokenValidator=false`) | ASP.NET Core 8 | `MapInboundClaims` property on `OpenIdConnectOptions` controls mapping; old `SecurityTokenValidator` is obsolete [VERIFIED: `OpenIdConnectOptions.cs`] |
| Implicit `id_token` + `form_post` | Code flow + PKCE | OAuth 2.1 direction | Allows Lax cookies, no implicit-grant setting in Entra |
| Groups in token for authorization | App roles for authorization, groups only for scoping | Microsoft guidance | Smaller tokens, avoids overage [CITED: how-to-connect-fed-group-claims] |

**Deprecated/outdated:** `SecurityTokenValidator` on `OpenIdConnectOptions` (obsolete); relying on `common` authority for a single-tenant internal app.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Entra, when sent `prompt=login` (+ `max_age=0`), returns an ID token with `auth_time` (once the optional claim is enabled) equal to the fresh credential time; and tolerates the `max_age` parameter | Pattern 5 | APR-03 cannot be proven live; fall back to a different evidence rule (e.g. `iat` + `prompt=login` provenance) or a short-lived-cookie variant (the Q15 alternative). Needs a real-tenant check |
| A2 | A new interactive sign-in reflects a removed app-role assignment within seconds to a few minutes | Pattern 5 | Reviewer could still decide for a short window after removal; document the propagation delay |
| A3 | Entra app role `value` may contain `:` (so `role:case_supervisor` works verbatim) | Pattern 2 | Mapper option (b) (map `case_supervisor` -> `role:case_supervisor`) avoids dependency; use (b) by default |
| A4 | Browsers accept `Secure` cookies from `http://localhost` | Pitfall 1 | Local dev needs a dev certificate (`dotnet dev-certs https`) |
| A5 | mock-oauth2-server cannot vary claims between two token requests for the same issuer/user, so "role removed after claim" is tested by seeding a cookie principal (real mapper output) and letting the real handler re-challenge against a role-less issuer | Pattern 8 | If a stateful mock exists, the test can be simplified; none found in the README |
| A6 | Chrome applies CSP `form-action` to redirect targets after a form POST | Pitfall 6 | If wrong, a stricter CSP is safe; omitting `form-action` costs little |
| A7 | The `groups` and `roles` values from Entra arrive as separate string claims per value (not one JSON array string) under `JsonWebTokenHandler` with `MapInboundClaims=false` | Pattern 2 | Mapper unit tests with a real handler-produced principal will catch this; handle both |

## Open Questions

1. **Entra live behaviour of `max_age` / `auth_time` (Q-B).**
   - Known: docs list `prompt=login`, not `max_age`; `auth_time` is an optional claim.
   - Unclear: actual behaviour in the team's tenant.
   - Recommendation: planner adds a `checkpoint:human-verify` (external, does not block phase completion): with a test app registration, call authorize with `prompt=login&max_age=0` and confirm a credential prompt and an `auth_time` in the token. If `auth_time` is absent, adopt the fallback (treat the callback itself as evidence only when `prompt=login` was sent and `iat` is within 60 s).
2. **Q-A: group identity for the production map.** Names in `reviewer-tenants.yaml` vs object IDs in tokens. Recommendation: production file keyed by group object ID (or tenants as app roles), decided with whoever names the `role:case_supervisor` group (already an external dependency).
3. **Q-C: audit page visibility of the four hashed fields.** UI-05 wants the fields shown; D-12/Phase 5 D-19 gate parameters/justification to the required role. Recommendation: show field values only to holders of the kind's `required_role` (reuse `GetPayloadAsync`), show hashes and the match indicator to every tenant reviewer.
4. **Q-D: `/dev/sign-in` in the matrix.** Registered under `ci`, absent in `production`. Recommendation: run the matrix twice (host `ci`: picker admits only anonymous GET; production-config unit test asserts the route/scheme is not registered), since a production host cannot currently start with fixtures registered.
5. **Q-E: freshness for stub sessions.** D-10 says re-read fixture; stub `auth_time` is irrelevant. Recommendation: stub principal carries no window check.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.400 (`global.json` rollForward latestFeature) | — |
| Docker | Postgres + mock-oauth2-server Testcontainers | yes | 29.7.2 | CI runner has Docker (ubuntu-latest) |
| ghcr.io access (pull mock-oauth2-server) | UI-07 | yes (manifest inspected) | 6.1.3 | Pre-pull in CI; fail the test (not skip) if unavailable |
| nuget.org | new package restore | yes | — | — |
| Entra tenant + app registration + `role:case_supervisor` group | live approvals | no (external dependency, per phase notes) | — | Stub + mock-oauth2-server cover phase completion |

**Missing dependencies with no fallback:** none for phase completion.
**Missing dependencies with fallback:** live Entra tenant (mock server and stub).

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 + Microsoft.AspNetCore.Mvc.Testing 10.0.13 + Testcontainers 4.16.0 |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (no separate config) |
| Quick run command | `dotnet test -c Release --filter "Category!=Integration"` (CI's quick job) |
| Full suite command | `dotnet test -c Release` |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| UI-01 | Real OIDC handler signs in against mock; dev picker works in ci; production config has neither DevReviewer nor picker; OIDC guard rejects missing/http config | integration + unit | `dotnet test --filter "FullyQualifiedName~ReviewerOidc"` | Wave 0 |
| UI-01 | `EntraClaimsMapper`: oid/tid, role allowlist, `role:` group filtering, overage, zero/two tenants | unit | `--filter "FullyQualifiedName~EntraClaimsMapper"` | Wave 0 |
| UI-02 | List shows own tenant's open items only, oldest first, cross-tenant invisible | integration (Postgres) | `--filter "FullyQualifiedName~ReviewerPagesList"` | Wave 0 |
| UI-03 | Claim/release POST requires antiforgery (400 without token), PRG redirect, flash static | integration | `--filter "FullyQualifiedName~ReviewerPagesPost"` | Wave 0 |
| UI-04 | Approve/reject requires rationale; success triggers Phase 6 execution; conflict reasons shown by code | integration | `--filter "FullyQualifiedName~ReviewerPagesDecide"` | Wave 0 |
| APR-03 | Stub: role removed from fixture after claim -> 403 `role_not_held`, item still claimed, no `queue_item_event` | integration | `--filter "FullyQualifiedName~DecisionTimeRole"` | Wave 0 |
| APR-03 | Mock: stale/absent `auth_time` -> re-challenge with `prompt=login&max_age=0`; draft restored; fresh token without role -> refused; stale evidence on re-auth -> refused | integration (container) | `--filter "FullyQualifiedName~OidcReauth"` | Wave 0 |
| UI-05 | Audit page shows four fields (role-gated), both hashes, recompute indicator (also detects mismatch), fixture banner, chain row | integration | `--filter "FullyQualifiedName~AuditPage"` | Wave 0 |
| UI-06 | Only the three page types + sign-in/out exist; no bulk/dashboard routes | architecture | `--filter "FullyQualifiedName~UiSurface"` | Wave 0 |
| UI-07 | 6 credentials x 5 route groups table, only intended pairings admitted | integration (container) | `--filter "FullyQualifiedName~CredentialRouteMatrix"` | Wave 0 |
| Cross-cutting | Absolute session cap, sliding idle (ManualTimeProvider); sentinel scan over HTML + logs (rationale, tokens, secret) | integration | `--filter "FullyQualifiedName~ReviewerSession|SentinelUi"` | Wave 0 |
| Architecture | Package allowlist updated; no `System.Net.Http` reference in Olive.Host dll (REQ_ACT_04) | architecture | `--filter "FullyQualifiedName~ArchitectureTests"` | exists (update allowlist) |

### Sampling Rate
- **Per task commit:** the relevant `--filter` above (< 30 s except container tests).
- **Per wave merge:** `dotnet test -c Release`.
- **Phase gate:** full suite green plus `dotnet list package --vulnerable --include-transitive` gate before `/gsd-verify-work`.

### Wave 0 Gaps
- [ ] `tests/Olive.Tests/MockOidcFixture.cs` - container lifetime, `JSON_CONFIG`, scenario authorities
- [ ] `tests/Olive.Tests/OidcFlowDriver.cs` - manual redirect/cookie driver (https base address)
- [ ] `OliveApiFactory` overload for OIDC config + HTTPS base address
- [ ] Test files per requirement rows above; `EntraClaimsMapperTests`
- [ ] `Testcontainers` package reference/version in tests; allowlist change in `ArchitectureTests`
- [ ] Manual UAT list: real Entra `prompt=login`/`auth_time` check, CSP + IdP redirect in a browser

## Security Domain

### Applicable ASVS Categories (level 1, enforcement on)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes | OpenIdConnect handler (code + PKCE + nonce), tenant-specific authority, confidential client secret in a redacting type; re-authentication for the sensitive decide operation |
| V3 Session Management | yes | `__Host-` cookie, HttpOnly, Secure, SameSite=Lax, 30 min sliding, 8 h absolute via claim, sign-out clears cookie + IdP session, persisted data-protection keys |
| V4 Access Control | yes | Per-route-group auth policies with explicit schemes; decide-time role via `QueueService`; tenant predicate + forced RLS preserved on the new audit read |
| V5 Input Validation | yes | Rationale length/NUL checks already in `QueueService`; route `{id:guid}`; Razor encoding, no `Html.Raw` |
| V6 Cryptography | yes | ASP.NET data protection for cookies/state; SHA-256 via existing `Jcs`; never hand-roll |
| V7 Error handling & logging | yes | Static reason codes; sentinel scan over HTML and logs |
| V13/V14 Config | yes | Startup guard for OIDC config; CSP and security headers |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| CSRF on claim/release/decide/sign-out | Tampering | Antiforgery on every POST; cookie SameSite=Lax; cookie never accepted on `/v1/*` |
| Login CSRF / token replay | Spoofing | OIDC state, nonce, correlation cookie, PKCE |
| Stale-role decision (privilege retained after removal) | Elevation of privilege | Decide-time fresh token (`prompt=login`, `auth_time` evidence) or fixture re-read; fail closed |
| Role injection via group display name | Elevation of privilege | Drop `role:`-prefixed group values; role only from allowlisted `roles` claim |
| Tenant confusion / spoofed tenant | Spoofing | Tenant only from server-side map; ignore header/form/query; require `tid` == configured tenant |
| Open redirect via `returnUrl` | Spoofing | Accept only local relative URLs (`Url.IsLocalUrl`) |
| Token / rationale leakage | Information disclosure | No `SaveTokens`; static errors; sentinel tests; redacting secret type |
| Clickjacking / XSS | Tampering | `frame-ancestors 'none'`, CSP `default-src 'self'`, Razor encoding |
| Draft replay or hijack across the round-trip | Tampering | Single-use, oid-bound, TTL, random 256-bit key, never in URL beyond the opaque key |
| Dev bypass in production | Elevation of privilege | Conditional registration + existing guards + new OIDC guard |

## Sources

### Primary (HIGH confidence)
- dotnet/aspnetcore `OpenIdConnectOptions.cs` and `OpenIdConnectHandler.cs` (read via GitHub API): defaults `ResponseType=IdToken`, `ResponseMode=FormPost`, nonce cookie `SameSite=None`, `UsePkce=true`, `MapInboundClaims`, `MaxAge`/`Prompt` read from `AuthenticationProperties`.
- AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet `OpenIdConnectProtocolValidator.cs`: `RequireAuthTime = false` by default.
- navikt/mock-oauth2-server: README (JSON_CONFIG, `interactiveLogin`, issuer id from first path segment, image tags), `AuthorizationCodeHandler.kt` / `OAuth2TokenProvider.kt` / `IdTokenRequiredClaimsIntegrationTest.kt` (`auth_time` only with `max_age`, claim override order); release 6.1.3 (2026-10-23); image digest via `docker buildx imagetools inspect`.
- Repo files read: `src/Olive.Host/Program.cs`, `Auth/*`, `Startup/StartupGuards.cs`, `Application/QueueService.cs`, `ReviewerContext.cs`, `IReviewerIdentitySource.cs`, `IReviewerTenantMap.cs`, `IProposalStore.cs`, `Domain/Jcs/Jcs.cs`, `sql/migrations/0009_execution_ledger.sql`, `config/*.yaml`, `tests/Olive.Tests/ArchitectureTests.cs`, `OliveApiFactory.cs`, `Directory.Packages.props`, `BannedSymbols.txt`, `.github/workflows/ci.yml`.

### Secondary (MEDIUM confidence)
- https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc (authorize parameters; `max_age` absent from the list)
- https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference (claims, overage, `oid`/`tid`, no default `auth_time`)
- https://learn.microsoft.com/en-us/entra/identity-platform/optional-claims-reference (`auth_time` optional claim)
- https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/how-to-connect-fed-group-claims (group formats, `ApplicationGroup`, limits 200/150, app roles recommendation)
- https://learn.microsoft.com/en-us/security/zero-trust/develop/configure-tokens-group-claims-app-roles (group-to-role assignment needs P1; claims current at issuance; surfaced via search, not fetched in full)
- https://auth0.com/docs/authenticate/login/max-age-reauthentication (why `auth_time` is the verifiable evidence; search summary)

### Tertiary (LOW confidence)
- A Microsoft Q&A thread reporting odd token times with `max_age=0` on Entra (single user report, https://learn.microsoft.com/en-us/answers/questions/1377508/oauth-tokens-endpoint-issues-expired-token); motivates A1 and the real-tenant check.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - one first-party package, versions confirmed on NuGet.
- Architecture: HIGH for ASP.NET mechanics and repo integration; MEDIUM for the Entra freshness evidence (A1).
- Pitfalls: MEDIUM-HIGH - handler defaults and mock behaviour verified in source; CSP/browser behaviours assumed.

**Research date:** 2026-10-24
**Valid until:** 2026-11-24 (mock-oauth2-server and ASP.NET patch releases move quickly; re-check the digest at plan time)
