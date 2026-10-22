# Phase 2: Contract Integrity and Idempotent Submission - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning

<domain>
## Phase Boundary

A submitted body is validated exactly as received and never mutated. Resubmissions under the same `(agent_id, run_id)` converge on one proposal through an RFC 8785 JCS `body_sha256`. The source-controlled catalog is loaded from YAML with YamlDotNet and served at `GET /v1/catalog/{version}`.

Requirements: API-03, API-04, API-05, API-06, API-07, API-09, API-10.

**Not in this phase:**
- The stored `not_in_catalog` refusal is Phase 4 (refusal machinery).
- Trace resolution and check 5 (`proposal_hash` comparison) are Phase 3.
- OpenAPI 3.1 and `COMPATIBILITY.md` are Phase 9 (EVD-04/05).
- API-08 (the full order-of-operations test) is not in this phase's list. The idempotency → schema/catalog part of the §15.2 order still applies here.

</domain>

<decisions>
## Implementation Decisions

### JSON Schema validation
- **D-01:** Validate with **JsonSchema.Net** (json-everything), using 2020-12 with **format assertion on**. Add it and its transitive packages (e.g. `Json.More.Net`, `JsonPointer.Net`) to Central Package Management, the lockfiles, and the FND-02 package allowlist test. One validator covers both the v1 envelope and the catalog parameter schemas.
- **D-02:** Catalog parameter schemas must belong to a **vetted subset**, checked at startup. Startup fails if any of these rules is broken:
  - every object schema has `additionalProperties: false`;
  - no remote `$ref`. Resolution is offline only, so there is no network fetch (REQ-ACT-04);
  - no `default` keyword anywhere;
  - `format` values come from an explicit allowlist.

  The rules make parameter schemas closed and non-mutating by construction. This is the same vetting that API-10's "validated against the action-type schema" startup check applies.
- **D-03:** A schema failure is a **400** whose problem detail carries:
  - a stable reason code (e.g. `schema_violation`);
  - the **instance JSON Pointer** and **failing keyword** of the **first** error;
  - **never the value**.

  Nothing is stored, so the caller can fix the body and resubmit under the same `run_id` (§15.2).
- **D-04:** The Phase 1 hand-written raw-input checks become the explicit **Q1 pre-idempotency I-JSON gate**. These are lone surrogates, NUL, number bounds, depth and the size cap, together with the new duplicate-key and lossy-number checks. A body that is unparseable, not I-JSON, or has a missing or invalid `run_id` gets 400 **before** the idempotency lookup. Schema validation stays a separate, later step (§15.2 step 4). The existing Phase 1 tests must keep passing.

### JCS canonicalization and hashing
- **D-05:** **Lossy numbers are rejected** at the I-JSON gate (400). A number is lossy when its IEEE-754 double does not round-trip to the same mathematical value; examples are `9007199254740993` and over-long decimals. Spellings that the double keeps exact (`1.0`, `1`, `1e0`) are accepted, and JCS hashes them identically. As a result, two bodies with different numeric values can never share a hash. The JCS implementation is in-house (API-09).
- **D-06:** The proposal row stores **the canonical JCS bytes** (text) that the hash was computed over, plus `body_sha256`, plus the existing jsonb columns for querying. Anyone can recompute the hash from stored data at audit time. This needs a new forward-only migration (`0004_…`). Migrations 0001–0003 are checksum-pinned and must not be edited.
- **D-07:** `body_sha256` covers the **whole v1 request body**, `nono_session_id` included, exactly as §15.3 says. A retry whose only change is a new `nono_session_id` is a different body and gets `409 idempotency_conflict`. Liam must resend the original body.
- **D-08:** Phase 2 also **computes and stores the four-key `proposal_hash`**: SHA-256 over JCS of exactly `action_type`, `party_ref`, `parameters` and `justification` (§17.2). It uses the same JCS code and gets its own column. Phase 3's check 5 is then a pure comparison.

### Catalog endpoint and manifest
- **D-09:** `{version}` in `GET /v1/catalog/{version}` is a **catalog release** number. For example, `GET /v1/catalog/1` returns every entry in release 1.
- **D-10:** The release comes from a **source-controlled manifest**, `catalog/catalog.yaml`, which declares `catalog_version` and lists the entries in it (e.g. `crm.case_note@1`). Startup fails if the manifest and the `catalog/*.yaml` files on disk disagree. Bumping the release is a reviewed source change, like any other catalog edit. There is still no runtime registration path (REQ-ACT-06).
- **D-11:** **Any active key** may call the catalog endpoint. Introspection still runs first, and an inactive key gets 401, but there is no principal-type or allowlist restriction. The catalog is not tenant data.
- **D-12:** An unknown release gets **404** (problem+json, stable reason code). A known release gets **200** with `{catalog_version, entries: [...]}`. Each entry is the full YAML entry served as JSON: `action_id`, `target_system`, `parameters` (JSON Schema), `required_purpose`, `required_approval`, `reversible`, `max_rate`.
- **D-13:** On POST, `action_type` is looked up in the **current release only**, which is the highest `catalog_version` in the manifest. An `action_type` that isn't there is `not_in_catalog`; the stored refusal itself is Phase 4. Older releases stay readable through GET for audit. Only one release exists in this phase.
- **D-14:** The YamlDotNet catalog loader replaces the old hand-rolled parser and follows the strict-mapping pattern in `AgentAllowlist` (no `IgnoreUnmatchedProperties`, fails closed). The scaffold parser is already gone with `Domain.cs` (Phase 1 D-05). Make sure nothing like it comes back.

