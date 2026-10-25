# Phase 7 Human UAT: live Entra and real-browser checks

These checks cannot run in CI because they need a real Microsoft Entra tenant and a real browser. They are external dependencies and do not block phase completion; CI already proves the same flows against mock-oauth2-server. Record pass or fail and what you observed under each item. If the tenant or group does not exist yet, mark the item `deferred: external dependency`.

Status: deferred: external dependency (user decision 2026-10-24; Entra tenant and supervisor group not yet available).

## 0. Entra prerequisites

1. App registration is single-tenant. Authority is `https://login.microsoftonline.com/<tenant-guid>/v2.0` (set as `OLIVE_OIDC_AUTHORITY`, https only, enforced by the UI-01 startup guard).
2. Redirect URI is `https://<host>/signin-oidc`. Post-logout redirect URI is `https://<host>/signout-callback-oidc`.
3. Create a confidential client secret and place it in `OLIVE_OIDC_CLIENT_SECRET` (client id in `OLIVE_OIDC_CLIENT_ID`). EVD-06 credential-audit note: this secret is a credential Olive holds; add it to the Phase 9 credential audit (EVD-06) alongside the Postgres credential. It is read-only sign-in material and grants no write access to any target system.
4. Define an app role with value `case_supervisor` (the mapper maps it to `role:case_supervisor`) and assign it to the supervisor group. Group-to-app-role assignment needs Entra ID P1 or higher.
5. Set `groupMembershipClaims` to `ApplicationGroup` in the app manifest so the token carries only groups assigned to the app.
6. Enable the optional ID-token claim `auth_time` (`optionalClaims.idToken: [{"name":"auth_time"}]`).
7. Q-A: once the reviewer tenant group is named, add its Entra group object id as a `group:` key (with its `tenant_id`) to `config/reviewer-tenants.yaml`. Group claim values are opaque strings matched as-is, so no code change is needed. The file is source-controlled and change-managed, not overridable by environment.

Expected: `https://<host>/auth/sign-in` redirects to `login.microsoftonline.com`, and after sign-in `/review` lists the open items for the mapped tenant.

## 1. Live `prompt=login` with `max_age=0` and `auth_time`

Steps:
1. Sign in as a supervisor, claim an item, and wait more than 5 minutes.
2. Open the item and submit a decision (approve or reject) with a rationale.
3. Confirm Entra shows a credential prompt (not silent single sign-on).
4. After re-authenticating, confirm you land back on the item with the draft restored and a notice that you signed in again, then submit the decision.

Expected: the credential prompt appears; the returned ID token has `auth_time` at or after the challenge; the decision succeeds on the second submit.

Record: pass/fail, whether `auth_time` was present, and whether Entra tolerated the `max_age` parameter. If `auth_time` is absent, the code fails closed (`reauth_evidence_missing`, redirect to `/auth/failed`); record the fallback decision (RESEARCH Open Question 1: for example `iat` plus `prompt=login` provenance, or the short-lived-cookie variant) rather than loosening the rule here.

## 2. Removed role assignment (A2)

Steps:
1. Sign in as a supervisor and claim an item.
2. In Entra, remove the `case_supervisor` role assignment from that user or group.
3. Wait for propagation, then submit the decision and complete the re-authentication.

Expected: the decision is refused with reason `role_not_held`, the item stays claimed, and no decision or execution is recorded.

Record: pass/fail and how long the propagation delay was before the removed role took effect (A2).

## 3. Real browser run and CSP (Pitfall 6, A6)

Steps:
1. In a real browser with the developer console open, complete sign-in, list, claim, decide with the re-challenge, and sign-out.
2. Watch the console and network tab throughout.

Expected: no CSP violation messages; the redirect to the IdP after the decide POST is not blocked (the policy deliberately has no `form-action`); the page loads only `/olive.css` from the same origin; sign-out ends at `/signed-out`.

Record: pass/fail and any console messages.

## 4. Local https host cookie (A4)

Steps:
1. Run the host over `https://localhost` (for example `dotnet dev-certs https --trust`, then run with an https profile).
2. Sign in and reload `/review`.

Expected: the `__Host-olive-reviewer` cookie is accepted (Secure, HttpOnly, SameSite=Lax, path `/`, no domain) and the session persists across reloads.

Record: pass/fail and the browser used.

## 5. Production-shaped startup

Steps:
1. Start the host with `OLIVE_ENVIRONMENT=production` and the real OIDC settings (https authority, client id, secret).
2. Observe startup.

Expected: while `DevSignInGate` is registered unconditionally in `Program.cs` (D-02 / D-18), the FND-05 startup guard refuses to start in production, so a production deployment cannot expose `/dev/sign-in`. CI proves this guard (`UI_07_production_refuses_dev_sign_in_gate_and_http_authority`). Record whether the refusal message appears and contains no secret or authority value; the production composition that removes the gate is a Phase 9 deployment concern.

Record: pass/fail.

## Results

| Item | Result (approved / failed / deferred: external dependency) | Notes |
|------|-------------------------------------------------------------|-------|
| 0 Prerequisites | | |
| 1 prompt=login, max_age, auth_time | | |
| 2 Removed role (A2) | | |
| 3 Browser CSP | | |
| 4 Local https cookie (A4) | | |
| 5 Production-shaped startup | | |
