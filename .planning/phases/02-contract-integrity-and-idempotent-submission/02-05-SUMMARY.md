---
phase: 02-contract-integrity-and-idempotent-submission
plan: 05
subsystem: catalog
tags: [yaml, yamldotnet, catalog, json-schema, startup-guard]
requires: [02-02]
provides:
  - CatalogLoader (Parse/LoadEmbedded) returning CatalogSet with Current, TryGetRelease, TryFindAction
  - CatalogVetting (D-02 closure across the whole parameters subtree)
  - GET /v1/catalog/{version}
  - StartupGuards.CheckCatalog (API-10)
affects: [02-06, 02-07]
tech-stack:
  added: []
  patterns: [YamlStream to JsonNode with core-schema scalar typing, strict System.Text.Json bind (Disallow), eager startup guard]
key-files:
  created:
    - catalog/catalog.yaml
    - src/Olive.Catalog/CatalogModels.cs
    - src/Olive.Catalog/YamlToJson.cs
    - src/Olive.Catalog/CatalogVetting.cs
    - src/Olive.Catalog/CatalogLoader.cs
    - src/Olive.Catalog/VocabularyPurposes.cs
    - src/Olive.Host/Endpoints/CatalogEndpoints.cs
    - tests/Olive.Tests/CatalogLoaderTests.cs
    - tests/Olive.Tests/CatalogEndpointTests.cs
  modified:
    - src/Olive.Catalog/Olive.Catalog.csproj
    - src/Olive.Contracts/ProblemReasons.cs
    - src/Olive.Host/Problems/ProblemResults.cs
    - src/Olive.Host/Program.cs
    - src/Olive.Host/Startup/StartupGuards.cs
    - src/Olive.Host/Startup/StartupGuardFilter.cs
key-decisions:
  - "Loader refuses YAML anchors/aliases and multi-document streams (alias cycles / ambiguity), in addition to duplicate keys"
  - "D-02 vetting treats any schema with type object, properties or patternProperties as an object schema needing additionalProperties:false; it also rejects $dynamicRef not starting with #"
requirements-completed: [API-03, API-10]
completed: 2026-10-22
---

# Phase 2 Plan 05: Catalog loader and GET /v1/catalog/{version} Summary

YamlDotNet YamlStream catalog loader with fail-closed vetting (duplicate keys, strict bind, vocabulary purposes, D-02 closure), served at GET /v1/catalog/{version} to any active key, with an eager startup guard.

## Tasks

| Task | Name | Commit |
|------|------|--------|
| 1 | Manifest, YamlStream loader, D-02 vetting, release model | 5d3e013 |
| 2 | GET /v1/catalog/{version}, DI registration, startup guard | 1834f0c |

## Verification

- `dotnet test tests/Olive.Tests` (full, including Docker integration tests): 462 passed, 0 failed.
- `git diff -- contracts/vendored` empty; no `AddAtRuntime` in src; no `IgnoreUnmatchedProperties` in Olive.Catalog.

## Deviations from Plan

None in behavior. Tests and implementation for each task were committed together rather than as separate RED/GREEN commits (types must exist for tests to compile). No new packages were needed (YamlDotNet was already referenced). The manifest/disk check compares `<id>.yaml` file names to the manifest.

## Notes

- crm.case_note@1.yaml was left unchanged. The CRLF churn on contracts/vendored in the worktree was reverted with `git checkout -- contracts/vendored` and never committed.
- The host catalog fault test injects a throwing `CatalogSet` factory via the test factory and asserts a StartupGuardException (API-10).

## Known Stubs

None.

## Self-Check: PASSED
