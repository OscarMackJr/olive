---
phase: 7
slug: reviewer-ui-and-entra-oidc
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-24
---

# Phase 7 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 + Microsoft.AspNetCore.Mvc.Testing 10.0.13 + Testcontainers 4.16.0 |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (no separate config) |
| **Quick run command** | `dotnet test -c Release --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test -c Release` |
| **Estimated runtime** | ~30 seconds quick; several minutes full (containers) |

---

## Sampling Rate

- **After every task commit:** Run the relevant `--filter` for the task (see map below)
- **After every plan wave:** Run `dotnet test -c Release`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet list package --vulnerable --include-transitive`
- **Max feedback latency:** 30 seconds (non-container filters)

---

## Per-Task Verification Map

| Req ID | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|----------|-----------|-------------------|-------------|--------|
| UI-01 | Real OIDC handler signs in against mock; dev picker in ci only; production has neither DevReviewer nor picker; OIDC guard rejects missing/http config | integration + unit | `dotnet test --filter "FullyQualifiedName~ReviewerOidc"` | ❌ W0 | ⬜ pending |
| UI-01 | `EntraClaimsMapper`: oid/tid, role allowlist, `role:` group filtering, overage, zero/two tenants | unit | `dotnet test --filter "FullyQualifiedName~EntraClaimsMapper"` | ❌ W0 | ⬜ pending |
| UI-02 | List shows own tenant's open items only, oldest first, cross-tenant invisible | integration (Postgres) | `dotnet test --filter "FullyQualifiedName~ReviewerPagesList"` | ❌ W0 | ⬜ pending |
| UI-03 | Claim/release POST requires antiforgery (400 without token), PRG redirect, static flash | integration | `dotnet test --filter "FullyQualifiedName~ReviewerPagesPost"` | ❌ W0 | ⬜ pending |
| UI-04 | Approve/reject requires rationale; success triggers Phase 6 execution; conflict reasons shown by code | integration | `dotnet test --filter "FullyQualifiedName~ReviewerPagesDecide"` | ❌ W0 | ⬜ pending |
| APR-03 | Stub: role removed after claim -> 403 `role_not_held`, item still claimed, no `queue_item_event` | integration | `dotnet test --filter "FullyQualifiedName~DecisionTimeRole"` | ❌ W0 | ⬜ pending |
| APR-03 | Mock: stale/absent `auth_time` -> re-challenge `prompt=login&max_age=0`; draft restored; fresh token without role refused | integration (container) | `dotnet test --filter "FullyQualifiedName~OidcReauth"` | ❌ W0 | ⬜ pending |
| UI-05 | Audit page: four fields (role-gated), both hashes, recompute indicator, fixture banner, chain row | integration | `dotnet test --filter "FullyQualifiedName~AuditPage"` | ❌ W0 | ⬜ pending |
| UI-06 | Only the three page types + sign-in/out exist; no bulk/dashboard routes | architecture | `dotnet test --filter "FullyQualifiedName~UiSurface"` | ❌ W0 | ⬜ pending |
| UI-07 | 6 credentials x 5 route groups, only intended pairings admitted | integration (container) | `dotnet test --filter "FullyQualifiedName~CredentialRouteMatrix"` | ❌ W0 | ⬜ pending |
| Cross-cutting | Absolute session cap, sliding idle; sentinel scan over HTML + logs | integration | `dotnet test --filter "FullyQualifiedName~ReviewerSession\|FullyQualifiedName~SentinelUi"` | ❌ W0 | ⬜ pending |
| Architecture | Package allowlist updated; no `System.Net.Http` in Olive.Host (REQ_ACT_04) | architecture | `dotnet test --filter "FullyQualifiedName~ArchitectureTests"` | ✅ (update) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/Olive.Tests/MockOidcFixture.cs` — container lifetime, `JSON_CONFIG`, scenario authorities
- [ ] `tests/Olive.Tests/OidcFlowDriver.cs` — manual redirect/cookie driver (https base address)
- [ ] `OliveApiFactory` overload for OIDC config + HTTPS base address
- [ ] Test files per requirement rows above; `EntraClaimsMapperTests`
- [ ] `Testcontainers` package reference in tests; allowlist change in `ArchitectureTests`

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Real Entra `prompt=login` / `auth_time` behavior | APR-03 | Needs a live Entra tenant and the named `role:case_supervisor` group | Sign in against real Entra, claim, wait past freshness window, decide; confirm re-auth prompt and fresh `auth_time` |
| CSP + IdP redirect in a real browser | UI-01 | Browser enforcement of CSP and SameSite cookies is not exercised by HttpClient | Load pages in a browser, check console for CSP violations, complete sign-in round trip |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
