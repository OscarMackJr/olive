<!-- GSD:project-start source:PROJECT.md -->
## Project

**Olive — Action Broker and Review Queue**

Olive is the POC-2 program's action broker and shared review queue. It is the smallest component that can hold the program's one write credential. An identified agent principal (Liam) submits an AI-suggested action that hometown has traced. Olive authenticates the agent through popeye, verifies the trace, and applies catalog and policy checks. Only after those checks does a proposal reach a human review queue. An approved proposal is executed idempotently and recorded in an append-only ledger. This milestone delivers Olive **Phase 1**, which is simulation-only under `specs/DECISION_olive-poc2-phase1_v0.5.md`. Every part of the path is real except the final write, which goes to a simulated executor.

**Core Value:** **No action reaches a target system unless it traces to a verified hometown answer, passed policy before any human saw it, was approved by a named reviewer with a rationale, and runs exactly once.** If everything else is cut, this chain must hold and be provable from the execution record.

### Constraints

- **Tech stack**: C# / .NET 10, ASP.NET Core and Razor Pages, PostgreSQL with Npgsql, xUnit v3 with Testcontainers. Charter §6.2 recommends .NET, which matches bluto and is familiar to reviewers.
- **Security**: no write-capable credential anywhere (REQ-ACT-04). The session key goes only to popeye. Tokens are never logged or stored. The service fails closed on any resolver or auth failure.
- **Data**: queue payloads are references only, never bodies (REQ-QUE-01). Tenant isolation has two layers, an application predicate and forced RLS, and both are kept.
- **Contracts**: `contracts/vendored/vocabulary.yaml` is never edited. `principal_type` is limited to `user | application | agent`. Within `v1`, changes are additive and optional only.
- **Dependencies**: popeye and hometown are stub- or fixture-backed until their sides are ratified and live. Fixtures are barred in production.
- **Change control**: changes to Decision §1–§3 need re-ratification. Changes to the response shape need hometown's agreement. Changes to the IR-1 contract need agreement from both the olive and bluto owners.
<!-- GSD:project-end -->

<!-- GSD:stack-start source:codebase/STACK.md -->
## Technology Stack

## Languages
- C# (.NET 10, `net10.0`, nullable and implicit usings enabled) - all code in `src/Olive/Domain.cs` and `tests/Olive.Tests/RequirementsTests.cs`
- SQL (PostgreSQL dialect) - `db/migrations/V001__action_broker.sql`
- YAML - action catalog `catalog/crm.case_note@1.yaml`, vendored vocabulary `contracts/vendored/vocabulary.yaml`
- JSON / JSON Schema draft 2020-12 - `contracts/<name>/v<N>/<name>.schema.json` with `<name>.valid.json` / `<name>.invalid.json` beside it
## Runtime
- .NET SDK 10.0.204, `rollForward: latestFeature` (`global.json`)
- `UseAppHost=false` for all projects (`Directory.Build.props`, `Directory.Build.targets`), except the test project (`UseAppHost=true` in `tests/Olive.Tests/Olive.Tests.csproj`)
- NuGet via the `dotnet` CLI
- Lockfile: missing (no `packages.lock.json`)
## Frameworks
- No web framework, ORM, or DI container. `src/Olive/Olive.csproj` is a plain class library with zero package references. It uses only BCL (`System.Text.Json`, `System.Security.Cryptography`, `System.Text.RegularExpressions`).
- xunit.v3 3.2.0 - test framework
- Microsoft.NET.Test.Sdk 18.0.0
- xunit.runner.visualstudio 3.1.5 (PrivateAssets=all)
- `Olive.sln` solution with two projects: `src/Olive/Olive.csproj` and `tests/Olive.Tests/Olive.Tests.csproj`
- No CI config detected (no `.github/`)
## Key Dependencies
- None third-party in `src/Olive`. YAML is parsed by hand (line-based `Parse` in `src/Olive/Domain.cs`), with no YAML library.
- Not present. No Npgsql, Dapper, EF Core, or HTTP client.
- PostgreSQL with forced row-level security; Testcontainers for CI Postgres
- Entra ID OIDC for reviewer auth
- JSON canonicalization per RFC 8785
## Configuration
- No runtime configuration is read by the code today. `SimulatedActionExecutor` takes a JSONL output path as a constructor argument; `Catalog.Load` takes catalog directory and vocabulary paths.
- `.gitignore` excludes `.env`, `appsettings.*.json`, `*.tfvars`, `*.jsonl`, `bin/`, `obj/`, `TestResults/`. No `.env` or `.env.example` exists yet.
- Planned variables: `OLIVE_POSTGRES_URL`, `OLIVE_POSTGRES_HOST`, `OLIVE_POSTGRES_PORT`, `OLIVE_POSTGRES_DB` (spec §15, not implemented).
- `global.json`, `Directory.Build.props`, `Directory.Build.targets`, `Olive.sln`
- Action catalog is file-only (`catalog/*.yaml`), validated at startup against purposes in `contracts/vendored/vocabulary.yaml` (do not edit; hash in `contracts/vendored/VENDORED.md`). No runtime registration (`Catalog.AddAtRuntime` throws).
## Platform Requirements
- .NET SDK 10.0.204+; Windows or any OS with .NET 10
- Planned: local shared Postgres container `deploy-postgres-1` on Docker network `deploy_default`, database `olive`
- Not defined. Charter open decision list is in `docs/STATUS.md`.
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

