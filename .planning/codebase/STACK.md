# Technology Stack

**Analysis Date:** 2026-10-21

Status: scaffold. Only the items marked "present" exist in code. Items marked "planned" come from `specs/DECISION_olive-poc2-phase1_v0.5.md` and are not implemented.

## Languages

**Primary:**
- C# (.NET 10, `net10.0`, nullable and implicit usings enabled) - all code in `src/Olive/Domain.cs` and `tests/Olive.Tests/RequirementsTests.cs`

**Secondary:**
- SQL (PostgreSQL dialect) - `db/migrations/V001__action_broker.sql`
- YAML - action catalog `catalog/crm.case_note@1.yaml`, vendored vocabulary `contracts/vendored/vocabulary.yaml`
- JSON / JSON Schema draft 2020-12 - `contracts/schemas/*.schema.json`, `contracts/examples/*.json`

## Runtime

**Environment:**
- .NET SDK 10.0.204, `rollForward: latestFeature` (`global.json`)
- `UseAppHost=false` for all projects (`Directory.Build.props`, `Directory.Build.targets`), except the test project (`UseAppHost=true` in `tests/Olive.Tests/Olive.Tests.csproj`)

**Package Manager:**
- NuGet via the `dotnet` CLI
- Lockfile: missing (no `packages.lock.json`)

## Frameworks

**Core:**
- No web framework, ORM, or DI container. `src/Olive/Olive.csproj` is a plain class library with zero package references. It uses only BCL (`System.Text.Json`, `System.Security.Cryptography`, `System.Text.RegularExpressions`).

**Testing:**
- xunit.v3 3.2.0 - test framework
- Microsoft.NET.Test.Sdk 18.0.0
- xunit.runner.visualstudio 3.1.5 (PrivateAssets=all)

**Build/Dev:**
- `Olive.sln` solution with two projects: `src/Olive/Olive.csproj` and `tests/Olive.Tests/Olive.Tests.csproj`
- No CI config detected (no `.github/`)

## Key Dependencies

**Critical:**
- None third-party in `src/Olive`. YAML is parsed by hand (line-based `Parse` in `src/Olive/Domain.cs`), with no YAML library.

**Infrastructure:**
- Not present. No Npgsql, Dapper, EF Core, or HTTP client.

**Planned (not present, per `specs/DECISION_olive-poc2-phase1_v0.5.md`):**
- PostgreSQL with forced row-level security; Testcontainers for CI Postgres
- Entra ID OIDC for reviewer auth
- JSON canonicalization per RFC 8785

## Configuration

**Environment:**
- No runtime configuration is read by the code today. `SimulatedActionExecutor` takes a JSONL output path as a constructor argument; `Catalog.Load` takes catalog directory and vocabulary paths.
- `.gitignore` excludes `.env`, `appsettings.*.json`, `*.tfvars`, `*.jsonl`, `bin/`, `obj/`, `TestResults/`. No `.env` or `.env.example` exists yet.
- Planned variables: `OLIVE_POSTGRES_URL`, `OLIVE_POSTGRES_HOST`, `OLIVE_POSTGRES_PORT`, `OLIVE_POSTGRES_DB` (spec §15, not implemented).

**Build:**
- `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Olive.sln`

**Source-controlled data:**
- Action catalog is file-only (`catalog/*.yaml`), validated at startup against purposes in `contracts/vendored/vocabulary.yaml` (do not edit; hash in `contracts/vendored/VENDORED.md`). No runtime registration (`Catalog.AddAtRuntime` throws).

## Platform Requirements

**Development:**
- .NET SDK 10.0.204+; Windows or any OS with .NET 10
- Planned: local shared Postgres container `deploy-postgres-1` on Docker network `deploy_default`, database `olive`

**Production:**
- Not defined. Charter open decision list is in `docs/STATUS.md`.

---

*Stack analysis: 2026-10-21*