### Contracts layout and golden vectors
- **D-15:** **Move all old contract schemas into versioned folders now**, each with paired valid and invalid examples:
  - proposal → `contracts/proposal/v1/`
  - action-type → `contracts/catalog/v1/`
  - queue-item and execution-record → versioned folders beside them

  `contracts/schemas/` and `contracts/examples/` are then removed. queue-item and execution-record are **placeholders** until Phases 5 and 6 reshape them, so label them as such. Update CLAUDE.md's contract-path conventions to match. Do not touch `contracts/vendored/`.
- **D-16:** `contracts/proposal/v1/` contains:
  - `proposal-request.schema.json`: the POST body. It has the 7 v1 fields, `additionalProperties: false`, and no `null` for optional fields.
  - `proposal.schema.json`: the GET resource.
  - a submit-response schema for the 201 and 200 replies (`{proposal_id, state}`).

  Each comes with paired valid and invalid examples.
- **D-17:** The shared JCS golden vectors live in `contracts/jcs/v1/` (Q20):
  - each case is a pair of files: `<name>.input.json` and `<name>.canonical.json` (exact canonical bytes);
  - a `vectors.json` manifest gives each case's expected SHA-256 and its `kind` (`body` or `proposal_hash`);
  - it includes the RFC 8785 and cyberphone vectors plus Olive-specific cases: the four-key `proposal_hash`, unicode and escapes, number spellings.
- **D-18:** The vectors include **negative cases marked `expect: reject`**: lossy numbers, duplicate keys, lone surrogates. hometown's canonicalizer must refuse the same inputs, so neither side hashes what the other rejects, which would make check 5 refuse honest proposals.

### Carried forward (already decided: do not re-ask)
- **§15.2 order:** introspect → principal checks → idempotency → schema and catalog. A replay never calls the trace resolver.
- **Replies:**
  - identical canonical body → `200` with the original `proposal_id` and **current** state;
  - different body → `409 idempotency_conflict`, not stored, logged;
  - concurrent first submissions → unique constraint plus re-lookup (the loser re-enters step 3).
- **Q1 and Q2:** the gate comes before idempotency. After that: envelope schema → catalog lookup → parameter schema, where a parameter-schema failure is a 400 and nothing is stored.
- **§15.3 and §17.2:** validation never mutates (no coercion, defaults, stripping, trimming or Unicode normalisation), and an optional field present as `null` gets 400. One canonical form serves the whole program.
- **Phase 1 D-09 and D-10:** 201 + `Location` + `{proposal_id, state}`, and `UNIQUE (tenant_id, agent_id, run_id)` already exists. `ProblemReasons.DuplicateRun` is replaced by `idempotency_conflict` (see the comment in `ProblemReasons.cs`).
- **Reason codes stay stable**, because Phase 9's OpenAPI documents them. Problem detail never includes parameter values.

### Claude's Discretion
- Column names and types for the JCS bytes, `body_sha256` and `proposal_hash` in migration `0004`, following the Phase 1 RLS and column-grant pattern (0002/0003). That means extending the column-level INSERT grant and the privilege-matrix test.
- The exact format allowlist for D-02, and the exact reason-code names (e.g. `schema_violation`, `catalog_version_not_found`).
- What the 409 conflict log line contains, as long as it never includes body or parameter values or the key.
- Which project hosts the JCS code and the validator wrapper (Contracts, Application or Catalog), within the FND-01 inward graph.
- The 200 replay's headers (whether to repeat `Location`) and body, consistent with the submit-response schema.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Ratified decisions
- `specs/DECISION_olive-poc2-phase1_v0.5.md`:
  - §6: endpoints, catalog pinning, submission idempotency (200 replay / 409).
  - §12: contract publication (JSON Schema 2020-12 under `contracts/proposal/v1/`).
  - §15.2: order of operations and concurrency.
  - §15.3: canonical body, `body_sha256`, null versus absent.
  - §17.2: four-key `proposal_hash`, no mutation before hashing on either side.
  - §17.3: check order (check 5, which Phase 3 uses).