## Project Rules (from `AGENTS.md`)
- The repo is a scaffold. No real target-system connector, SDK, HTTP client, credential, or model call. Do not add `HttpClient` (enforced by test `REQ_ACT_04` in `tests/Olive.Tests/RequirementsTests.cs`).
- Real executors need a signed divisional write agreement and a scoped credential (charter §3.3, `specs/OLIVE_CHARTER_v0.4.md`). Do not add one until both exist.
- Proposals originate outside Olive. Olive only validates, queues, approves and simulates execution.
- Never edit `contracts/vendored/vocabulary.yaml` (see `contracts/vendored/VENDORED.md`). Update catalog entries in `catalog/*.yaml` instead.
- Queue payloads are references (`payload://...`, `proposal://...`), never inline bodies. Queue-item schema is `additionalProperties: false` (`contracts/queue-item/v0/queue-item.schema.json`).
- Keep tenant predicates in application queries (`x.TenantId == tenantId`) even though PostgreSQL RLS is also enforced (`db/migrations/V001__action_broker.sql`).
- Catalog action types are source-controlled (`catalog/`). No runtime registration API; `Catalog.AddAtRuntime` throws `NotSupportedException`.
## Naming Patterns
- C# files are PascalCase. Domain code currently lives in one file, `src/Olive/Domain.cs`. Tests: `tests/Olive.Tests/RequirementsTests.cs`.
- Catalog files: `catalog/<system>.<name>@<version>.yaml` (e.g. `catalog/crm.case_note@1.yaml`).
- Contract files: `contracts/<name>/v<N>/<name>.schema.json` with `<name>.valid.json` / `<name>.invalid.json` beside it, plus `contracts/jcs/v1/`.
- DB migrations: `db/migrations/V###__snake_case.sql`.
- PascalCase for types, methods, properties, record members. `_camelCase` for private fields (`_items`, `_attempts`, `_proposals`).
- Action IDs: `^[a-z0-9_.]+@[0-9]+$`, e.g. `crm.case_note@1`.
- Outcome/state strings are snake_case constants in `Outcomes` (`refused_policy`, `awaiting_approval`). Policy outcomes are literal `"allowed"` / `"refused"`.
- Policy check names snake_case (`purpose_entitlement`); rule IDs `UPPER-000` (`PURPOSE-001`, `DELEGATION-001`, `RATE-001`).
- JSON payload properties use snake_case (`proposal_id`, `would_send`).
## Code Style
- No `.editorconfig`, Prettier, or formatter configured. Follow the existing style: 4-space indent, Allman braces for type/method bodies, file-scoped `namespace Olive;`.
- Dense style is common: expression-bodied members, one-line records, and short methods compacted onto single lines with `;`-separated statements. Prefer clarity for new code, but keep records one-line.
- `net10.0`, `Nullable` enabled, `ImplicitUsings` enabled (`src/Olive/Olive.csproj`).
- Immutable `sealed record` types with `with` expressions for state changes; `sealed class` for services; primary constructors for services (`ActionBroker(Catalog catalog, ...)`); collection expressions (`[]`).
- Types are `sealed` by default. Time is passed in as a `DateTimeOffset now` parameter (not read inside services) so it can be tested; the exception is `SimulatedActionExecutor`, which uses `DateTimeOffset.UtcNow`.
- None configured (no analyzers, no `TreatWarningsAsErrors`).
- `Directory.Build.props` / `Directory.Build.targets` set `UseAppHost=false` for all projects (test project overrides to `true`).
## Import Organization
## Error Handling
- Throw exceptions with short messages for invariant violations: `InvalidOperationException` (unknown action, not approved, expired, invalid catalog), `ArgumentException` (decision missing reviewer/rationale/valid outcome), `KeyNotFoundException` (missing or cross-tenant item/proposal, deliberately indistinguishable), `NotSupportedException` (runtime catalog add).
- Policy refusals are not exceptions; they are returned as `PolicyDecision` lists and a `RefusedPolicy` proposal state.
- Reference requirement IDs in messages/comments where relevant (`"REQ-ACT-06: ..."`).
- Policy decision `Detail` text must be static; it must never include proposal parameter values (test `policy_decision_detail_never_contains_parameter_value`).
## Logging
## Comments
- Sparse. Comments explain boundaries/constraints (charter §3.3 note above `SimulatedActionExecutor`, RLS-mirroring note on `ListWithDatabaseRls`).
- No XML doc comments.
## Function Design
## Module Design
## Requirements Traceability
- Requirement IDs `REQ-ACT-nn` and `REQ-QUE-nn` tie code, tests and specs together. New behavior should cite a requirement ID in the test name.
- Contracts: JSON Schemas in `contracts/<name>/v<N>/` with paired `.valid.json` / `.invalid.json` examples beside them. Add both when adding a schema.
- Specs live in `specs/`; project status in `docs/STATUS.md`.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->
## Architecture

