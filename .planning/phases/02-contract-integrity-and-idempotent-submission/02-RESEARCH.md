# Phase 2: Contract Integrity and Idempotent Submission - Research

**Researched:** 2026-10-21
**Domain:** ASP.NET Core minimal-API request integrity: I-JSON gate, in-house RFC 8785 JCS, JSON Schema 2020-12 validation, idempotent submission on Postgres, YAML-sourced catalog
**Confidence:** HIGH on codebase integration and .NET behaviour (spiked on this machine); MEDIUM on JsonSchema.Net API details and the lossy-number rule (needs unit tests to pin)

## Summary

Phase 2 is mostly wiring around a few well-bounded problems: (1) turn the Phase 1 raw-body checks into a whole-body I-JSON gate with duplicate-key rejection; (2) write a small, BCL-only JCS canonicalizer and hash; (3) add an idempotency lookup between the principal checks and schema validation, with `ON CONFLICT DO NOTHING` plus re-lookup for the race; (4) validate with JsonSchema.Net; (5) replace the (already removed) hand parser with a YamlDotNet catalog loader and a `GET /v1/catalog/{version}` route; (6) move contracts into versioned folders and commit JCS golden vectors.

Four findings change how the plan should be written and need the planner's attention. They are detailed under Pitfalls and Open Questions.

1. **JsonSchema.Net licensing.** From 9.0.0 the NuGet binaries (JsonSchema.Net 9.x, JsonPointer.Net 7.x, Json.More.Net 3.x) ship under the Open Source Maintenance Fee EULA (fee only for revenue-generating users at or above US$10k/yr). **8.0.5 is the last MIT-licensed release** and works on net10.0 (spiked). Recommendation: pin 8.0.5 / JsonPointer.Net 6.0.1 / Json.More.Net 2.2.0, unless the user accepts the EULA. This is a surfaced decision, not a silent substitution of D-01 (the library is the same).
2. **Cyberphone `values.json` conflicts with D-05/D-18.** The canonical input contains `333333333.33333329`, which is "lossy" under D-05 yet is a required JCS vector with expected output `333333333.3333333`. The golden manifest must separate "canonicalizer output" from "gate verdict".
3. **Enabling `AllowDuplicateProperties = false` changes parse exceptions.** A lone-surrogate *property name* now throws `InvalidOperationException` from `JsonDocument.Parse` (not `JsonException`). Phase 1 catches only `JsonException`, so this would become a 500 unless the new gate catches both.
4. **The envelope's required-field set is unspecified**, and many Phase 1 success-path tests post partial bodies. Decide it up front (recommendation below).

**Primary recommendation:** Build in this order as vertical slices: (a) JCS + golden vectors (pure, no dependencies); (b) I-JSON gate + body_sha256 + migration 0004 + idempotent replay/409/race; (c) JsonSchema.Net envelope + catalog loader/vetting + parameter validation; (d) `GET /v1/catalog/{version}`; (e) contracts folder move, CLAUDE.md update, test-suite updates.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Validate with **JsonSchema.Net** (json-everything), using 2020-12 with **format assertion on**. Add it and its transitive packages (e.g. `Json.More.Net`, `JsonPointer.Net`) to Central Package Management, the lockfiles, and the FND-02 package allowlist test. One validator covers both the v1 envelope and the catalog parameter schemas.
- **D-02:** Catalog parameter schemas must belong to a **vetted subset**, checked at startup. Startup fails if any of these rules is broken: every object schema has `additionalProperties: false`; no remote `$ref` (offline resolution only, REQ-ACT-04); no `default` keyword anywhere; `format` values come from an explicit allowlist. Same vetting applies to API-10's startup check.
- **D-03:** A schema failure is a **400** whose problem detail carries a stable reason code (e.g. `schema_violation`), the **instance JSON Pointer** and **failing keyword** of the **first** error, **never the value**. Nothing is stored; caller can fix and resubmit under the same `run_id`.
- **D-04:** The Phase 1 hand-written raw-input checks become the explicit **Q1 pre-idempotency I-JSON gate** (lone surrogates, NUL, number bounds, depth, size cap) plus the new duplicate-key and lossy-number checks. Unparseable / non-I-JSON / missing-or-invalid `run_id` gets 400 **before** the idempotency lookup. Schema validation is a separate, later step (§15.2 step 4). Existing Phase 1 tests must keep passing.
- **D-05:** **Lossy numbers are rejected** at the I-JSON gate (400). Lossy = the IEEE-754 double does not round-trip to the same mathematical value (e.g. `9007199254740993`, over-long decimals). Spellings the double keeps exact (`1.0`, `1`, `1e0`) are accepted and hash identically. JCS is in-house (API-09).
- **D-06:** Proposal row stores the **canonical JCS bytes** (text) the hash was computed over, plus `body_sha256`, plus existing jsonb columns. New forward-only migration (`0004_...`). Migrations 0001-0003 are checksum-pinned and must not be edited.
- **D-07:** `body_sha256` covers the **whole v1 request body**, `nono_session_id` included (§15.3). A retry that only changes `nono_session_id` is a different body: `409 idempotency_conflict`.
- **D-08:** Phase 2 also **computes and stores the four-key `proposal_hash`**: SHA-256 over JCS of exactly `action_type`, `party_ref`, `parameters`, `justification` (§17.2), same JCS code, own column.
- **D-09:** `{version}` in `GET /v1/catalog/{version}` is a **catalog release** number (`GET /v1/catalog/1` returns every entry in release 1).
- **D-10:** Release comes from source-controlled manifest `catalog/catalog.yaml` declaring `catalog_version` and listing entries. Startup fails if manifest and `catalog/*.yaml` on disk disagree. No runtime registration path (REQ-ACT-06).
- **D-11:** **Any active key** may call the catalog endpoint (introspection first; inactive = 401; no principal-type or allowlist restriction).
- **D-12:** Unknown release = **404** (problem+json, stable reason code). Known release = **200** `{catalog_version, entries: [...]}`; each entry is the full YAML entry as JSON: `action_id`, `target_system`, `parameters` (JSON Schema), `required_purpose`, `required_approval`, `reversible`, `max_rate`.
- **D-13:** On POST, `action_type` is looked up in the **current release only** (highest `catalog_version` in the manifest). Not found = `not_in_catalog`; the stored refusal is Phase 4. Older releases stay readable through GET.
- **D-14:** YamlDotNet catalog loader replaces the old hand-rolled parser, following the strict-mapping pattern in `AgentAllowlist` (no `IgnoreUnmatchedProperties`, fails closed). Make sure nothing like the scaffold parser comes back.
- **D-15:** **Move all old contract schemas into versioned folders now**, each with paired valid/invalid examples: proposal -> `contracts/proposal/v1/`; action-type -> `contracts/catalog/v1/`; queue-item and execution-record -> versioned folders beside them (labelled **placeholders** until Phases 5/6). `contracts/schemas/` and `contracts/examples/` are removed. Update CLAUDE.md contract-path conventions. Do not touch `contracts/vendored/`.
- **D-16:** `contracts/proposal/v1/` contains `proposal-request.schema.json` (POST body: 7 v1 fields, `additionalProperties: false`, no `null` for optional fields), `proposal.schema.json` (GET resource), and a submit-response schema for 201/200 (`{proposal_id, state}`), each with paired valid and invalid examples.
- **D-17:** Shared JCS golden vectors live in `contracts/jcs/v1/`: per case `<name>.input.json` + `<name>.canonical.json` (exact canonical bytes); a `vectors.json` manifest with expected SHA-256 and `kind` (`body` or `proposal_hash`); includes RFC 8785 + cyberphone vectors plus Olive-specific cases (four-key `proposal_hash`, unicode/escapes, number spellings).
- **D-18:** Vectors include **negative cases `expect: reject`**: lossy numbers, duplicate keys, lone surrogates.
- **Carried forward:** §15.2 order (introspect -> principal checks -> idempotency -> schema/catalog; replay never calls the trace resolver); identical canonical body -> `200` original `proposal_id` + **current** state; different body -> `409 idempotency_conflict`, not stored, logged; concurrent first submissions -> unique constraint + re-lookup. Q1/Q2 ordering. §15.3/§17.2 non-mutation; optional field present as `null` = 400. Phase 1 D-09/D-10 (201 + `Location` + `{proposal_id, state}`; `UNIQUE (tenant_id, agent_id, run_id)` exists); `ProblemReasons.DuplicateRun` replaced by `idempotency_conflict`. Reason codes stay stable; problem detail never includes parameter values.

