---
phase: 2
slug: contract-integrity-and-idempotent-submission
status: validated
nyquist_compliant: true
wave_0_complete: true
validated: 2026-10-22
created: 2026-10-21
---

# Phase 2 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 3.2.2, Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing |
| **Config file** | `tests/Olive.Tests/Olive.Tests.csproj` (traits: `Category=Integration`, `Category=Architecture`) |
| **Quick run command** | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| **Full suite command** | `dotnet test tests/Olive.Tests` (needs Docker) |
| **Estimated runtime** | ~20 seconds quick, ~120 seconds full |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test tests/Olive.Tests --filter "Category!=Integration"`
- **After every plan wave:** Run `dotnet test tests/Olive.Tests`
- **Before `/gsd-verify-work`:** Full suite must be green, plus `dotnet restore --locked-mode` and the vulnerable-package gate
- **Max feedback latency:** 120 seconds

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 02-01-T1,T2 | 02-01 | 1 | API-09 | — | JCS output matches RFC 8785 App. B and cyberphone vectors; golden vectors in `contracts/jcs/v1/` recompute byte-for-byte | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_09"` | ✅ | ✅ green |
| 02-04-T1,T2; 02-06-T1,T2 | 02-04, 02-06 | 2, 3 | API-04 | — | Duplicate keys, lone surrogates, lossy numbers, null optional, coercion-needed values → 400, nothing stored, no 500 | unit + integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_04"` | ✅ | ✅ green |
| 02-02-T2,T3; 02-06-T1 | 02-02, 02-06 | 1, 3 | API-05 | — | Contract schemas accept valid and reject invalid examples (format assertion on) | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_05"` | ✅ | ✅ green |
| 02-03-T1,T2; 02-07-T1,T2 | 02-03, 02-07 | 1, 4 | API-06 | — | Same body replay → 200 same id + current state; different body → 409, no new row, log has no body; run_id missing/invalid → 400 first | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_06"` | ✅ | ✅ green |
| 02-07-T1,T2 | 02-07 | 4 | API-07 | — | N parallel first submissions → exactly one row | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_07"` | ✅ | ✅ green |
| 02-05-T2 | 02-05 | 2 | API-03 | — | GET catalog returns `crm.case_note@1` with parameter schema; 404 unknown; 401 inactive | integration | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_03"` | ✅ | ✅ green |
| 02-05-T1,T2; 02-06 | 02-05 | 2 | API-10 | — | YamlDotNet load; D-02 violations and duplicate YAML keys fail startup; no runtime registration | unit + architecture | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_10"` | ✅ | ✅ green |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Green counts (2026-10-22, main @ 605fbae): API-03 10, API-04 59, API-05 46, API-06 21, API-07 2, API-09 76, API-10 61; full suite 589/589.*

---

## Wave 0 Requirements

- [x] `tests/Olive.Tests/JcsTests.cs` + `JcsVectorTests.cs` (vector loader) — API-09
- [x] `tests/Olive.Tests/JsonGateTests.cs` (planned as `IJsonGateTests.cs`) + `ApiIntegrityTests.cs` — API-04 / D-05 lossy-number table
- [x] `tests/Olive.Tests/IdempotencyTests.cs` — API-06, API-07
- [x] `tests/Olive.Tests/CatalogLoaderTests.cs` + `CatalogEndpointTests.cs` (planned as `CatalogTests.cs`) — API-03, API-10
- [x] `tests/Olive.Tests/ContractSchemaTests.cs` + `SchemaValidatorTests.cs` + `SubmissionValidatorTests.cs` — API-05; `JsonSchemaLite` removed, `IntrospectionFixtureTests` and `JcsVectorTests` use the real validator
- [x] `PrivilegeMatrixTests` and `MigrationRunnerTests` extended for migrations `0004` and `0005`; `ProposalStoreHashTests.cs` added

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| hometown runs the shared golden-vector set | API-09 (Q20) | Cross-team execution outside this repo | Hand `contracts/jcs/v1/` to the hometown owner and record their pass result |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 120s (quick ~5 s, full ~16 s)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-10-22

---

## Validation Audit 2026-10-22

| Metric | Count |
|--------|-------|
| Gaps found | 0 |
| Resolved | 0 |
| Escalated | 0 |

Each requirement's filter was run on main after the code-review fixes, and every behaviour in the map has a named test:
- **API-03:** `API_03_any_active_key_gets_the_catalog`, `_unknown_or_unparseable_version_is_404` and `_inactive_or_missing_key_gets_401`.
- **API-04:** `API_04_gate_failures_give_400_store_nothing_and_do_not_500` and `_null_optional_is_a_type_violation`. Lone-surrogate cases are in `JsonGateTests` and the vector reject cases.
- **API-05:** `API_05_format_assertion_is_on`.
- **API-06:** `API_06_identical_resubmission_replays_200_with_original_id_and_current_state`, `_different_body_same_run_gives_409_not_stored_and_logged_value_free` and `_gate_failures_give_400_before_any_lookup`.
- **API-07:** `API_07_concurrent_identical_first_submissions_yield_one_201_and_replays`.
- **API-09:** `API_09_vector_recomputes_canonical_bytes_and_sha256`.
- **API-10:** `API_10_duplicate_key_in_entry_fails`, `_faulty_catalog_stops_the_host_at_startup` and `_catalog_has_no_runtime_registration_member`.

The one manual item is unchanged: hometown still has to run the vectors (Q20, also tracked in `02-HUMAN-UAT.md`).
