# Phase 7: Reviewer UI and Entra OIDC - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md. This log preserves the alternatives considered.

**Date:** 2026-10-24
**Phase:** 07-reviewer-ui-and-entra-oidc
**Areas discussed:** Sign-in and session model, Role freshness at decide, Decide and audit page content, Credential × route matrix

---

## Sign-in and session model

| Question | Options | Selected |
|----------|---------|----------|
| Session holding | Cookie + OIDC challenge / Re-validate tokens per request | Cookie + OIDC challenge |
| Dev browser sign-in | Fixture picker page / Reuse header scheme / mock-oauth2-server only | Fixture picker page |
| Lifetime | Short sliding (~30 min, ~8 h cap) / IdP token lifetime / You decide | Short sliding |
| Zero or several tenants | Signed in, 403 page / Refuse at sign-in | Signed in, 403 page |

## Role freshness at decide

| Question | Options | Selected |
|----------|---------|----------|
| Proving the role at decide (Q15) | max_age re-challenge / Short-lived cookie re-validated / Both | max_age re-challenge |
| Role claim source | App role `roles` claim / groups claim for both | App role `roles` claim |
| Groups overage | Fail closed, no tenant / Move tenant to app roles | Fail closed |
| Role missing at decide | 403 page, claim kept / 403 + auto-release | 403 page, claim kept |
| Freshness window | 5 min / 0 (always) / 15 min | 5 min |
| Dev-path decide check | Re-read fixture at decide / Cookie claims only | Re-read fixture |

## Decide and audit page content

| Question | Options | Selected |
|----------|---------|----------|
| Page layout | List + item + audit / List + single item page | List + item + audit |
| Payload display | Rendered HTML-encoded fields / Raw canonical JSON / Both | Rendered fields |
| Hashes | Show and recompute / Show stored only | Show and recompute |
| Errors | PRG + flash message / Dedicated error page | PRG + flash |
| Execution outcome on audit page | Yes, from execution_chain / Queue history only | Yes, from execution_chain |
| Styling | Plain HTML + tiny CSS / Bootstrap template | Plain HTML + tiny CSS |

## Credential × route matrix

| Question | Options | Selected |
|----------|---------|----------|
| JSON reviewer API credentials | DevReviewer header only / Also Entra bearer / Also UI cookie | DevReviewer header only |
| UI-07 route groups | Five groups / UI vs API only | Five groups |
| Mock IdP in CI | Testcontainers + scripted flow / Playwright | Testcontainers + scripted flow |
| Production OIDC config | Env vars + startup guard / You decide | Env vars + startup guard |

## Claude's Discretion

Cookie hardening (SameSite, HTTPS in local dev, data-protection keys), the mechanism that carries the rationale draft through the max_age round-trip, the claim-mapper location, how freshness reaches QueueService, the Entra claim types, page routes and the flash mechanism, new reason codes, and where the UI project lives.

## Deferred Ideas

- Production reviewer JSON API with Entra JwtBearer
- Teams notifications (P2-04)
- Auto-release on a role-missing refusal
