---
phase: 02-contract-integrity-and-idempotent-submission
reviewed: 2026-10-22T00:00:00Z
depth: standard
files_reviewed: 35
files_reviewed_list:
  - src/Olive.Domain/Jcs/Jcs.cs
  - src/Olive.Domain/Jcs/JsonNumbers.cs
  - src/Olive.Host/Endpoints/JsonGate.cs
  - src/Olive.Host/Endpoints/ProposalEndpoints.cs
  - src/Olive.Host/Endpoints/CatalogEndpoints.cs
  - src/Olive.Host/Problems/ProblemResults.cs
  - src/Olive.Host/Program.cs
  - src/Olive.Host/Startup/StartupGuards.cs
  - src/Olive.Host/Startup/StartupGuardFilter.cs
  - src/Olive.Catalog/SchemaValidator.cs
  - src/Olive.Catalog/SchemaViolation.cs
  - src/Olive.Catalog/SubmissionValidator.cs
  - src/Olive.Catalog/CatalogLoader.cs
  - src/Olive.Catalog/CatalogModels.cs
  - src/Olive.Catalog/CatalogVetting.cs
  - src/Olive.Catalog/VocabularyPurposes.cs
  - src/Olive.Catalog/YamlToJson.cs
  - src/Olive.Catalog/Olive.Catalog.csproj
  - src/Olive.Contracts/ProblemReasons.cs
  - src/Olive.Application/IProposalStore.cs
  - src/Olive.Persistence/PostgresProposalStore.cs
  - src/Olive.Persistence/ProposalSql.cs
  - sql/migrations/0004_proposal_body_hash.sql
  - catalog/catalog.yaml
  - contracts/proposal/v1/proposal-request.schema.json
  - contracts/proposal/v1/proposal-submit-response.schema.json
  - contracts/proposal/v1/proposal.schema.json
  - contracts/catalog/v1/action-type.schema.json
  - Directory.Packages.props
  - .gitattributes
  - tests/Olive.Tests/IdempotencyTests.cs
  - tests/Olive.Tests/JsonGateTests.cs
  - tests/Olive.Tests/SubmissionValidatorTests.cs
  - tests/Olive.Tests/CatalogLoaderTests.cs
  - tests/Olive.Tests/ApiProposalTests.cs
findings:
  critical: 2
  warning: 6
  info: 5
  total: 13
status: issues_found
---

# Phase 02: Code Review Report

**Reviewed:** 2026-10-22
**Depth:** standard
**Files Reviewed:** 35
**Status:** issues_found

## Narrative Findings (AI reviewer)

## Summary

I checked the JCS implementation, the I-JSON gate, idempotent submission, the startup guards, migration 0004 and catalog vetting. I ran a scratch probe (outside the repo) against `CatalogLoader.Parse`, `SubmissionValidator`, `JsonNumbers` and `Jcs` to confirm the behaviour claims below.

What holds up:
- **JCS:** number formatting follows the ES6 `Number::toString` cases. Verified outputs: `1e21` gives `1e+21`, `1e-7` gives `1e-7`, `5e-324` gives `5e-324`, and `-0` gives `0`. String escaping matches the RFC 8785 rules, and keys are sorted by UTF-16 ordinal.
- **I-JSON gate:** it rejects duplicate keys, lone surrogates, NUL, non-finite numbers and lossy numbers before any hashing.
- **Idempotency:** the lookup is scoped by tenant, agent and run, and the losing side of an insert race re-reads the winning row.
- **Error responses:** problem bodies and logs do not echo values.

The main defects are in catalog vetting. It does not close the `parameters` subtree, which is the D-02 invariant. It also lets a schema with a dangling `$ref` through the startup guard, so every submission for that action fails with a 500 at request time.

## Critical Issues

### CR-01: Catalog vetting does not enforce closure: `true`, `{}`, untyped and item-less array subschemas admit arbitrary nested objects

