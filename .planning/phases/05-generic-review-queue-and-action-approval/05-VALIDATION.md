---
phase: 5
slug: generic-review-queue-and-action-approval
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-10-23
---

# Phase 5 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution. Source: `05-RESEARCH.md` § Validation Architecture.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql (postgres:17-alpine), Mvc.Testing |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration|Architecture|Unit`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Demo command** | `bash build/demo-beat4.sh` (`--filter "Demo=beat4"`) |
| **Estimated runtime** | ~30–60 seconds full suite |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`, plus the task's targeted `--filter "FullyQualifiedName~<REQ>"` when it touches the database
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite green, `dotnet restore --locked-mode`, `bash build/demo-beat4.sh`
- **Max feedback latency:** 60 seconds

---

## Per-Task Verification Map

Requirement-level map. Task IDs are filled in from the PLAN.md files once planning completes.

| Requirement | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|-------------|-----------------|-----------|-------------------|-------------|--------|
| QUE-01 | Inline payload / wrong or cross-kind scheme rejected (DB CHECK + v1 schema) | integration + unit | `--filter "FullyQualifiedName~QUE_01"` | ❌ W0 | ⬜ pending |
| QUE-02 | Registry fixed at startup, strict parse, no mutator | unit | `--filter "FullyQualifiedName~QUE_02"` | ❌ W0 | ⬜ pending |
| QUE-03 | Enqueue atomic with accept; none on refusal/replay | integration | `--filter "FullyQualifiedName~QUE_03"` | ❌ W0 | ⬜ pending |
| QUE-04 | Tenant-only, expired filtered, list never writes | integration | `--filter "FullyQualifiedName~QUE_04"` | ❌ W0 | ⬜ pending |
| QUE-05 | Concurrent claims → exactly one winner | integration | `--filter "FullyQualifiedName~QUE_05"` | ❌ W0 | ⬜ pending |
| QUE-06 | Claimant-only release, recorded | integration | `--filter "FullyQualifiedName~QUE_06"` | ❌ W0 | ⬜ pending |
| QUE-07 | Claimant-only decide; decided_by + non-blank rationale required | integration + unit | `--filter "FullyQualifiedName~QUE_07"` | ❌ W0 | ⬜ pending |
| QUE-08 | Same decision no-op; conflicting decision 409 | integration | `--filter "FullyQualifiedName~QUE_08"` | ❌ W0 | ⬜ pending |
| QUE-09 | Full `seq`-ordered history; event table INSERT-only | integration | `--filter "FullyQualifiedName~QUE_09"` | ❌ W0 | ⬜ pending |
| QUE-10 | Lazy withdrawal committed at claim/release/decide | integration | `--filter "FullyQualifiedName~QUE_10"` | ❌ W0 | ⬜ pending |
| APR-01 | Unauthenticated reviewer → 401 | integration | `--filter "FullyQualifiedName~APR_01"` | ❌ W0 | ⬜ pending |
| APR-02 | Self-approval / unresolved mapping refused (approve only, D-18) | integration + unit | `--filter "FullyQualifiedName~APR_02"` | ❌ W0 | ⬜ pending |
| APR-04 | Item + proposal move in one transaction | integration | `--filter "FullyQualifiedName~APR_04"` | ❌ W0 | ⬜ pending |
| APR-05 | `auth_source=stub` recorded, not overridable | integration | `--filter "FullyQualifiedName~APR_05"` | ❌ W0 | ⬜ pending |
| POL-08 | Expiry committed before `proposal_expired` returned | integration | `--filter "FullyQualifiedName~POL_08"` | ❌ W0 | ⬜ pending |
| EVD-03 | Beat 4: refused, traceable, absent from queue | integration (Demo) | `bash build/demo-beat4.sh` | ❌ W0 | ⬜ pending |
| D-02 | Caller separation matrix; no introspection on reviewer routes | integration | `--filter "FullyQualifiedName~Caller_separation"` | ❌ W0 | ⬜ pending |
| D-08 | Tenant only from server map; 0/>1 tenants → 403 | unit + integration | `--filter "FullyQualifiedName~ReviewerTenant"` | ❌ W0 | ⬜ pending |
| D-09 / D-19 | Role gate at decide and payload read | integration | `--filter "FullyQualifiedName~Role"` | ❌ W0 | ⬜ pending |
| D-21 | Startup fails on required_approval / required_role drift | unit | `--filter "FullyQualifiedName~QUE_02"` | ❌ W0 | ⬜ pending |
| Q17 | Production refuses dev reviewer scheme and fixtures | integration | `--filter "FullyQualifiedName~FND_05"` | extend | ⬜ pending |
| DB-03/04/06 | RLS, privilege matrix, ablation cover new tables | integration | `--filter "FullyQualifiedName~DB_0"` | extend | ⬜ pending |
| Sentinel | Rationale/parameters never in logs, problems, events, transcript | integration | `--filter "FullyQualifiedName~Sentinel"` | extend | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `sql/migrations/0008_review_queue.sql` plus forced test edits: `MigrationRunnerTests`, `PrivilegeMatrixTests`, `RlsPatternTests`, `TenantIsolationAblationTests`, `TestDatabase.SeedQueueItemAsync`
- [ ] Recording/faulting `IUserIdentityResolver` doubles in `RecordingPorts.cs` / `FaultingDoubles.cs`; `OliveApiFactory.CreateReviewerClient(id)`
- [ ] Reviewer, user-identity and tenant-map fixtures with schemas + valid/invalid examples
- [ ] `config/queue-kinds.yaml`, `config/reviewer-tenants.yaml`
- [ ] `contracts/queue-item/v1/*`; delete v0; update `ContractSchemaTests.Folders` and CLAUDE.md references
- [ ] Caller-separation spike test (4-case matrix) early, since multi-scheme challenge behaviour is MEDIUM confidence

---

## Manual-Only Verifications

All phase behaviors have automated verification.

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