## System Overview
```text
```
## Component Responsibilities
| Component | Responsibility | File |
|-----------|----------------|------|
| `ActionBroker` | Proposal lifecycle: validate, queue, approve, execute; holds proposals in memory | `src/Olive/Domain.cs` |
| `Catalog` | Loads source-controlled action types from YAML, validates ids and purposes against vendored vocabulary; `AddAtRuntime` always throws | `src/Olive/Domain.cs`, `catalog/crm.case_note@1.yaml` |
| `PolicyEngine` | Three checks per proposal: `purpose_entitlement`, `principal_delegation`, `rate_limit` | `src/Olive/Domain.cs` |
| `ReviewQueueService` | Shared review queue: add, list, claim, decide, audit, tenant-filtered | `src/Olive/Domain.cs` |
| `SimulatedActionExecutor` | Idempotent (by `ProposalId`) simulated execution; JSONL append only | `src/Olive/Domain.cs` |
| `Outcomes` | String constants for all states (match DB check constraints) | `src/Olive/Domain.cs` |
| Records (`Principal`, `ActionType`, `Proposal`, `PolicyDecision`, `QueueItem`, `ExecutionRecord`) | Immutable domain data; mutate via `with` | `src/Olive/Domain.cs` |
## Pattern Overview
- Olive never originates proposals or calls models; it validates, queues, approves, simulates (`AGENTS.md`).
- Policy refusal happens before queueing (REQ-ACT-02); refused proposals never reach the queue.
- Queue items carry references (`payload_ref`, `subject_ref`), never inline bodies (REQ-QUE-01).
- Tenancy enforced twice: application predicates (`x.TenantId == tenantId` in every lookup) plus PostgreSQL forced RLS.
- Catalog is file-only, loaded at startup; no runtime registration, no `action_types` table.
- No real executor, HTTP client, credential or connector (charter §3.3 gate).
## Layers
- Purpose: JSON Schemas + valid/invalid examples, queue-kind contracts, vendored vocabulary
- Location: `contracts/`
- Contains: `proposal/v1/`, `catalog/v1/`, `queue-item/v0/`, `execution-record/v0/`, `introspection/`, `queue/identity-review/`, `vendored/` (read-only `vocabulary.yaml`)
- Used by: tests (`RequirementsTests`), external producers/consumers
- Location: `catalog/` (YAML per action type, named `<action_id>.yaml`)
- Parsed by line-based reader in `Catalog.Parse` (not a YAML library)
- Location: `src/Olive/Domain.cs`
- Depends on: BCL only (System.Text.Json, Regex, File IO)
- Location: `db/migrations/V001__action_broker.sql` — schemas `action_broker` and `integration_outbox`; tables `proposals`, `queue_items`, `executions`, `idempotency_records`, `outbox_facts`; RLS via `action_broker.current_tenant_id()` reading `olive.tenant_id` session setting. No C# code uses it yet.
## Data Flow
### Primary Request Path
### Expiry
- In-memory dictionaries/lists inside `ActionBroker`, `ReviewQueueService`, `PolicyEngine` (rate windows), `SimulatedActionExecutor`. Time is injected via `now` parameters.
## Key Abstractions
## Entry Points
## Architectural Constraints
- **Threading:** Not thread-safe; plain `Dictionary`/`List` without locks.
- **Global state:** None static; all state is per-instance.
- **Circular imports:** None (one file).
- **Serialization:** DB multi-row writes are expected to use SERIALIZABLE isolation with row-count checks before COMMIT (comment in `V001__action_broker.sql`).
- **Vendored file:** `contracts/vendored/vocabulary.yaml` must not be edited (hash in `contracts/vendored/VENDORED.md`).
## Anti-Patterns
### Inline payloads in queue items
### Dropping tenant predicates
### Runtime action registration or real connectors
## Error Handling
- `InvalidOperationException` for unknown action type, expired or unapproved execution
- `KeyNotFoundException` for missing/cross-tenant proposal or item
- `ArgumentException` for decisions lacking reviewer/rationale/valid outcome
- `NotSupportedException` for runtime catalog adds
## Cross-Cutting Concerns
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->
## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->



<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
