# Codebase Structure

**Analysis Date:** 2026-10-21

## Directory Layout

```
olive/
├── AGENTS.md                 # Agent constraints (scaffold rules)
├── README.md                 # Status summary
├── Olive.sln                 # Solution (src + tests)
├── global.json               # .NET SDK 10.0.204, rollForward latestFeature
├── Directory.Build.props     # UseAppHost=false
├── Directory.Build.targets
├── src/Olive/                # Domain library (Domain.cs only)
├── tests/Olive.Tests/        # xUnit requirement tests
├── catalog/                  # Source-controlled action types (YAML)
├── contracts/
│   ├── schemas/              # JSON Schemas (action-type, execution-record, proposal, queue-item)
│   ├── examples/             # *.valid.json / *.invalid.json per schema
│   ├── queue/identity-review/# Queue item contract IR-1 (bluto -> olive); untracked
│   └── vendored/             # vocabulary.yaml (read-only) + VENDORED.md
├── db/migrations/            # Flyway-style V###__name.sql
├── docs/STATUS.md            # Entry criteria and open decisions
├── specs/                    # Charter, decision records, work prompt
└── .planning/codebase/       # Generated codebase maps
```

## Directory Purposes

**`src/Olive/`:**
- Purpose: Domain model and services
- Contains: `Domain.cs` (all types, ~130 lines), `Olive.csproj` (net10.0, nullable, implicit usings)

**`tests/Olive.Tests/`:**
- Purpose: Requirement-keyed tests
- Key files: `RequirementsTests.cs`, `GlobalUsings.cs`, `Olive.Tests.csproj`

**`catalog/`:**
- Purpose: One YAML per action type; fields `action_id`, `target_system`, `parameters`, `required_purpose`, `required_approval`, `reversible`, `max_rate.{limit,window_seconds}`
- Key files: `catalog/crm.case_note@1.yaml`

**`contracts/`:**
- Purpose: Machine-readable and prose contracts
- Key files: `contracts/schemas/queue-item.schema.json`, `contracts/queue/identity-review/QUEUE_ITEM_CONTRACT_identity-review_v0.1.md`, `contracts/vendored/vocabulary.yaml` (do not edit)

**`db/migrations/`:**
- Purpose: PostgreSQL schema with forced RLS
- Key files: `db/migrations/V001__action_broker.sql`

**`specs/`:**
- Purpose: Governing documents
- Key files: `specs/OLIVE_CHARTER_v0.4.md`, `specs/DECISION_olive-poc2-phase1_v0.5.md` (also `_v0.1.md`), `specs/PROMPT_OLIVE.md`, `specs/README.md` (the DECISION files and PROMPT are untracked)

## Key File Locations

**Entry Points:**
- `src/Olive/Domain.cs`: `ActionBroker` is the sole facade; no host executable

**Configuration:**
- `global.json`, `Directory.Build.props`, `src/Olive/Olive.csproj`, `.gitignore` (ignores `bin/`, `obj/`, `*.jsonl`, `.env`, `appsettings.*.json`, `*.tfvars`)

**Core Logic:**
- `src/Olive/Domain.cs`

**Testing:**
- `tests/Olive.Tests/RequirementsTests.cs` (resolves repo root via `../../../../../` from `AppContext.BaseDirectory` to read catalog/contracts/source)

## Naming Conventions

**Files:**
- C#: PascalCase (`Domain.cs`, `RequirementsTests.cs`)
- Catalog: `<domain>.<action>@<version>.yaml` (`crm.case_note@1.yaml`); must match `^[a-z0-9_.]+@[0-9]+$`
- Schemas: `<name>.schema.json`; examples `<name>.valid.json` / `<name>.invalid.json`
- Migrations: `V###__snake_case.sql`
- Specs: `UPPER_SNAKE_vX.Y.md`; contracts `QUEUE_ITEM_CONTRACT_<kind>_vX.Y.md`

**Directories:** lowercase; kebab-case for queue kinds (`identity-review`).

**Code:** namespace `Olive`; records PascalCase; tests named `REQ_<AREA>_<NN>_snake_description` tied to charter requirement IDs (REQ-ACT, REQ-QUE); state strings snake_case.

## Where to Add New Code

**New action type:** `catalog/<id>@<n>.yaml` using only purposes from `contracts/vendored/vocabulary.yaml`. No code or runtime registration.

**New domain behavior:** `src/Olive/Domain.cs` (or split into new files in `src/Olive/`, namespace `Olive`). Keep tenant predicates and reference-only payloads.

**New persisted table/column:** new `db/migrations/V002__*.sql`; never edit V001; include tenant_id in PK, enable and force RLS, add tenant policy.

**New queue kind:** contract markdown under `contracts/queue/<kind>/`; build on the generic queue.

**New schema:** `contracts/schemas/<name>.schema.json` plus valid and invalid examples in `contracts/examples/`.

**Tests:** `tests/Olive.Tests/`, one `[Fact]` per requirement, prefixed with the requirement ID.

**Real executors/connectors:** do not add until a signed divisional write agreement and scoped credential exist (charter §3.3).

## Special Directories

**`contracts/vendored/`:** copied unchanged from Fiskroad shared vocabulary; hash in `VENDORED.md`; committed; never edit.
**`bin/`, `obj/`, `TestResults/`:** generated, gitignored.
**`.planning/`:** generated planning artifacts.

---

*Structure analysis: 2026-10-21*