### Claude's Discretion
- Column names/types for JCS bytes, `body_sha256`, `proposal_hash` in migration `0004`, following the 0002/0003 RLS and column-grant pattern (extend column-level INSERT grant and the privilege-matrix test).
- Exact format allowlist for D-02 and exact reason-code names (e.g. `schema_violation`, `catalog_version_not_found`).
- 409 conflict log line content (never body/parameter values or the key).
- Which project hosts the JCS code and validator wrapper (Contracts, Application or Catalog), within the FND-01 inward graph.
- The 200 replay's headers (whether to repeat `Location`) and body, consistent with the submit-response schema.

### Deferred Ideas (OUT OF SCOPE)
None. (Not in this phase: stored `not_in_catalog` refusal = Phase 4; trace resolution and check 5 = Phase 3; OpenAPI 3.1 and COMPATIBILITY.md = Phase 9; API-08 full order-of-operations test = Phase 4.)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| API-03 | `GET /v1/catalog/{version}` returns the source-controlled entry incl. parameter JSON Schema | YAML -> JSON conversion via `YamlStream` (Pattern 5); manifest + release lookup; route with `RequireAuthorization()` only |
| API-04 | Raw bytes, parsed once, duplicate keys rejected, no coercion/defaults/stripping/trimming, null optional = 400 | `JsonDocumentOptions.AllowDuplicateProperties = false` (spiked); gate walk (Pattern 1); closed schema with no `null` types |
| API-05 | v1 JSON Schemas (2020-12) under `contracts/proposal/v1/`, format assertion on | JsonSchema.Net `EvaluationOptions.RequireFormatValidation = true` (spiked on 8.0.5) |
| API-06 | Idempotent on `(agent_id, run_id)` with JCS `body_sha256`; 200 replay / 409 conflict logged | Lookup-first + hash compare (Pattern 3); 409 log guidance |
| API-07 | Concurrent first submissions -> one proposal | Existing `ON CONFLICT ON CONSTRAINT proposal_run_unique DO NOTHING RETURNING` + re-lookup (Pattern 3); concurrency test design |
| API-09 | In-house JCS passes RFC 8785 + cyberphone vectors; shared golden vectors for hometown | JCS algorithm notes (Pattern 2), vector sources and the values.json conflict |
| API-10 | Catalog loaded from YAML at startup via YamlDotNet, validated against action-type schema; no hand parser; no runtime registration | Pattern 5; D-02 vetting; duplicate-key finding for YamlDotNet |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

- C# / .NET 10, ASP.NET Core, PostgreSQL + Npgsql (+ Dapper in Persistence), xUnit v3 + Testcontainers.
- **No `HttpClient` anywhere in src** (REQ-ACT-04; enforced by tests). JSON Schema `$ref` must resolve offline; JsonSchema.Net must not be given a fetch hook.
- Never edit `contracts/vendored/vocabulary.yaml`. Catalog changes go in `catalog/*.yaml`.
- Queue payloads are references only; tenant predicates stay in every application query (`tenant_id = @tenant_id`) even with forced RLS.
- Catalog is file-only; `Catalog.AddAtRuntime` / any runtime registration must not exist.
- Policy-decision / problem-detail text must be static; never include parameter values.
- New behaviour cites a requirement ID in the test name (`API_06_...`); contracts get paired valid/invalid examples.
- Style: `.editorconfig` exists; `sealed` types, file-scoped namespaces, 4-space indent, time injected (`TimeProvider`).
- FND-01 inward project graph (Domain/Contracts leaf; Application -> Domain; Catalog -> Application+Domain; Host -> all) and FND-02 package allowlist are enforced by `ArchitectureTests`.
- CI runs `dotnet restore --locked-mode`; lockfiles (`packages.lock.json`) must be regenerated and committed; a vulnerable-package gate runs in CI.
- GSD workflow: edits only through GSD commands.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| I-JSON gate (dup keys, surrogates, NUL, numbers) | API / Backend (Host endpoint layer) | Domain (pure helpers) | Runs on raw bytes before any state read; no persistence |
| JCS canonicalization + SHA-256 | Domain (pure, BCL only) | Contracts (vectors) | Deterministic pure function shared by body hash and proposal_hash; hometown consumes the same vectors |
| Idempotency lookup / race resolution | Database / Storage | API / Backend | Unique constraint is the authority; endpoint orchestrates lookup, compare, re-lookup |
| Envelope + parameter schema validation | Catalog (validator wrapper, JsonSchema.Net) | Host (maps result to 400) | Needs package; Catalog already owns YamlDotNet and is referenced by Host |
| Catalog load/vetting | Catalog | Host (startup guard) | Source-controlled files, validated before the host accepts traffic |
| Catalog endpoint | API / Backend (Host) | Catalog | Read-only projection of the in-memory release |
| Golden vectors and versioned schemas | Contracts files (`contracts/`) | Tests | Language-neutral artifacts consumed by hometown |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| JsonSchema.Net | **8.0.5** (MIT; see licensing note) | JSON Schema 2020-12 validation with format assertion | D-01 locked. 9.4.1 is current but is under the OSMF EULA. `[VERIFIED: nuget.org nuspec for 7.4.0, 8.0.5, 9.0.0, 9.4.1]` |
| JsonPointer.Net | 6.0.1 (transitive of 8.0.5) | Instance location pointers in errors | Transitive; `[VERIFIED: nuspec]` |
| Json.More.Net | 2.2.0 (transitive via JsonPointer.Net 6.0.1) | JSON helpers | Transitive; `[VERIFIED: nuspec]` |
| YamlDotNet | 18.1.0 (already pinned) | Catalog YAML parse | Already in `Directory.Packages.props` and used by `AgentAllowlist` `[VERIFIED: codebase]` |
| System.Text.Json / System.Security.Cryptography | in-box (.NET 10.0.11 runtime present) | Parsing, SHA-256 | BCL, no new package |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Humanizer.Core | 2.14.1 / 3.0.1 (transitive of JsonPointer.Net 6.0.1) | Transitive dependency | Appears in lockfiles; must pass the vulnerable-package gate |
| Dapper / Npgsql | 2.1.89 / 10.0.3 (existing) | Persistence | Extend `ProposalSql` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| JsonSchema.Net 8.0.5 (MIT) | JsonSchema.Net 9.4.1 | Newer, but OSMF EULA (`requireLicenseAcceptance=true`; fee for revenue-generating users >= US$10k/yr). Needs user acceptance before use. |
| JsonSchema.Net 8.0.5 | 7.4.0 (MIT) | Already in the local NuGet cache; older JsonPointer.Net 5.3.1 / Json.More.Net 2.1.1. API differs from 8.x; no reason to prefer. |
| Hand JCS | `Cyberphone.Json.Canonicalizer` NuGet etc. | API-09 says in-house; also packages are outside the FND-02 allowlist. Not used. |

