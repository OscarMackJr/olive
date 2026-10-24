---
phase: 07-reviewer-ui-and-entra-oidc
plan: 04
subsystem: auth
tags: [oidc, entra, openidconnect, pkce, mock-oauth2-server, testcontainers, claims-mapping]
requires: [07-01, 07-02, 07-03]
provides:
  - Real OpenIdConnect sign-in (code flow, PKCE, query response) issuing the same reviewer cookie as the dev picker
  - EntraClaimsMapper and RoleAllowlist (registry-derived)
  - OidcClientSecret redacting type, OidcConfig, StartupGuards.CheckOidcConfiguration (UI-01)
  - /auth/sign-in, /auth/failed, IdP-aware sign-out
  - MockOidcFixture (per-class, digest pinned), OidcFlowDriver, OliveApiFactory mockOidc option
affects: [07-05, 07-06]
key-files:
  created:
    - src/Olive.Domain/OidcClientSecret.cs
    - src/Olive.Integrations.Entra/EntraClaimsMapper.cs
    - src/Olive.Host/Startup/OidcConfig.cs
    - src/Olive.Host/Auth/ReviewerOidc.cs
    - src/Olive.Host/Pages/Account/SignIn.cshtml(.cs)
    - src/Olive.Host/Pages/Account/Failed.cshtml(.cs)
    - tests/Olive.Tests/EntraClaimsMapperTests.cs
    - tests/Olive.Tests/OidcConfigGuardTests.cs
    - tests/Olive.Tests/MockOidcFixture.cs
    - tests/Olive.Tests/OidcFlowDriver.cs
    - tests/Olive.Tests/ReviewerOidcTests.cs
  modified:
    - Directory.Packages.props, src/Olive.Host/Olive.Host.csproj, tests/Olive.Tests/Olive.Tests.csproj, both packages.lock.json
    - src/Olive.Host/Program.cs, Auth/ReviewerCookie.cs, Startup/StartupGuards.cs, StartupGuardFilter.cs, SecurityHeaders.cs
    - src/Olive.Host/Pages/Account/SignOut.cshtml.cs, SignedOut.cshtml
    - tests/Olive.Tests/OliveApiFactory.cs, ArchitectureTests.cs, SecretTypeTests.cs, SourceHygieneTests.cs, StartupGuardTests.cs
    - .env.example
requirements-completed: [UI-01]
completed: 2026-10-24
---

# Phase 7 Plan 04: Entra OIDC sign-in

A reviewer can sign in through the real OpenIdConnect handler against a digest-pinned mock-oauth2-server and receive the same `__Host-olive-reviewer` cookie as the dev picker, with `auth_source = entra`, oid, tid, groups, allowlisted role and the Q13-mapped tenant.

## Commits
- d811f9e: OIDC package (first-party, allowlisted, locked), OidcClientSecret, EntraClaimsMapper + RoleAllowlist, unit tests
- d94bba3: OidcConfig, CheckOidcConfiguration, AddReviewerOidc, Account pages, .env.example, guard tests
- daee058: MockOidcFixture, OidcFlowDriver, factory support, ReviewerOidcTests, logging and hygiene fixes

## Verification
Full `dotnet test -c Release`: 1325 passed, 0 failed, 0 skipped. `dotnet restore --locked-mode` clean; `dotnet list package --vulnerable --include-transitive` reports nothing. ReviewerOidc covers 12 cases (sign-in, role absent, auth_time carried, sentinel scan, overage, no-tenant and two-tenant, tampered state, sign-out, open-redirect, no prompt on plain sign-in, http authority in ci). Mapper has 15 unit cases; guard 14.

Q-A resolved as planned: group claim values are opaque strings matched against `config/reviewer-tenants.yaml` as-is; no code change.

## Deviations from Plan

**1. [Rule 1 - Bug] response_mode=query is not on the authorize URL**
- The OpenIdConnect handler omits `response_mode` when it equals the default for the code flow, so the plan's assertion cannot hold. The driver asserts it is absent or `query`; the callback arriving as a GET with `code` in the query proves the mode.

**2. [Rule 2 - Security] Authorization code appeared in logs**
- The OpenIdConnectHandler logs the raw callback message (`MessageReceived: '?code=...'`) at Debug, and Hosting logs the query string at Information. Added log filters in Program.cs: `Microsoft.AspNetCore.Hosting.Diagnostics` at Warning and `...OpenIdConnectHandler` at Information (same mechanism as the existing ExceptionHandlerMiddleware filter). The sentinel test now proves the code, id_token, access_token and client secret are absent from logs and cookies.

**3. [Rule 3 - Blocking] Existing tests**
- Production-mode StartupGuardTests would now fail with UI-01 (no OIDC settings). OliveApiFactory gives `environment: "production"` a default https OIDC configuration that guard tests can override; `ProductionSafePorts` made internal for reuse.
- `SourceHygieneTests` FND_06 ExposeSecret allowlist extended with `OidcClientSecret.cs` and `Host/Auth/ReviewerOidc.cs` (the single options-delegate use, as the plan requires).
- OliveApiFactory also forwards OLIVE_ENVIRONMENT to UseSetting so Program.cs reads the environment when deciding `RequireHttpsMetadata`.

**4. Minor**
- `OidcConfig.Read` returns null (rather than throwing) when authority, client id or secret is missing; production then fails through the guard with UI-01, other environments simply have no Entra sign-in.
- `AddReviewerSession` takes the cookie login path (`/auth/sign-in` when OIDC is configured, else `/dev/sign-in`); `SignedOut` reads the same path.
- ReviewerId claim is left null for Entra sessions (oid is the reviewer key); `auth_time` claim is written only when the token carries one.
- SignOut for stub sessions keeps the original cookie-only redirect; Entra sessions use `SignOut(props, cookie, "Entra")`.
- `/signin-oidc` and `/signout-callback-oidc` added to the no-store prefixes.
- Environment: `contracts/vendored/*` CRLF drift restored with `git checkout --`, not committed. Worktree base reset to 471b1bb. Lockfile CRLF noise in unrelated projects was not committed (only the Host and Tests lockfiles changed in content).

## Known Stubs
None. `DecisionFreshness` Entra branch still fails closed until plan 05 (unchanged).

## Threat Flags
None beyond the plan's threat model (T-07-19..25 mitigated as planned).

## Self-Check: PASSED