**File:** `src/Olive.Catalog/CatalogVetting.cs:33-36, 66-70, 113-128`
**Issue:** `Vet` returns early for boolean `true` (line 33). It only requires `additionalProperties: false` when `IsObjectSchema` finds `type: object`, `properties` or `patternProperties` (lines 113-128). Any subschema that does not name `object` therefore accepts open objects at any depth. Examples: `true`, `{}`, `{ type: array }` without `items`, `{ not: { type: string } }`, `{ required: [x] }`, or an `anyOf` branch that is `{}`. I confirmed all three of these with the real loader:

- `properties: { a: true }` loads, and `{"a":{"anything":{"deep":1}}}` validates as **valid**.
- `properties: { a: {} }` loads, and `{"a":{"anything":1}}` is **valid**.
- `properties: { a: { type: array } }` loads, and `{"a":[{"x":1}]}` is **valid**.

`CatalogLoaderTests` (lines 156-168) only covers schemas that say `type: object`, so nothing catches this. Because D-02 closure is the only control on catalog authoring mistakes, a catalog entry can quietly let agents put unbounded, unreviewed data into stored `parameters`.
**Fix:** Switch from "check when it looks like an object" to an allowlist. Every schema node must constrain its type, and boolean `true` is refused:
```csharp
if (schema.ValueKind == JsonValueKind.True) throw new InvalidOperationException(Invalid);
if (schema.ValueKind == JsonValueKind.False) return;
// ...
bool typed = schema.TryGetProperty("type", out _) || schema.TryGetProperty("const", out _)
          || schema.TryGetProperty("enum", out _) || schema.TryGetProperty("$ref", out _);
if (!typed) throw new InvalidOperationException(Invalid);
if (IsArraySchema(schema) && !schema.TryGetProperty("items", out _) && !schema.TryGetProperty("prefixItems", out _))
    throw new InvalidOperationException(Invalid);
```
Also add a negative test for each shape above.

### CR-02: Dangling local `$ref` passes vetting and the API-10 startup guard, then every submission for that action returns 500

**File:** `src/Olive.Catalog/CatalogVetting.cs:49-55`, `src/Olive.Catalog/CatalogLoader.cs:116-117`, `src/Olive.Host/Startup/StartupGuards.cs:28-38`
**Issue:** Vetting only checks that `$ref` starts with `#`. `SchemaValidator.Compile` does not resolve references, so a parameter schema such as `a: { $ref: '#/$defs/missing' }` loads without error. I confirmed this: the catalog loads, and `Validate` then throws `Json.Schema.RefResolutionException` on each request. `ProblemExceptionHandler` turns that into a 500. This breaks API-10, whose comment at `StartupGuards.cs:27` says "a fault stops the host before it serves traffic". The host starts and serves an action that can never be submitted.
**Fix:** Resolve every local reference at load time. Either evaluate the compiled schema once against a probe instance and catch `RefResolutionException`, or (more robustly) walk every `$ref` / `$dynamicRef` and check the target with `JsonPointer.Parse(r[1..]).TryEvaluate(parametersNode, out var target)`, refusing when it is missing. Map any failure to `InvalidOperationException(Invalid)`.

## Warnings

### WR-01: JsonSchema.Net exceptions escape the catalog fail-closed wrapper and the API-10 guard

**File:** `src/Olive.Catalog/CatalogLoader.cs:57`, `src/Olive.Host/Startup/StartupGuards.cs:34`
**Issue:** The exception filter in `CatalogLoader.Parse` catches `YamlException`, `JsonException`, `InvalidOperationException`, `ArgumentException` and `FormatException`. It does not catch `Json.Schema.JsonSchemaException` or its subclass `RefResolutionException`. `CheckCatalog` catches only `InvalidOperationException`. I confirmed that an unknown keyword in a parameter schema throws `Json.Schema.JsonSchemaException: Unknown keywords (x-hidden) are disallowed for this dialect.` straight out of `Parse`. Startup still aborts, but without the static API-10 `StartupGuardException`. Instead an exception message that quotes catalog content escapes, which contradicts the "static message that names no values" contract at `CatalogLoader.cs:8-9`.
**Fix:** Add `or Json.Schema.JsonSchemaException` to the filter at line 57. In `CheckCatalog`, catch `Exception` (not just `InvalidOperationException`) and rethrow the static `StartupGuardException("API-10", ...)`.