**Installation (central package management):**
```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="JsonSchema.Net" Version="8.0.5" />
<!-- optional transitive pins (CentralPackageTransitivePinningEnabled=true) -->
<PackageVersion Include="JsonPointer.Net" Version="6.0.1" />
<PackageVersion Include="Json.More.Net" Version="2.2.0" />
```
```xml
<!-- src/Olive.Catalog/Olive.Catalog.csproj -->
<PackageReference Include="JsonSchema.Net" />
```
Then regenerate every `packages.lock.json` (`dotnet restore --force-evaluate`) and update `ArchitectureTests.PackageAllowlist["Olive.Catalog"]` to `["JsonSchema.Net", "YamlDotNet"]`. The test project calls Catalog code only, so it needs no direct reference.

**Version verification:** nuget.org flat-container index and nuspecs queried 2026-10-21: JsonSchema.Net latest 9.4.1 (OSMFEULA), 8.0.5 (MIT, net10.0 target group present, JsonPointer.Net 6.0.1), 7.4.0 (MIT). 8.0.5 downloads 804k; package total 52.8M; owner gregsdennis; source repo github.com/json-everything/json-everything.

## Package Legitimacy Audit

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| JsonSchema.Net | NuGet | multi-year (v1 .. v9) | 52.8M total | github.com/json-everything/json-everything | CLI installed but `--json` unsupported; registry + repo + owner verified manually | Approved at 8.0.5. `[ASSUMED]` tag on slopcheck only; planner may add a lightweight `checkpoint:human-verify` before first restore. Licence note above. |
| JsonPointer.Net | NuGet | multi-year | high | same repo | not run | Approved (transitive, pinned 6.0.1) |
| Json.More.Net | NuGet | multi-year | high | same repo | not run | Approved (transitive, pinned 2.2.0) |
| YamlDotNet | NuGet | existing | existing | existing | n/a | Already in use |

**Packages removed:** none. **Flagged suspicious:** none. **License flag:** 9.x line carries OSMF EULA (see Summary).
`postinstall` scripts do not exist in NuGet.

## Architecture Patterns

### System Architecture Diagram

```
POST /v1/proposals (raw bytes)
  |
  v
[auth handler: introspect key] --inactive--> 401
  |
[principal checks: agent + allowlist] --fail--> 403
  |
[content-type JSON?] --no--> 400
  |
[bounded read, 1 MiB] --over--> 413
  |
[PARSE once: JsonDocument, MaxDepth 64, AllowDuplicateProperties=false]
   catch JsonException AND InvalidOperationException --> 400 invalid_request
  |
[I-JSON GATE walk of whole tree: names+strings well-formed, no NUL,
   numbers in range + not lossy + expansion budget; root is object;
   run_id present, string, 1..256, clean]  --fail--> 400 invalid_request
  |
[JCS(whole body) -> canonical bytes -> body_sha256]
  |
[idempotency lookup (tenant_id, agent_id, run_id)] -- found, same hash --> 200 {proposal_id, current state}
  |                                                  -- found, other hash --> 409 idempotency_conflict (log, store nothing)
  | not found
  v
[envelope schema (proposal-request.schema.json)] --fail--> 400 schema_violation {pointer, keyword}
  |
[catalog lookup in CURRENT release] --miss--> (Phase 2 interim; Phase 4 = stored refused_policy not_in_catalog)
  |
[parameter schema (catalog entry)] --fail--> 400 schema_violation {pointer, keyword}
  |
[proposal_hash = SHA256(JCS{action_type, party_ref, parameters, justification})]
  |
[INSERT ... ON CONFLICT ON CONSTRAINT proposal_run_unique DO NOTHING RETURNING proposal_id]
  |-- row returned --> 201 + Location + {proposal_id, state}
  |-- no row (race lost) --> re-lookup -> same hash 200 / different hash 409

GET /v1/catalog/{version} --> introspect (any active key) --> release exists? 200 {catalog_version, entries} : 404
Startup: manifest + catalog/*.yaml -> YamlStream -> JSON -> D-02 vetting -> action-type schema -> fail closed
```

### Recommended Project Structure
```
src/Olive.Domain/Jcs/            # Jcs.cs (Canonicalize(JsonElement)->byte[]), Sha256Hex; BCL only
src/Olive.Application/           # IProposalStore gains FindByRunAsync; ICatalog port (optional)
src/Olive.Catalog/               # CatalogLoader (YamlStream), CatalogVetting, SchemaValidator (JsonSchema.Net), embedded contracts
src/Olive.Host/Endpoints/        # ProposalEndpoints (split: IJsonGate / BodyReader helpers), CatalogEndpoints
sql/migrations/0004_proposal_body_hash.sql
catalog/catalog.yaml + catalog/crm.case_note@1.yaml
contracts/proposal/v1/  contracts/catalog/v1/  contracts/queue-item/v0/ contracts/execution-record/v0/  contracts/jcs/v1/
```
Placement: JCS in **Olive.Domain** (leaf, BCL-only, usable from Host and tests without graph changes). Validator wrapper in **Olive.Catalog** (the only project that may take JsonSchema.Net per the allowlist change). The I-JSON gate should be extracted from the 350-line `ProposalEndpoints.cs` into its own static class in Host (or Domain if kept pure on `JsonElement`) so it is unit-testable without HTTP. Catalog and schemas must be embedded resources (like `config/agents.yaml`) so the service does not depend on the working directory; the Host test factory already relies on this pattern.

