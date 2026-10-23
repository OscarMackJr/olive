---
phase: 04-policy-expiry-and-refusal
plan: 01
subsystem: catalog-policy-inputs
tags: [catalog, allowlist, entitlements, policy]
requires: []
provides:
  - CatalogEntry typed members RequiredPurpose, MaxRateLimit, MaxRateWindowSeconds, RequiredEntitlement, MaxExpirySeconds
  - IAgentAllowlist.Entitlements(agentId)
  - Q4 stop rule cleared and recorded
affects: [04-02, 04-03, 04-04, 04-05, 04-06, 04-07]
key-files:
  modified:
    - .planning/STATE.md
    - src/Olive.Catalog/CatalogModels.cs
    - src/Olive.Catalog/CatalogLoader.cs
    - src/Olive.Catalog/AgentAllowlist.cs
    - src/Olive.Application/IAgentAllowlist.cs
    - contracts/catalog/v1/action-type.schema.json
    - contracts/catalog/v1/action-type.valid.json
    - catalog/crm.case_note@1.yaml
    - config/agents.yaml
    - tests/Olive.Tests/CatalogLoaderTests.cs
    - tests/Olive.Tests/CatalogEndpointTests.cs
    - tests/Olive.Tests/AgentAllowlistTests.cs
decisions:
  - "Q4 closed (D-01): entitlement source is Olive-owned directory group resolution intersected with allowlist agent entitlements (POPEYE_v0.4.2 lines 55, 91; vocabulary v0.4 s3.2); POPEYE_v0.4.3 does not change it. Q14 stays open."
  - "Missing agent entitlements key means empty set (deny); catalog entries without required_entitlement or max_expiry_seconds > 0 fail startup."
metrics:
  tasks: 3
  completed: 2026-10-22
---

# Phase 4 Plan 01: Policy inputs in catalog and allowlist Summary

Catalog entries now require and expose typed `required_entitlement` and `max_expiry_seconds` (plus previously discarded purpose and rate scalars), agents carry source-controlled entitlements, and the Q4 stop rule is recorded as closed.

## Tasks

| Task | Commit | Notes |
|------|--------|-------|
| 1 Q4 ruling in STATE.md | a80e806 | Re-verified specs: v0.4.3 has no entitlement or group text; no contradiction |
| 2 Catalog typed policy fields | 7df366f | Strict loader, schema (properties and required), valid.json, crm.case_note@1 = ent:crm.case_note.write / 28800 |
| 3 Allowlist entitlements | 45357a0 | Dictionary-backed, blank entitlement rejected, liam holds ent:crm.case_note.write |

## Deviations from Plan

None to the design. The plan's loader validation also requires max_rate limit and window_seconds > 0 in the loader (as the plan stated); action-type.invalid.json was left unchanged and still fails the schema.

## Verification

- `dotnet build`: succeeds.
- `dotnet test --filter "Category!=Integration"`: 444 passed, 3 failed. The 3 failures (`VendoredContractTests.FND_03_*`) are pre-existing and environmental: this Windows checkout has `core.autocrlf=true`, so `contracts/vendored/vocabulary.yaml` is CRLF and its hash and LF-only checks fail. The file was not touched.
- Integration tests (`Category=Integration`, incl. `CatalogEndpointTests` with the two new assertions on `required_entitlement` and `max_expiry_seconds`): could not run here. 218 fail at host startup with `StartupGuardException` FND-03 (the same vocabulary CRLF hash mismatch), before reaching any test logic. The new endpoint assertions are therefore compiled but unverified in this environment; the loader unit test confirms the served entry JSON source contains the fields.

## Known Stubs

None.

## Threat Flags

None.

## Self-Check: PASSED

Commits a80e806, 7df366f, 45357a0 exist; modified files present; ROADMAP.md untouched.