### WR-02: Idempotent replay ignores the principal context (on_behalf_of, key_id, purpose)

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:65-77`, `src/Olive.Persistence/ProposalSql.cs:19-20`
**Issue:** Replay is keyed on (tenant, agent, run_id) plus the body hash. The hash covers only the request body, and `FindByRun` returns no delegation context. Suppose the same agent submits the same body and `run_id` under a different session key, acting on behalf of a different user or for a different purpose. That caller gets `200` and the proposal id of a proposal created on behalf of the *first* user. The second caller believes their proposal was recorded, while the stored record attributes it to someone else. This weakens the Core Value chain from proposal to delegated principal.
**Fix:** Return `on_behalf_of` and `purpose` from `FindByRun`. Treat a mismatch with `key.OnBehalfOf` / `key.Purpose` as `409 idempotency_conflict`, logged with ids only. Alternatively, fold those fields into the idempotency hash input.

### WR-03: Schema-violation pointer echoes caller-supplied property names, ignores its cap, and can split surrogate pairs

**File:** `src/Olive.Catalog/SchemaValidator.cs:50-54`, `src/Olive.Catalog/SubmissionValidator.cs:45`
**Issue:** For `additionalProperties` failures, `pointer` is the instance location of the *unknown key* (tests assert `/zzz` and `/parameters/extra`). An agent-supplied key name, which can hold PII or a secret pasted as a key, is returned in the problem body. That conflicts with the value-free intent of D-03 and T-02-06. On top of that:
1. The 256-character cap is applied *before* `/parameters` is prepended, so the response pointer can reach 267 characters (confirmed).
2. Truncation cuts at a UTF-16 index. I confirmed that a key ending in an emoji at the boundary leaves a lone high surrogate (`0xD83D`), which System.Text.Json writes as `�`, giving a pointer that is not valid JSON Pointer.
**Fix:** For `additionalProperties` / `unevaluatedProperties` errors, report the parent object's location (e.g. `/parameters`) with the keyword, not the offending key. Apply the length cap after prefixing, and never cut between a high and a low surrogate (back off one char when `char.IsHighSurrogate(pointer[cap-1])`), or cut at the last `/` segment boundary.

### WR-04: Migration 0004 does not bind `body_sha256` to `canonical_body`

**File:** `sql/migrations/0004_proposal_body_hash.sql:4-9`
**Issue:** The migration says its purpose is "so the hash is recomputable at audit time", but the database accepts any 64-hex value next to any `canonical_body`. A bug or an alternate writer could store a hash that does not match the stored canonical text, and nothing would catch it until an audit. The idempotency decision relies on `body_sha256` alone.
**Fix:** Add a check constraint (PostgreSQL 11+ has `sha256(bytea)`). Put it in 0004 if 0004 is not yet checksum-pinned, otherwise in a new `0005`:
```sql
alter table proposal add constraint proposal_body_sha256_matches check (
  canonical_body is null
  or body_sha256 = encode(sha256(convert_to(canonical_body, 'UTF8')), 'hex'));