### Pattern 1: Whole-body I-JSON gate
**What:** One pass after a single parse. Order: bytes -> `JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false })` -> recursive walk.
**Verified behaviour (spiked on runtime 10.0.11):**
- `AllowDuplicateProperties = false` throws `JsonException` for `{"a":1,"a":2}`, for nested duplicates, and for names that are duplicates only after unescaping (`"a"` vs `"a"`). `[VERIFIED: spike]`
- A body whose property name is a lone-surrogate escape (`"\ud800"`) now throws **`InvalidOperationException`** from `Parse` (name materialization for the duplicate check). Phase 1 only caught `JsonException`. **Catch both.** `[VERIFIED: spike]`
- A lone-surrogate string *value* parses; `GetString()` throws `InvalidOperationException` (Phase 1 `TryGetCleanString` already handles it). Invalid UTF-8 bytes inside a string parse, then `GetString()` throws `InvalidOperationException`. A leading BOM and trailing garbage fail parse with `JsonReaderException`. `\u0000` parses and yields `'\0'` (caught by `IsWellFormed`). `[VERIFIED: spike]`
- Caution for test authors: build lone-surrogate test inputs from an explicit backslash char or escaped C# literal; a literal U+D800 in C# source is replaced with U+FFFD by `Encoding.UTF8.GetBytes` and silently becomes a valid string (this cost time during research).
**Extension required:** Phase 1 only walked `parameters`. The gate must walk the **whole** root (every property name and string value, every number) because run_id/other fields are hashed and a top-level number such as `"nono_session_id": 9007199254740993` must be rejected here, not by the schema.
**Number checks:** keep Phase 1 `IsNumberInRange` + growth budget (WR-10) and add the lossy rule (below). Keep 64-char token limit, exponent bounds.
**Lossy rule (recommended implementation of D-05) `[ASSUMED]`:** for each number token, parse to double via `GetDouble()` (reject if infinite / `TryGetDouble` false); compute the shortest round-trip digits of that double (`double.ToString("R", InvariantCulture)` -> digit string + decimal exponent); compare against the token's own digit string and exponent after stripping leading/trailing zeros and normalising the exponent. Equal -> exact (`1.0`, `1e0`, `0.1`, `100`, `1E2`, `5e-324` pass); different -> lossy (`9007199254740993`, `333333333.33333329`) -> 400. `-0` is accepted (value equals 0) and canonicalizes to `0`. Pin with a unit-test table; this rule treats "round-trips to the same *decimal text*" as the meaning of D-05's "same mathematical value", which is the only reading under which `0.1` is accepted.

### Pattern 2: In-house JCS (RFC 8785)
**What:** Walk `JsonElement`; emit to a `StringBuilder`/`ArrayBufferWriter<byte>`; no `Utf8JsonWriter` (its encoder escapes differently and cannot be configured to RFC 8785).
- Objects: collect `(name, value)`, sort names by **UTF-16 code-unit ordinal** (`string.CompareOrdinal`), recursively. Duplicates cannot occur after the gate. `[CITED: rfc-editor.org/rfc/rfc8785 section 3.2.3]`
- Strings: only `"`->`\"`, `\`->`\\`, `\b \t \n \f \r` short forms, other `< 0x20` as lowercase `\u00xx`; everything else (including `/`, `<`, non-ASCII, U+007F, U+0080) emitted literally; output UTF-8. Confirmed by the cyberphone `weird.json` and `values.json` outputs (`\u000f` lowercase, `/` unescaped, `\\\\` preserved). `[VERIFIED: raw.githubusercontent.com cyberphone testdata]`
- Numbers: ECMAScript `Number::toString`. Take the double, get shortest digits `d` and exponent `n` (via `"R"` / `"E16"` parsing), then: `-0 -> "0"`; if `21 >= n >= k` print digits followed by zeros; if `0 < n <= 21` insert decimal point; if `-6 < n <= 0` print `0.` + zeros + digits; otherwise exponent form with `e+`/`e-` (e.g. `1e+30`, `1e-27`, `5e-324`, `1.7976931348623157e+308`). .NET's `ToString("R")` prints `1E+21`/`1E-07` so it cannot be used as-is. Appendix B of RFC 8785 supplies IEEE-754 hex -> text pairs; transcribe them as unit-test data. `[CITED: RFC 8785 Appendix B]` (transcription is an implementation task; not re-fetched here).
- Literals `true/false/null` verbatim; arrays preserve order; no whitespace.
- NaN/Infinity cannot appear (gate rejects). Whole-document output is UTF-8 **without BOM**.
**Hash:** `SHA256.HashData(bytes)` -> `Convert.ToHexStringLower`. `body_sha256` hashes the whole body; `proposal_hash` hashes `{"action_type":..,"justification":..,"parameters":..,"party_ref":..}` built as a JCS object of exactly these four keys (sorted automatically). Note a missing key must not be silently omitted: the envelope schema should require all four (see Open Question 1), so `proposal_hash` is always defined.

### Pattern 3: Idempotency lookup + race
```csharp
// Sketch only. Order matches Decision §15.2 step 3.
var existing = await store.FindByRunAsync(key.TenantId, key.AgentId, runId, ct);   // select proposal_id, state, body_sha256 where tenant_id=@t and agent_id=@a and run_id=@r
if (existing is not null)
    return existing.BodySha256 == bodySha ? Replay(existing) : Conflict(...);       // 200 / 409; never call trace resolver

// ... schema + catalog validation (400 stores nothing) ...

var id = await store.InsertAsync(key.TenantId, proposal, ct);                        // ON CONFLICT ON CONSTRAINT proposal_run_unique DO NOTHING RETURNING proposal_id
if (id is null)                                                                      // lost the race
{
    existing = await store.FindByRunAsync(key.TenantId, key.AgentId, runId, ct);     // loser re-enters step 3 (fresh READ COMMITTED snapshot)
    return existing is not null && existing.BodySha256 == bodySha ? Replay(existing) : Conflict(...);
}
```
- `InsertAsync` already returns `null` on conflict (Phase 1 D-10). Replace the endpoint's `DuplicateRun` 409 with the re-lookup above.
- Postgres semantics `[CITED: postgresql.org/docs/current/sql-insert.html]`: under READ COMMITTED a second `INSERT ... ON CONFLICT DO NOTHING` blocks until the first transaction commits or aborts, then (on commit) inserts nothing and returns no row. A subsequent statement sees the committed row. If the winner rolls back the loser inserts. `TenantUnitOfWork` uses the default isolation (READ COMMITTED), so the sketch holds. Do **not** raise isolation to SERIALIZABLE for this.
- Privileges spike (roadmap note): `ON CONFLICT DO NOTHING` needs INSERT on the inserted columns; `RETURNING` needs SELECT on the returned column; with RLS the `WITH CHECK` insert policy applies, and DO NOTHING does not need UPDATE or a SELECT policy beyond what `olive_app` already has. Phase 1 already exercises exactly this statement with `olive_app`, so no extra grant beyond the new columns is needed. The lookup runs as `olive_app` (has SELECT). `[ASSUMED]` for the claim that no further privilege is required; the existing integration test passing is the evidence, and the new concurrency test (below) is the proof.
- Replay response: `200` with `{proposal_id, state}` (same shape as 201, per submit-response schema). Repeating `Location` is recommended (harmless, lets clients follow it). The state is read from the row at lookup time ("current").
- 409 logging: `ILogger.LogWarning` with static template; fields: tenant_id, agent_id, existing proposal_id, truncated stored hash and received hash (first 12 hex). Never log run_id? run_id is not secret but keep to ids/hashes only. Never log body, parameters, key, or exception text. Use the existing `InMemoryLogCapture` test double to assert no body/key substrings and that the line exists.

### Pattern 4: JsonSchema.Net validation (8.0.5, spiked)
```csharp
// Source: spike against JsonSchema.Net 8.0.5 (2026-10-21)
var schema = JsonSchema.FromText(schemaText);
var opts = new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
var r = schema.Evaluate(rootElement, opts);                 // takes the already-parsed JsonElement: no re-serialization, no mutation
if (!r.IsValid)
{
    var first = r.Details.First(d => d.Errors is not null);  // pointer: first.InstanceLocation.ToString()
    // keyword: first.Errors.Keys.First() ("type", "format", ...). For additionalProperties:false the detail has an
    // empty error key at /z; derive the keyword from first.EvaluationPath's last segment in that case.
}
```
Observed: `"format":"uuid"` fails an invalid string only with `RequireFormatValidation = true`; `"n":"5"` against `integer` fails on `type` (no coercion); extra property fails at `/z`; `1.0` satisfies `integer` (valid under 2020-12). Build the first-error rule as "first detail with errors, in List order", return `(pointer, keyword)` only, and a test asserting the response JSON does not contain the offending value. Cache compiled `JsonSchema` instances (immutable, thread-safe per library docs; confirm in unit test with parallel calls). Register no `SchemaRegistry.Fetch` delegate (REQ-ACT-04) and run a vetting pass that rejects any `$ref` whose value does not begin with `#` or an embedded-registered `$id`.
`[VERIFIED: spike]` for API shape; `[ASSUMED]` for thread safety.
**Format allowlist (recommendation for D-02):** `uuid`, `date-time`, `date`, `uri`, `uri-reference`, `email`? Keep minimal: `uuid`, `date-time`, `date`, `uri` (Olive uses `payload://`, `case://` reference strings, so prefer `pattern` for schemes). Anything else fails startup.

