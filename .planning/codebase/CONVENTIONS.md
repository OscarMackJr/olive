# Coding Conventions

**Analysis Date:** 2026-10-21

## Project Rules (from `AGENTS.md`)

These are binding constraints for all new code:

- The repo is a scaffold. No real target-system connector, SDK, HTTP client, credential, or model call. Do not add `HttpClient` (enforced by test `REQ_ACT_04` in `tests/Olive.Tests/RequirementsTests.cs`).
- Real executors need a signed divisional write agreement and a scoped credential (charter §3.3, `specs/OLIVE_CHARTER_v0.4.md`). Do not add one until both exist.
- Proposals originate outside Olive. Olive only validates, queues, approves and simulates execution.
- Never edit `contracts/vendored/vocabulary.yaml` (see `contracts/vendored/VENDORED.md`). Update catalog entries in `catalog/*.yaml` instead.
- Queue payloads are references (`payload://...`, `proposal://...`), never inline bodies. Queue-item schema is `additionalProperties: false` (`contracts/schemas/queue-item.schema.json`).
- Keep tenant predicates in application queries (`x.TenantId == tenantId`) even though PostgreSQL RLS is also enforced (`db/migrations/V001__action_broker.sql`).
- Catalog action types are source-controlled (`catalog/`). No runtime registration API; `Catalog.AddAtRuntime` throws `NotSupportedException`.

## Naming Patterns

**Files:**
- C# files are PascalCase. Domain code currently lives in one file, `src/Olive/Domain.cs`. Tests: `tests/Olive.Tests/RequirementsTests.cs`.
- Catalog files: `catalog/<system>.<name>@<version>.yaml` (e.g. `catalog/crm.case_note@1.yaml`).
- Contract files: `contracts/schemas/<name>.schema.json`, examples `contracts/examples/<name>.valid.json` / `.invalid.json`.
- DB migrations: `db/migrations/V###__snake_case.sql`.

**Types/Functions:**
- PascalCase for types, methods, properties, record members. `_camelCase` for private fields (`_items`, `_attempts`, `_proposals`).
- Action IDs: `^[a-z0-9_.]+@[0-9]+$`, e.g. `crm.case_note@1`.
- Outcome/state strings are snake_case constants in `Outcomes` (`refused_policy`, `awaiting_approval`). Policy outcomes are literal `"allowed"` / `"refused"`.
- Policy check names snake_case (`purpose_entitlement`); rule IDs `UPPER-000` (`PURPOSE-001`, `DELEGATION-001`, `RATE-001`).
- JSON payload properties use snake_case (`proposal_id`, `would_send`).

## Code Style

**Formatting:**
- No `.editorconfig`, Prettier, or formatter configured. Follow the existing style: 4-space indent, Allman braces for type/method bodies, file-scoped `namespace Olive;`.
- Dense style is common: expression-bodied members, one-line records, and short methods compacted onto single lines with `;`-separated statements. Prefer clarity for new code, but keep records one-line.

**Language features in use:**
- `net10.0`, `Nullable` enabled, `ImplicitUsings` enabled (`src/Olive/Olive.csproj`).
- Immutable `sealed record` types with `with` expressions for state changes; `sealed class` for services; primary constructors for services (`ActionBroker(Catalog catalog, ...)`); collection expressions (`[]`).
- Types are `sealed` by default. Time is passed in as a `DateTimeOffset now` parameter (not read inside services) so it can be tested; the exception is `SimulatedActionExecutor`, which uses `DateTimeOffset.UtcNow`.

**Linting:**
- None configured (no analyzers, no `TreatWarningsAsErrors`).
- `Directory.Build.props` / `Directory.Build.targets` set `UseAppHost=false` for all projects (test project overrides to `true`).

## Import Organization

**Order:**
1. `System.*` namespaces (alphabetical)
2. Project namespace (`using Olive;` in tests)

Test project has `global using Xunit;` in `tests/Olive.Tests/GlobalUsings.cs`. No path aliases.

## Error Handling

**Patterns:**
- Throw exceptions with short messages for invariant violations: `InvalidOperationException` (unknown action, not approved, expired, invalid catalog), `ArgumentException` (decision missing reviewer/rationale/valid outcome), `KeyNotFoundException` (missing or cross-tenant item/proposal, deliberately indistinguishable), `NotSupportedException` (runtime catalog add).
- Policy refusals are not exceptions; they are returned as `PolicyDecision` lists and a `RefusedPolicy` proposal state.
- Reference requirement IDs in messages/comments where relevant (`"REQ-ACT-06: ..."`).
- Policy decision `Detail` text must be static; it must never include proposal parameter values (test `policy_decision_detail_never_contains_parameter_value`).

## Logging

**Framework:** None. No logging exists. Execution output is appended to a JSONL file by `SimulatedActionExecutor` (`*.jsonl` is git-ignored).

## Comments

**When to Comment:**
- Sparse. Comments explain boundaries/constraints (charter §3.3 note above `SimulatedActionExecutor`, RLS-mirroring note on `ListWithDatabaseRls`).
- No XML doc comments.

## Function Design

**Size:** Small methods; broker methods are state-machine steps (`Submit` -> `Approve` -> `Execute`).

**Parameters:** Tenant ID is always the first parameter on tenant-scoped service methods (`Get(Guid tenantId, Guid proposalId)`); `now` is last.

**Return Values:** Return updated immutable records; collections returned as `IReadOnlyList<T>`.

## Module Design

**Exports:** Everything is `public` in namespace `Olive`; interface `ISimulatedActionExecutor` for the only seam.

**Barrel Files:** Not applicable.

## Requirements Traceability

- Requirement IDs `REQ-ACT-nn` and `REQ-QUE-nn` tie code, tests and specs together. New behavior should cite a requirement ID in the test name.
- Contracts: JSON Schemas in `contracts/schemas/` with paired valid/invalid examples in `contracts/examples/`. Add both when adding a schema.
- Specs live in `specs/`; project status in `docs/STATUS.md`.

---

*Convention analysis: 2026-10-21*
