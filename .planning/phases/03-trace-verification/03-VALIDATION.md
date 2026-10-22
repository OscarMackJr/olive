---
phase: 3
slug: trace-verification
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-10-22
audited: 2026-10-22
---

# Phase 3 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing 10.0.13 |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits `Category=Integration`, `Category=Architecture`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Estimated runtime** | ~25 seconds (full, with Testcontainers; 753 tests) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet restore --locked-mode`
- **Max feedback latency:** 90 seconds

---

## Per-Task Verification Map

| Req ID | Plans | Behavior | Test Type | Automated Command | Tests | Status |
|--------|-------|----------|-----------|-------------------|-------|--------|
| TRC-01 | 03-03 | Fixtures conform to the seven-field schema; fixture hash equals `Jcs.ProposalHash`; strict parser | unit + integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~TRC_01"` | 9 | ✅ green |
| TRC-02 | 03-02, 03-05 | Unknown trace -> 422 `retryable:true`, zero rows; same `run_id` retry after fixture added -> 201 | integration | `--filter "FullyQualifiedName~TRC_02"` | 3 | ✅ green |
| TRC-03 | 03-01, 03-02, 03-04 | Null/differing `run_id` -> stored `trace_unresolved`; refusal storage, events, replay finality | integration | `--filter "FullyQualifiedName~TRC_03"` | 19 | ✅ green |
| TRC-04 | 03-02, 03-04 | Tenant mismatch -> `trace_tenant_mismatch` | integration | `--filter "FullyQualifiedName~TRC_04"` | 2 | ✅ green |
| TRC-05 | 03-02, 03-04 | `agent_id` null or differs from token `delegated_agent_id` -> `trace_agent_mismatch` | unit + integration | `--filter "FullyQualifiedName~TRC_05"` | 5 | ✅ green |
| TRC-06 | 03-02, 03-03, 03-04 | Hash mismatch/null -> `trace_proposal_mismatch`; key reorder passes; unknown key -> 400 before trace step | unit + integration | `--filter "FullyQualifiedName~TRC_06"` | 9 | ✅ green |
| TRC-07 | 03-02, 03-05 | Resolver/issuer unavailable, hang, throw, 401/403, malformed -> 503 retryable, alarm where specified, zero rows; invalid timeout refuses start | unit + integration | `--filter "FullyQualifiedName~TRC_07"` | 35 | ✅ green |
| TRC-08 | 03-02, 03-05 | Positive cache hit; not-found and failures never cached; cap clears | unit | `--filter "FullyQualifiedName~TRC_08"` | 8 | ✅ green |
| TRC-09 | 03-01, 03-03 | Source `fixture` recorded; CHECK/grant enforcement; production + fixture resolver (incl. decorated) refuses start | integration | `--filter "FullyQualifiedName~TRC_09"` | 11 | ✅ green |
| IDN-05 | 03-02, 03-03 | Stub issues grants with all four fields; unregistered tenant/agent refused; production refuses stub | unit + integration | `--filter "FullyQualifiedName~IDN_05"` | 14 | ✅ green |
| IDN-06 | 03-02, 03-05 | Token cache by `(tenant, agent, key_id)`; never outlives key; already-expired rejected; cap clears | unit | `--filter "FullyQualifiedName~IDN_06"` | 6 | ✅ green |
| IDN-07 | 03-02, 03-05 | Claim disagreement -> discarded, 503, alarm, zero rows | unit + integration | `--filter "FullyQualifiedName~IDN_07"` | 6 | ✅ green |
| IDN-08 | 03-06 | `[OutboundPort]` reflection; key only on popeye ports; sentinel scan over responses, Trace logs, stored rows (JSONL half: Phase 6) | architecture + integration | `--filter "FullyQualifiedName~IDN_08"` | 14 | ✅ green (partial scope) |
| Order | 03-03 | introspect -> FindByRun -> token -> resolve -> insert; replay/409/400 emit no token/resolve | integration | `--filter "FullyQualifiedName~TRC_ORDER"` | 3 | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

Known flake: `StartupGuardTests`, and `CatalogEndpointTests.API_10_*`, which uses the same `TryStartAsync` helper, fail intermittently in full-suite runs with `ObjectDisposedException`. This is carried over from Phase 1. Both pass when run alone.

---

## Wave 0 Requirements

- [x] `tests/Olive.Tests/ManualTimeProvider.cs`, `RecordingPorts.cs`, `FaultingDoubles.cs`, `SentinelScan.cs`
- [x] `OliveApiFactory` trace mode (auto-fixture default for legacy tests, explicit for Phase 3 tests) and recording decorators on all ports
- [x] Extend `PrivilegeMatrixTests` and `MigrationRunnerTests` for migration 0006
- [x] New `StartupGuardTests` cases: production + `FixtureTraceResolver` / `StubTokenIssuer`, including behind a `CachingTraceResolver` decorator
- [x] Contract examples for changed submit/GET response schemas; `ContractSchemaTests` additions
- [x] No framework install needed

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Popeye token-issuance hand-off and JCS golden set coordination recorded | IDN-05, TRC-06 | Cross-team coordination, not code | Confirm STATE.md / contract README lists the open items |
| Null or unreadable session key at the trace step returns 503 and stores nothing (`ProposalEndpoints.cs:114-118`) | TRC-01 / T-03-14 | Unreachable over HTTP: the auth handler uses the same `AgentKeyHeader.TryRead` and returns 401 first. The guard is defensive only | Code review: confirm the branch returns `TraceVerificationUnavailable` before any verifier or store call |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 90s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-10-22

---

## Validation Audit 2026-10-22

| Metric | Count |
|--------|-------|
| Gaps found | 3 |
| Resolved | 2 |
| Escalated (manual-only) | 1 |

- Resolved:
  - `IDN_06_token_cache_cap_clears_without_changing_results`.
  - `TRC_07_invalid_trace_timeout_refuses_start` (0, -5, abc), plus the positive control `TRC_07_valid_trace_timeout_is_applied_at_start`.
- Harness fix: `OliveApiFactory` now passes `OLIVE_TRACE_TIMEOUT_MS` with `UseSetting`, because `Program.cs` reads it before `Build()`. Before this fix the TRC-07 hang tests ran on the 5 s default instead of 100 ms. The full suite went from about 35 s to about 21 s.
- Manual-only: the null-session-key guard (unreachable over HTTP).