### Pattern 5: Catalog loader (YamlDotNet) -> JSON
- **Pitfall found:** `Deserialize<Dictionary<string,object>>` returns every scalar as `string` (`additionalProperties: false` -> `"false"`), and `WithAttemptingUnquotedStringTypeDeserialization()` returns mixed CLR types (`Byte` for `limit: 10`). `[VERIFIED: spike on YamlDotNet 18.1.0]`
- **Pitfall found:** typed `Deserialize<T>` (the `AgentAllowlist` pattern) and dictionary deserialization **silently accept duplicate keys (last wins)**. `YamlStream.Load` throws `YamlException` on duplicate mapping keys. `[VERIFIED: spike]` So `AgentAllowlist`'s pattern alone is not enough for a security-relevant catalog.
- **Recommended loader:** `YamlStream` -> walk nodes -> `System.Text.Json.Nodes.JsonNode`. Resolve plain scalars with the YAML core schema (`true/false`, integers, floats, `null`), keep quoted scalars as strings. Then (a) bind the top-level entry strictly (`JsonSerializerOptions { UnmappedMemberHandling = Disallow }` into a record: `action_id`, `target_system`, `parameters` as `JsonElement`, `required_purpose`, `required_approval`, `reversible`, `max_rate`), (b) validate the entry JSON against `contracts/catalog/v1/action-type.schema.json` with the same validator, (c) run D-02 vetting on the `parameters` subtree, (d) check `required_purpose` values against `contracts/vendored/vocabulary.yaml` purposes (existing scaffold behaviour; `VendoredContracts` helper exists), (e) verify `action_id` matches the file name and `^[a-z0-9_.]+@[0-9]+$`.
- Manifest `catalog/catalog.yaml`: `catalog_version: 1`, `entries: [crm.case_note@1]`. Startup: set of manifest entries must equal set of `catalog/*.yaml` (excluding the manifest) and every entry must load. Mismatch -> `StartupGuardException`. Embed `catalog/*.yaml` the same way as `config/*.yaml` (`EmbeddedResource` with `LogicalName`) so the Host does not read the working directory.
- Existing `catalog/crm.case_note@1.yaml` satisfies D-02 as is (`additionalProperties: false`, no `$ref`, no `default`, no `format`). Its `parameters` object lacks a `$schema` key; JsonSchema.Net defaults to the latest supported draft, so **set the dialect explicitly** (wrap or pass `BuildOptions { Dictionary dialect }` / require `$schema`: pick one and test that a 2020-12-only keyword such as `prefixItems` is honoured).
- Action-type schema (`contracts/schemas/action-type.schema.json`) currently constrains `parameters` to a tiny shape (`type`, `additionalProperties:const false`, `properties`, `required`) and `additionalProperties:false` on that level: nested schema keywords like `pattern`/`format` inside `properties` are allowed because `properties` is `{"type":"object"}`; but top-level `$defs`, `description`, `maxLength` etc. at parameters level would be rejected. Decide whether to relax when moving it to `contracts/catalog/v1/`.

### Pattern 6: Catalog endpoint
`app.MapGet("/v1/catalog/{version:int}", ...).RequireAuthorization()` with no principal-type check. Non-integer version -> use `{version}` string and 404 `catalog_version_not_found` for anything unparseable (avoid routing 404 with no problem body; the custom handler must emit `application/problem+json`). 200 body `{catalog_version, entries}` built from the stored JSON (return `JsonElement` so numbers/booleans pass through unchanged).

### Anti-Patterns to Avoid
- **Re-serializing the parsed body** (model binding or `JsonSerializer` round trips) before hashing or validating: this is exactly the mutation §17.2 forbids. Hash from the `JsonElement` of the original bytes.
- **Hashing after schema validation but via a different parse** (e.g. `JsonNode`): number/escape handling may differ. Use one `JsonDocument`.
- **Storing only jsonb `parameters` and recomputing the hash from it**: jsonb reorders keys and normalizes numbers (D-06 is the fix: store the canonical text).
- **Catching exceptions to coerce** (e.g. `GetInt32` fallback) in the envelope check: use the schema only.
- **Fetching `$ref`s**: no network (REQ-ACT-04, and `BannedSymbols.txt`/hygiene tests).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| JSON Schema validation | A keyword-by-keyword checker (the test-only `JsonSchemaLite` is a stand-in) | JsonSchema.Net (D-01) | Dialect semantics, `$ref`, `unevaluatedProperties`, format assertion. Replace `JsonSchemaLite` uses (IntrospectionFixtureTests) with the real validator so the fixture check is not weaker than production. |
| YAML parsing | Line-based reader | YamlDotNet `YamlStream` | Already a project dependency; D-14 |
| Duplicate-key detection in JSON | A custom tokenizer | `JsonDocumentOptions.AllowDuplicateProperties = false` | Handles escaped-equivalent names (spiked) |
| SHA-256 / hex | Own implementation | `SHA256.HashData`, `Convert.ToHexStringLower` | BCL |
| ES6 double -> string digits | Own shortest-digit algorithm | .NET `double.ToString("R")` for digits, then only re-format | .NET Core 3.0+ gives shortest round-trippable digits; only the layout (exponent thresholds) is custom |
| JCS itself | (n/a) | **Do hand-roll** per API-09; no third-party canonicalizer | Required in-house; validate with the vectors |