- `specs/OLIVE_CHARTER_v0.4.md`: REQ-ACT-06 (source-controlled catalog, no runtime registration) and REQ-ACT-04 (no outbound network).
- `AGENTS.md`: scaffold constraints; never edit `contracts/vendored/vocabulary.yaml`.

### Standards
- RFC 8785 (JSON Canonicalization Scheme) and its test vectors; the cyberphone `json-canonicalization` test data.
- RFC 7493 (I-JSON): duplicate keys, number range and precision, surrogates.
- JSON Schema 2020-12 (core and validation), format-assertion vocabulary.

### Project planning
- `.planning/REQUIREMENTS.md`: API-03…API-10 texts, working defaults Q1, Q2 and Q20.
- `.planning/ROADMAP.md`, Phase 2: success criteria and spike notes (`JsonDocumentOptions.AllowDuplicateProperties`, lone surrogates, the privileges `ON CONFLICT DO NOTHING` needs).
- `.planning/phases/01-foundation-and-walking-skeleton/01-CONTEXT.md`: D-04, D-05, D-07, D-09, D-10.
- `.planning/phases/01-foundation-and-walking-skeleton/01-REVIEW.md`: WR-10 (number budget) and WR-11 (class-22 backstop), which shape the existing gate.

### External (D§4 stop rule: confirm present at run start)
- `~/liam/specs/LIAM_DESIGN_SPEC_v0.4.md` §7.2: Liam forwards the four proposal fields unchanged.
- hometown `HOMETOWN_v0.4` §2.5 (proposed): the hometown side of `proposalHash`, which consumes the `contracts/jcs/v1/` vectors.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`src/Olive.Host/Endpoints/ProposalEndpoints.cs`** (351 lines) holds the Phase 1 raw-body reading, the 1 MiB cap and MaxDepth 64, `TryGetCleanString` / `IsWellFormed` / `TryGetPropertyName` (surrogates and NUL), `IsNumberInRange` with the WR-10 growth-only budget, and the narrowed class-22 backstop (WR-11). It becomes the I-JSON gate (D-04). Duplicate-key rejection is not there yet: `JsonDocument.Parse` at line 53 uses default options.
- **`src/Olive.Catalog/AgentAllowlist.cs`**: a strict YamlDotNet deserialization pattern that fails closed, embedded as a resource (`Olive.Catalog.csproj` line 10). Model the catalog loader on it.
- **`catalog/crm.case_note@1.yaml`**: the single existing entry. Its parameters schema already has `additionalProperties: false`, so it should pass D-02 as is.
- **`contracts/introspection/`**: an example of schema-checked fixtures with a README and an index. Follow its style for `contracts/jcs/v1/`.
- **`src/Olive.Contracts/ProblemReasons.cs`**: the stable reason codes. `DuplicateRun` is to be replaced by `idempotency_conflict`.

### Established Patterns
- Tests are named by requirement ID (`API_06_…`, `API_09_…`). Integration tests use Testcontainers via `PostgresFixture`, and HTTP tests use `OliveApiFactory`.
- Migrations are forward-only and checksum-pinned. A new grant change means a new file, and the expectations in `MigrationRunnerTests` and `PrivilegeMatrixTests` must be updated with it.
- Central Package Management with lockfiles and `dotnet restore --locked-mode` in CI. The FND-02 allowlist test must list every new direct package.
- Reading the body as raw bytes and never model-binding it is already the Phase 1 pattern (API-04 formalizes it).

### Integration Points
- `ProposalEndpoints.PostAsync`: insert idempotency lookup → replay/409 between the principal checks and schema validation.
- `src/Olive.Persistence/ProposalSql.cs` and `PostgresProposalStore.cs`: the insert gains `body_sha256`, the JCS bytes and `proposal_hash`. The lookup by `(tenant_id, agent_id, run_id)` returns the stored hash and the current state.
- `Program.cs` / startup guards: the catalog load and vetting fail closed at startup, alongside the FND-03 vocabulary check.
- A new `GET /v1/catalog/{version}` route next to the proposal routes, with `RequireAuthorization()` and no principal-type check.

</code_context>

<specifics>
## Specific Ideas

- Shared golden vectors must be consumable by any language, which is why each case is a separate pair of files with exact bytes rather than JSON-escaped strings.
- "Two distinct values can never share a hash" is the guiding property behind the number rules.

</specifics>

<deferred>
## Deferred Ideas

None. The discussion stayed within phase scope. (OpenAPI and COMPATIBILITY.md were already scheduled for Phase 9 and were not re-opened.)

</deferred>

---

*Phase: 02-contract-integrity-and-idempotent-submission*
*Context gathered: 2026-10-22*
