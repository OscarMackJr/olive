---
phase: 4
slug: policy-expiry-and-refusal
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-22
---

# Phase 4 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0 (postgres:17-alpine), Mvc.Testing |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration`, `Category=Architecture`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Estimated runtime** | ~30 seconds (full, with Testcontainers; ~750 tests baseline) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet restore --locked-mode`
- **Max feedback latency:** 90 seconds

---

## Per-Task Verification Map

| Req ID | Plans | Behavior | Test Type | Automated Command | File Exists | Status |
|--------|-------|----------|-----------|-------------------|-------------|--------|
| POL-01 | TBD | Wrong purpose -> stored `purpose_not_permitted`, never queue-eligible; unknown action -> stored `not_in_catalog`, no trace/resolver call | unit + integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~POL_01"` | ❌ W0 | ⬜ pending |
| POL-02 | TBD | Intersection matrix (user only / agent only / both / neither / null / empty); unknown user refused; resolver fault -> 503 nothing stored; credential rejection alarm | unit + integration | `--filter "FullyQualifiedName~POL_02"` | ❌ W0 | ⬜ pending |
| POL-03 | TBD | Refusals do not consume limit; limit+1 -> `rate_limited`; N parallel above limit accepts exactly `limit`; window slides; per-tenant/per-agent scope | integration | `--filter "FullyQualifiedName~POL_03"` | ❌ W0 | ⬜ pending |
| POL-04 | TBD | `policy_evaluated` event with outcome/reasons/checks for allowed, each refusal, `not_in_catalog`; no parameter values or groups in detail (sentinel scan) | integration | `--filter "FullyQualifiedName~POL_04"` | ❌ W0 | ⬜ pending |
| POL-05 | TBD | Replay of stored refusal under same key -> same refusal, evaluator/resolver call counts and event count unchanged; different body -> 409 | integration | `--filter "FullyQualifiedName~POL_05"` | ❌ W0 | ⬜ pending |
| POL-06 | TBD | Full transition matrix; CAS fails on stale `from`; each success writes exactly one `proposal_event`; illegal transitions rejected | unit + integration | `--filter "FullyQualifiedName~POL_06"` | ❌ W0 | ⬜ pending |
| POL-07 | TBD | Default 8h; shorten; <=0 or >max -> 400 nothing stored; key-expiry cap; absolute `expires_at` (unchanged after clock advance); GET exposes it | integration (ManualTimeProvider) | `--filter "FullyQualifiedName~POL_07"` | ❌ W0 | ⬜ pending |
| API-08 | TBD | Multi-fault request peeled one fault at a time; recorder asserts call order and absence of later calls | integration | `--filter "FullyQualifiedName~API_08"` | ❌ W0 | ⬜ pending |
| Schema/migration | TBD | Catalog schema new fields; proposal `expires_in_seconds` valid/invalid; migration 0007 privilege matrix and checksum | contract + integration | `--filter "FullyQualifiedName~ContractSchema\|PrivilegeMatrix\|MigrationRunner"` | extend existing | ⬜ pending |
| Guards | TBD | Production + `FixtureDirectoryGroupResolver` refuses start (incl. behind decorator); architecture tests accept new outbound destination | integration/arch | `--filter "FullyQualifiedName~StartupGuard\|Architecture"` | extend existing | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `PolicyEvaluatorTests.cs`, `PolicyEndpointTests.cs`, `RateLimitTests.cs`, `ExpiryTests.cs`, `LifecycleTransitionTests.cs`, `SubmissionOrderTests.cs`
- [ ] `FaultingDirectoryResolver` + `RecordingDirectoryResolver` and `OliveApiFactory` wiring (default fixtures granting liam/alpha/beta the entitlement so legacy tests stay meaningful)
- [ ] Test catalog with high rate limit for legacy API tests, and a small-window variant
- [ ] Rewrite `API_05_unknown_action_type_gives_non_stored_400_not_in_catalog` and the `ApiProposalTests`/`SchemaValidatorTests` `CatalogEntry` constructions
- [ ] Contract examples for changed proposal request/response, catalog entry, directory fixtures
- [ ] No framework install needed

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| D§4 stop rule: entitlement source exists in POPEYE_v0.4.2 (and v0.4.3 §2.5 still assigns group resolution to Olive) or vocabulary §3.2 | POL-02 (Q4) | Spec review, not code | Confirm the cited section and record it in STATE.md / CONTEXT before executing POL-02 work |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 90s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