**Key insight:** the only genuinely custom code is JCS layout/escaping and the lossy-number comparison; both are small and fully specified by test vectors, so test them first.

## Runtime State Inventory

Not a rename/refactor phase, but it moves files and changes a schema, so explicitly:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Existing `proposal` rows from Phase 1 (dev/test DBs only) have no `body_sha256`/canonical bytes/`proposal_hash`. | Migration 0004: add columns nullable (with `check (body_sha256 ~ '^[0-9a-f]{64}$')` when not null) or NOT NULL if the team confirms no persistent Phase 1 data exists; a NULL-hash legacy row must be treated as `409` by the lookup, never as a match. Testcontainers DBs are rebuilt per test, so no test impact. |
| Live service config | None - verified: no external services hold the contract paths | none |
| OS-registered state | None | none |
| Secrets/env vars | None new | none |
| Build artifacts | `packages.lock.json` in all 12 projects regenerate when a package is added to Catalog (only Catalog + dependents change); stale `contracts/schemas` references in docs | Regenerate lockfiles; grep and update `CLAUDE.md`, `README.md`, `docs/STATUS.md`, `AGENTS.md` paths for moved contracts. Tests currently do not reference `contracts/schemas` (grep: only `CLAUDE.md` hits). |

## Common Pitfalls

### Pitfall 1: Lone-surrogate names now throw from Parse
**What goes wrong:** With duplicate checking on, `{"\ud800":1}` throws `InvalidOperationException` during `JsonDocument.Parse`, which Phase 1 does not catch -> 500.
**How to avoid:** `catch (Exception e) when (e is JsonException or InvalidOperationException)` around Parse; keep `API_01_lone_surrogate...` tests (they will now cover the Parse path for the name cases). Add a log-free 400.
**Warning signs:** `Assert.False(factory.Logs.HasErrorOrAbove)` in the existing test starts failing.

### Pitfall 2: Cyberphone `values.json` vs the lossy-number gate
**What goes wrong:** `333333333.33333329` is required JCS input (expected `333333333.3333333`) yet fails D-05. If the shared manifest expects every `kind: body` input to be accepted, either the vector or the gate is wrong; hometown could also diverge.
**How to avoid:** In `vectors.json` give each case two verdicts: `canonicalize` (output bytes + sha256 for the pure JCS function, which accepts any finite JSON number per RFC) and `gate` (`accept` | `reject_lossy_number` | `reject_duplicate_key` | `reject_lone_surrogate`). Tests: JCS unit tests run the first for all non-reject cases; gate tests run the second. Tell hometown that `gate` is Olive's I-JSON policy so check 5 cannot diverge for honest traffic (D-18). Flag in Open Questions.

### Pitfall 3: Required-field set for the envelope
**What goes wrong:** Making all six fields required (matching API-01 text and giving `proposal_hash` four always-present keys) breaks every Phase 1 test that posts `{"run_id":"r"}`-style partial bodies expecting 201 (about 37 body literals across the test suite). Leaving fields optional leaves `proposal_hash` ill-defined.
**How to avoid:** See Open Question 1; schedule the test-fixture update as an explicit task inside the first vertical slice that adds the envelope schema, not as an afterthought.

### Pitfall 4: Silent YAML mistakes
**What goes wrong:** Duplicate YAML keys (last wins) and scalar-as-string typing produce a catalog that validates but means something else (`additionalProperties: "false"` is not `false`).
**How to avoid:** `YamlStream` loader (Pattern 5) plus a test with a duplicate-key catalog file and a quoted-vs-plain scalar test.

### Pitfall 5: Hashing what was validated vs what was received
**What goes wrong:** Any helper that trims `run_id`, normalizes Unicode, or converts jsonb text before hashing changes the hash and breaks check 5 later (§17.2).
**How to avoid:** Single `JsonDocument`; JCS from `JsonElement`; unit test that a body with a lone `" "`-padded value hashes with the spaces and fails the schema only if the schema says so (no trim).

### Pitfall 6: Number representation drift on GET
**What goes wrong:** `parameters` is stored as jsonb (`1.0` -> `1.0`, `1E2` -> `100`); GET returns jsonb text, not the original. That is acceptable (it is a query column) but the **canonical text column is the audit source**. Do not try to make GET reproduce the original bytes.

### Pitfall 7: Migration plumbing
**What goes wrong:** `MigrationRunnerTests` hard-codes `["0001","0002","0003"]` and `PrivilegeMatrixTests` pins the INSERT column list; `MigrationCatalog` requires contiguous versions and pinned checksums.
**How to avoid:** Add `0004_proposal_body_hash.sql` only; update both test expectations in the same task. New grant: `grant insert (canonical_body, body_sha256, proposal_hash) on proposal to olive_app;` (additive, no revoke needed). Keep UPDATE limited to `state` (columns are immutable after insert, Decision 13). Consider `check (octet_length(canonical_body) <= 1048576)` and a unique/index on `(tenant_id, agent_id, run_id)` already exists (constraint).

### Pitfall 8: Hash column vs stored bytes drift
**What goes wrong:** If code ever writes `body_sha256` without `canonical_body` (or from different bytes), the audit property is lost.
**How to avoid:** Compute both from one `byte[]` in one place; add a DB-level check is not possible for SHA-256 without pgcrypto (`digest`) - instead add an integration test that reads a stored row and recomputes SHA-256 of `canonical_body` and compares.

### Pitfall 9: Test authors' lone surrogates
See Pattern 1 caution: construct test bodies with `"\\" + "ud800"` escapes or raw bytes, never a literal `\uD800` char inside a C# string that is then UTF-8 encoded.

## Code Examples

### JCS number layout (pseudo-C#)
```csharp
// Source: ECMA-262 Number::toString as required by RFC 8785 section 3.2.2.3
static string Es6(double v)
{
    if (v == 0) return "0";                                   // also -0
    var neg = v < 0; v = Math.Abs(v);
    var r = v.ToString("E16", CultureInfo.InvariantCulture);  // use "R" for shortest digits, then parse mantissa/exponent
    // derive digits (no trailing zeros) and n (position of decimal point), k = digits.Length
    // if (k <= n && n <= 21) digits + zeros(n-k)
    // if (0 < n && n <= 21) digits[..n] + "." + digits[n..]
    // if (-6 < n && n <= 0) "0." + zeros(-n) + digits
    // else exponent form: d[.ddd]e(+|-)(n-1)
    return (neg ? "-" : "") + /* layout */ "";
}
```
Unit tests: Appendix B table, plus `1E30 -> 1e+30`, `4.50 -> 4.5`, `2e-3 -> 0.002`, `0.000000000000000000000000001 -> 1e-27`, `333333333.33333329 -> 333333333.3333333`.