```

### WR-05: Envelope contract schema loads lazily, outside the startup guard

**File:** `src/Olive.Catalog/SubmissionValidator.cs:20-26`, `src/Olive.Host/Startup/StartupGuards.cs:28-38`
**Issue:** The `Envelope` `Lazy<SchemaValidator>` is first built on the first POST. If the embedded `proposal-request.schema.json` is missing or fails to compile, the host starts anyway and every submission returns 500. `Lazy` caches the exception, so this does not recover. API-10 checks only the catalog, not the contract the catalog is applied through.
**Fix:** Expose a `SubmissionValidator.EnsureLoaded()` (or resolve `Envelope.Value` in the constructor), and call it from `CheckCatalog` inside the same fail-closed try/catch.

### WR-06: YAML-to-JSON conversion ignores explicit tags and silently loses precision on large integers

**File:** `src/Olive.Catalog/YamlToJson.cs:74-102`
**Issue:** Two problems with numbers and tags in catalog YAML:
- **Tags:** `Scalar` looks only at `Style`, so explicit tags are ignored. A plain scalar written `!!str 10` becomes the number `10`, the opposite of what the author asked for.
- **Large integers:** an integer outside `long` range fails `long.TryParse` and drops through to `FloatPattern` / `double.TryParse`. It becomes a rounded double; for example, `maximum: 9007199254740993` in a parameter schema becomes `9007199254740992`. This is the same lossy-number class the request gate refuses (D-05), now allowed silently in the catalog. It changes validation boundaries without any error.

**Fix:** Refuse any scalar with a non-empty `Tag` other than the core-schema tags, or honour `!!str`. For integers that fail `long.TryParse`, throw `InvalidOperationException` instead of falling through to double. Also run `JsonNumbers.IsLossless(v)` on the float path.

## Info

### IN-01: Endpoint-level insert-race branch is not deterministically tested

**File:** `src/Olive.Host/Endpoints/ProposalEndpoints.cs:128-132`, `tests/Olive.Tests/IdempotencyTests.cs:196-226`
**Issue:** The 8-way `Task.WhenAll` race over the in-process test server can finish with every loser taking the pre-insert `FindByRunAsync` path, so the `id is null` branch (and its fail-closed 409 when the winner row is missing) may never run. The tests still pass because both paths give the same result. `TenantUnitOfWorkTests:77-78` covers only the store returning null.
**Fix:** Add a test with a fake `IProposalStore` whose `FindByRunAsync` returns null first and whose `InsertAsync` returns null. Assert a 200 replay when the second lookup finds a matching row, and a 409 when it returns null.

### IN-02: NewProposal hash fields are optional, so callers can create rows that can never be replayed

**File:** `src/Olive.Application/IProposalStore.cs:18`
**Issue:** `CanonicalBody`, `BodySha256` and `ProposalHash` default to `null`. Any new caller that leaves them out writes a row that will always return 409 on replay (`ProposalEndpoints.cs:73`).
**Fix:** Make them required (non-nullable, no default) on `NewProposal`. Legacy rows already exist with NULLs and need no new writes.

### IN-03: .NET `$` accepts a trailing newline in id patterns

**File:** `src/Olive.Catalog/CatalogLoader.cs:25`, `contracts/proposal/v1/proposal-request.schema.json:10`
**Issue:** In .NET regex, `^...$` matches `"crm.case_note@1\n"`. The envelope pattern therefore accepts it, and the request gets `not_in_catalog` rather than `schema_violation`. The C# `ActionIdPattern` has the same weakness.
**Fix:** Use `\z` in the C# `GeneratedRegex`. For the schema pattern, reject the newline via the character class, e.g. `"^[a-z0-9_.]+@[0-9]+$"` combined with `"maxLength"` plus a `"not": {"pattern": "\\n"}`, or accept the current mapping and document it.

### IN-04: Catalog version route accepts non-canonical leading zeros

**File:** `src/Olive.Host/Endpoints/CatalogEndpoints.cs:20-22`
**Issue:** `/v1/catalog/0001` returns release 1, so one resource has many URLs. D-12 treats unparseable versions as 404.
**Fix:** Also require `version == number.ToString(CultureInfo.InvariantCulture)`.

### IN-05: Nullable flow warning on `result.Details`

**File:** `src/Olive.Catalog/SchemaValidator.cs:44`
**Issue:** The build emits CS8604 (`result.Details` may be null). If it is ever null, `FirstOrDefault` throws `ArgumentNullException`, which becomes a 500.
**Fix:** `var first = result.Details?.FirstOrDefault(d => d.Errors is { Count: > 0 });`

---

_Reviewed: 2026-10-22_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
