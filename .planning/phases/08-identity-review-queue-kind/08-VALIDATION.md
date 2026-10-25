---
phase: 8
slug: identity-review-queue-kind
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-25
---

# Phase 8 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.0 + Testcontainers.PostgreSql + Microsoft.AspNetCore.Mvc.Testing |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Unit|Integration|Architecture`, `Demo=...`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category=Unit|Category=Architecture"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Phase-focused command** | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~IdentityReview|FullyQualifiedName~ServiceToken|FullyQualifiedName~ProducerRegistry|FullyQualifiedName~CredentialRouteMatrix|FullyQualifiedName~PrivilegeMatrix|FullyQualifiedName~MigrationRunner"` |
| **Estimated runtime** | ~60s quick, several minutes full |

---

## Sampling Rate

- **After every task commit:** Run the quick command plus the one focused integration class for the task
- **After every plan wave:** Run the phase-focused command
- **Before `/gsd-verify-work`:** Full suite green plus `bash build/demo-identity-review.sh`
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

Filled in by the planner and executor from the plan tasks. Requirement-to-test map (from 08-RESEARCH.md §Validation Architecture):

| Requirement | Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|----------|-----------|-------------------|-------------|--------|
| IRQ-01 / TC-IRQ-01 | inline payload or record value → 400 `invalid_request`, no row | integration | `--filter "FullyQualifiedName~IRQ_01"` | ❌ W0 | ⬜ pending |
| IRQ-01 / TC-IRQ-02 | identical body replay 200; different body 409 `idempotency_conflict` | integration + unit | `--filter "FullyQualifiedName~IRQ_02"` | ❌ W0 | ⬜ pending |
| IRQ-01 / TC-IRQ-03 | server-assigned fields → 400 `server_assigned_field`, no echo | integration | `--filter "FullyQualifiedName~IRQ_03"` | ❌ W0 | ⬜ pending |
| IRQ-01 / TC-IRQ-11 | `link_challenge` → 400 `invalid_request` | integration | `--filter "FullyQualifiedName~IRQ_11"` | ❌ W0 | ⬜ pending |
| IRQ-02 / TC-IRQ-04 | non-steward refused on identity; steward refused on action item | integration | `--filter "FullyQualifiedName~IRQ_04"` | ❌ W0 | ⬜ pending |
| IRQ-02 / TC-IRQ-05 | rationale required incl. defer | unit + integration | `--filter "FullyQualifiedName~IRQ_05"` | ❌ W0 | ⬜ pending |
| IRQ-02 / TC-IRQ-06 | defer → open, claim cleared, history; accept → decided | integration | `--filter "FullyQualifiedName~IRQ_06"` | ❌ W0 | ⬜ pending |
| IRQ-03 / TC-IRQ-09 | withdraw open/claimed; decided → 409; foreign → 404; bad reason → 400 | integration | `--filter "FullyQualifiedName~IRQ_09"` | ❌ W0 | ⬜ pending |
| IRQ-03 / TC-IRQ-10 | poll tenant + producer scoped; RLS ablation | integration | `--filter "FullyQualifiedName~IRQ_10"` | ❌ W0 | ⬜ pending |
| IRQ-03 | cursor ordering, page of 100, malformed cursor 400, no rationale | unit + integration | `--filter "FullyQualifiedName~IdentityReviewPoll|FullyQualifiedName~ProducerCursor"` | ❌ W0 | ⬜ pending |
| IRQ-04 | stub verifier states, 503 fail-closed, token never logged/stored | unit + integration | `--filter "FullyQualifiedName~ServiceToken"` | ❌ W0 | ⬜ pending |
| IRQ-04 | `producers.yaml` strict + startup cross-check; unknown client/tenant 403 | unit + integration | `--filter "FullyQualifiedName~ProducerRegistry"` | ❌ W0 | ⬜ pending |
| IRQ-04 / FND-05 | production refuses verifier stub | unit | `--filter "FullyQualifiedName~StartupGuard"` | ✅ extend | ⬜ pending |
| IDN-03 | credential/route matrix incl. service token row and producer columns | integration | `--filter "FullyQualifiedName~CredentialRouteMatrix"` | ✅ extend | ⬜ pending |
| IDN-03 / IDN-08 | inbound token type only on popeye ports | architecture | `--filter "Category=Architecture"` | ✅ extend | ⬜ pending |
| Migration 0010 | applies, checksum, CHECKs, grants, `deferred` event | integration | `--filter "FullyQualifiedName~MigrationRunner|FullyQualifiedName~PrivilegeMatrix|FullyQualifiedName~QueueTable"` | ✅ extend | ⬜ pending |
| IRQ-02 pages | kind badge, refs-only identity page, kind-driven buttons, defer history | integration (HTML) | `--filter "FullyQualifiedName~ReviewerPages|FullyQualifiedName~IdentityReviewPages|FullyQualifiedName~UiSurface|FullyQualifiedName~SentinelUi"` | partial | ⬜ pending |
| Contracts | new schemas with valid/invalid pairs; IR-1 SHA-256 pinned | unit | `--filter "FullyQualifiedName~ContractSchema|FullyQualifiedName~QueueSchema"` | ✅ extend | ⬜ pending |
| D-18 demo | scripted demo + transcript with no rationale/record values | integration | `bash build/demo-identity-review.sh` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `tests/Olive.Tests/IdentityReviewCreateTests.cs`, `IdentityReviewDecisionTests.cs`, `IdentityReviewPollWithdrawTests.cs`, `IdentityReviewPagesTests.cs`
- [ ] `ServiceTokenVerifierTests.cs`, `ServiceTokenAuthTests.cs`, `ProducerRegistryTests.cs`, `ProducerCursorTests.cs`
- [ ] `IdentityReviewDemoTests.cs` + `build/demo-identity-review.sh`
- [ ] Helpers: `OliveApiFactory.CreateServiceClient`, `RecordingServiceTokenVerifier`, faulting double, identity-item seeding helper
- [ ] Updates: `PrivilegeMatrixTests`, `MigrationRunnerTests`, `ReviewerSessionTests`, `QueueServiceDecideTests`, `IQueueStore` fakes, `CredentialRouteMatrixTests`, `ContractSchemaTests.Folders`, `ArchitectureTests`, `StartupGuardTests`

No framework install needed.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live bluto traffic stays disabled | IRQ-04 | External gate (IR-1 §8: popeye registry entry, Entra steward group) | Confirm no production verifier registration and no named steward group in config |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 120s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