### Golden-vector manifest sketch (`contracts/jcs/v1/vectors.json`)
```json
{
  "version": 1,
  "source": ["rfc8785", "cyberphone/json-canonicalization (Apache-2.0)", "olive"],
  "cases": [
    { "name": "cyberphone-values", "kind": "body", "input": "cyberphone-values.input.json", "canonical": "cyberphone-values.canonical.json", "sha256": "<hex>", "gate": "reject_lossy_number" },
    { "name": "olive-proposal-hash-basic", "kind": "proposal_hash", "input": "...", "canonical": "...", "sha256": "<hex>", "gate": "accept" },
    { "name": "dup-key", "kind": "body", "input": "dup-key.input.json", "expect": "reject", "reason": "duplicate_key" }
  ]
}
```
Cyberphone vectors to import (all in `testdata/input` + `testdata/output`): `arrays`, `french`, `structures`, `unicode`, `values`, `weird`. Attribution: Apache-2.0, copyright 2018 Anders Rundgren; commit the license notice in `contracts/jcs/v1/README.md`. Create `.input.json` files with exact bytes (disable git EOL conversion: check `.gitattributes` and add `contracts/jcs/v1/* -text` so CRLF conversion on Windows cannot alter bytes). `[VERIFIED: github API listing of testdata files]`
`vectors.json` must be validated by a schema in the same folder (follow the `contracts/introspection/` README + index style).

### Replay/conflict responses
```csharp
// 200 replay: same shape as 201
return Results.Json(new ProposalCreatedResponse(existing.ProposalId, existing.State), statusCode: StatusCodes.Status200OK);
// 409
return ProblemResults.Result(StatusCodes.Status409Conflict, ProblemReasons.IdempotencyConflict);
```
Add `IdempotencyConflict = "idempotency_conflict"`, `SchemaViolation = "schema_violation"`, `CatalogVersionNotFound = "catalog_version_not_found"`, (interim) `NotInCatalog = "not_in_catalog"`; remove `DuplicateRun` and its title entry and update the Phase 1 tests that reference it (`API_01_duplicate_run_gives_409...`). The `schema_violation` problem needs extension members `pointer` and `keyword` (static strings only; pointer is built from property *names* in the schema/instance - names are attacker-controlled, so cap length and ensure it contains no values; it may echo a property name from the body, which is acceptable since D-03 forbids values, not names; consider also truncating to 256 chars).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `JsonDocument` silently accepts duplicate keys | `JsonDocumentOptions.AllowDuplicateProperties` (.NET 9+) | .NET 9 | Native duplicate rejection; no custom tokenizer `[VERIFIED: spike on 10.0.11]` |
| JsonSchema.Net MIT | JsonSchema.Net 9.x under OSMF EULA | 9.0.0 | Pin 8.0.5 or accept EULA |
| Hand-rolled YAML | YamlDotNet YamlStream | this phase | duplicate keys detected |

**Deprecated/outdated:** `contracts/schemas/*`, `contracts/examples/*` (moved); `JsonSchemaLite` test helper (replace); `ProblemReasons.DuplicateRun`.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Lossy rule = literal decimal digits/exponent must equal the shortest round-trip text of its double | Pattern 1 | Different reading of D-05 would accept/reject different edge numbers; hometown would diverge. Confirm with user; pin with table tests. |
| A2 | No privilege beyond new-column INSERT is needed for `ON CONFLICT DO NOTHING` + re-lookup as `olive_app` | Pattern 3 | Migration would need another grant; caught by the concurrency integration test. |
| A3 | `JsonSchema` instances are thread-safe and can be cached | Pattern 4 | Per-request compile cost, or races; parallel unit test checks. |
| A4 | JsonSchema.Net defaults to a draft where 2020-12 keywords apply when `$schema` is absent | Pattern 5 | Wrong dialect would weaken parameter validation; require/force `$schema` or dialect option and test. |
| A5 | Format allowlist `uuid, date-time, date, uri` is sufficient | Pattern 4 | Catalog entries needing other formats fail startup; extend the list in review. |
| A6 | Interim POST behaviour for unknown `action_type` before Phase 4 is a non-stored 400 `not_in_catalog` | Open Question 2 | If the user prefers a different interim code, change one mapping. |
| A7 | slopcheck verdict not obtained (CLI lacks `--json`); packages vetted manually | Package audit | Low; packages are long-established, same publisher. |

## Open Questions

1. **Which envelope fields are required?**
   - Known: API-01 lists `action_type, party_ref, parameters, justification, trace_ref, run_id` and optional `nono_session_id`. Phase 1 stores most as nullable. `proposal_hash` needs all four proposal keys.
   - Unclear: whether partial bodies must stay valid (Phase 1 tests post them).
   - Recommendation: require `run_id, action_type, party_ref, parameters, justification, trace_ref`; optional `nono_session_id`; update the Phase 1 success-path fixtures in the same plan. The gate tests (surrogates, numbers, size) keep passing unchanged because they expect 400.
2. **Interim handling of unknown `action_type`** (stored refusal is Phase 4): recommend a non-stored 400 `not_in_catalog` behind one clearly marked method that Phase 4 replaces. A6.
3. **JsonSchema.Net licence/version:** pin 8.0.5 (MIT) vs 9.4.1 (OSMF EULA). Recommend 8.0.5 unless the user decides otherwise. Needs a user decision because D-01 names the library but not the version.
4. **Golden vector vs gate semantics** (Pitfall 2): confirm the two-verdict manifest with the hometown owner (Q20).
5. **Nullable vs NOT NULL hash columns** in 0004 (Runtime State Inventory): recommend nullable + format check unless the team confirms no durable Phase 1 data.
6. **Action-type schema rigidity** when moved to `contracts/catalog/v1/` (Pattern 5 last bullet).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | yes | 10.0.400 (global.json 10.0.204, rollForward latestFeature) | - |
| .NET runtime | tests | yes | 10.0.11 and 10.0.8 | - |
| Docker | Testcontainers (integration tests) | yes | daemon running | skip Integration category |
| NuGet (nuget.org) | restoring JsonSchema.Net | yes | reachable; 8.0.5 resolved in spike | local cache has 7.4.0 only |
| Python/pip | slopcheck | yes | installed; CLI has no `--json` | manual vetting |

**Missing with no fallback:** none.

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 on Microsoft.NET.Test.Sdk 18.0.0, Testcontainers.PostgreSql 4.16.0, Mvc.Testing |
| Config file | `tests/Olive.Tests/Olive.Tests.csproj` (single project); traits: `Category=Integration` for DB/HTTP, `Category=Architecture` |
| Quick run command | `dotnet test tests/Olive.Tests --filter "Category!=Integration"` |
| Full suite command | `dotnet test tests/Olive.Tests` (needs Docker) |

### Phase Requirements -> Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| API-09 | JCS matches RFC 8785 App. B numbers and all cyberphone vectors; golden vectors in `contracts/jcs/v1/` match recomputed bytes/sha | unit | `dotnet test tests/Olive.Tests --filter "FullyQualifiedName~API_09"` | no, Wave 0 (`JcsTests.cs`) |
| API-09 | Negative vectors rejected by the gate (dup key, lone surrogate, lossy number) | unit | same filter | no, Wave 0 |
| API-04 | Duplicate keys (incl. escaped-equal, nested) -> 400, nothing stored; null optional -> 400; no coercion (`"5"` for integer) | integration | `--filter "FullyQualifiedName~API_04"` | no, Wave 0 (`ApiIntegrityTests.cs`) |
| API-04 | Lone-surrogate *name* does not 500 (Pitfall 1) | integration | `--filter "FullyQualifiedName~API_01_lone|API_04"` | partly (existing `API_01_lone_*`) |
| API-05 | proposal-request/proposal/response schemas validate their valid examples and reject invalid ones with format assertion on | unit | `--filter "FullyQualifiedName~API_05"` | no, Wave 0 |
| API-06 | Same body replay -> 200 same id + current state (after a transition); different body -> 409, no new row, log line has no body/key; `nono_session_id`-only change -> 409; run_id missing/invalid -> 400 before idempotency | integration | `--filter "FullyQualifiedName~API_06"` | no, Wave 0 |
| API-07 | N parallel first submissions -> exactly one row, one 201, rest 200 (same body) or 409 (different body) | integration | `--filter "FullyQualifiedName~API_07"` | no, Wave 0 |
| API-03 | GET catalog release 1 returns `crm.case_note@1` with parameter schema; 404 for unknown; 401 inactive; any principal type OK | integration | `--filter "FullyQualifiedName~API_03"` | no, Wave 0 |
| API-10 | Catalog loads via YamlDotNet; D-02 violations (missing additionalProperties:false, remote `$ref`, `default`, bad `format`), duplicate YAML keys, manifest/disk mismatch each fail startup; no runtime registration symbol exists | unit + architecture | `--filter "FullyQualifiedName~API_10"` | no, Wave 0 |
| API-06/D-06/D-08 | Stored `canonical_body` hashes to `body_sha256`; `proposal_hash` equals vector; column grants (INSERT allowed, UPDATE denied) | integration | `--filter "FullyQualifiedName~DB_|API_06"` | extend `PrivilegeMatrixTests`, `MigrationRunnerTests` |
| FND-02 | Package allowlist includes `JsonSchema.Net` for Catalog | architecture | `--filter "Category=Architecture"` | exists; update |

### Sampling Rate
- **Per task commit:** `dotnet test tests/Olive.Tests --filter "Category!=Integration"` (JCS, schema, catalog vetting, architecture; seconds)
- **Per wave merge:** full `dotnet test tests/Olive.Tests`
- **Phase gate:** full suite green plus `dotnet restore --locked-mode` and the vulnerable-package gate before `/gsd-verify-work`

### Wave 0 Gaps
- [ ] `tests/Olive.Tests/JcsTests.cs` (+ vector loader) covers API-09
- [ ] `tests/Olive.Tests/IJsonGateTests.cs` (unit: gate verdicts incl. lossy-number table) covers API-04/D-05
- [ ] `tests/Olive.Tests/IdempotencyTests.cs` (integration) covers API-06/07
- [ ] `tests/Olive.Tests/CatalogTests.cs` (unit + integration) covers API-03/API-10
- [ ] `tests/Olive.Tests/ContractSchemaTests.cs` covers API-05; replace `JsonSchemaLite` usage in `IntrospectionFixtureTests`
- [ ] Framework install: none (existing). New package: JsonSchema.Net via Catalog

## Security Domain

### Applicable ASVS Categories (level 1)

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | yes (unchanged) | popeye introspection first on every request including replays and catalog GET |
| V3 Session Management | no | stateless key per request |
| V4 Access Control | yes | principal/allowlist checks before body; tenant predicate on the idempotency lookup; catalog open to any active key (D-11) |
| V5 Input Validation | yes | I-JSON gate, JsonSchema.Net closed schemas, size/depth/number budgets |
| V6 Cryptography | yes | `SHA256.HashData` from BCL only; no custom hash |
| V8/V7 Logging | yes | 409 and 400 logs carry no bodies, parameters, keys, or exception text |

### Known Threat Patterns

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Duplicate-key smuggling (two parsers see different values) | Tampering | `AllowDuplicateProperties=false` before hashing/validation |
| Hash/validation mismatch (parser differential, coercion) | Tampering | One parse, no re-serialization, closed schemas, no `default` |
| Number-precision collision (two values, one hash) | Tampering/Repudiation | Lossy-number rejection (D-05) |
| Cross-tenant replay/probing via idempotency lookup | Information disclosure | `tenant_id` predicate + RLS on lookup; 409 reveals only same-tenant state |
| Resource exhaustion (huge numbers, depth, bodies) | DoS | 1 MiB cap, MaxDepth 64, number budget (WR-10), exponent caps |
| SSRF via schema `$ref` | Information disclosure/EoP | Offline-only schemas; vetting rejects remote refs; no fetch delegate |
| Secret/value leakage in problem details/logs | Information disclosure | Static titles; pointer+keyword only |
| Catalog tampering at runtime | Tampering | File-only, embedded, manifest-checked at startup; no registration API |
| Stored-vs-hashed drift | Repudiation | Store canonical bytes + sha; recompute test |

## Sources

### Primary (HIGH confidence)
- Spikes executed on this machine (runtime 10.0.11, SDK 10.0.400): `AllowDuplicateProperties`, lone surrogate/invalid UTF-8/BOM behaviour, YamlDotNet scalar typing and duplicate-key behaviour, JsonSchema.Net 8.0.5 evaluate/format/error shape.
- Codebase: `ProposalEndpoints.cs`, `ProposalSql.cs`, `PostgresProposalStore.cs`, `TenantUnitOfWork.cs`, `AgentAllowlist.cs`, `ArchitectureTests.cs`, migrations 0001-0003, `Directory.Packages.props`, `ProblemReasons.cs`, `ProblemResults.cs`.
- `specs/DECISION_olive-poc2-phase1_v0.5.md` §6, §12, §15.2, §15.3, §17.2.
- nuget.org flat-container nuspecs for JsonSchema.Net 7.4.0 / 8.0.5 / 9.0.0 / 9.4.1, JsonPointer.Net 5.3.1 / 6.0.1 / 7.0.3, Json.More.Net 2.1.1 / 2.2.0 / 3.0.1.
- https://www.rfc-editor.org/rfc/rfc8785.txt (sorting rules, sample in §3.2.2/3.2.3).
- https://github.com/cyberphone/json-canonicalization/tree/master/testdata (file listing; `values.json`, `weird.json` inputs and outputs fetched; Apache-2.0).
- https://raw.githubusercontent.com/json-everything/json-everything/master/OSMFEULA.txt (fee terms).

### Secondary (MEDIUM confidence)
- PostgreSQL INSERT / ON CONFLICT semantics (documentation knowledge; the Phase 1 integration test exercises the statement).

### Tertiary (LOW confidence)
- RFC 8785 Appendix B numeric table values were not re-fetched in this session; transcribe from the RFC during implementation.

## Metadata

**Confidence breakdown:**
- Standard stack: MEDIUM-HIGH. Library choice locked; version/licence verified; thread-safety and dialect default assumed.
- Architecture: HIGH. Fits the existing graph and Phase 1 patterns; integration points read directly.
- Pitfalls: HIGH. Most were reproduced by spike.

**Research date:** 2026-10-21
**Valid until:** 2026-11-20 (JsonSchema.Net and YamlDotNet release monthly; re-check licence line before locking if 9.x is chosen)
