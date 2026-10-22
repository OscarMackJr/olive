---
phase: 02-contract-integrity-and-idempotent-submission
fixed_at: 2026-10-22T00:00:00Z
review_path: .planning/phases/02-contract-integrity-and-idempotent-submission/02-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 02: Code Review Fix Report

**Fixed at:** 2026-10-22
**Source review:** .planning/phases/02-contract-integrity-and-idempotent-submission/02-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 8 (2 critical, 6 warning; Info findings IN-01..IN-05 are out of scope)
- Fixed: 8
- Skipped: 0
- Verification: `dotnet build` passed with 0 errors (44 warnings, all pre-existing analyzer warnings). The full `dotnet test` run, with Docker and Testcontainers, passed 589 of 589 tests with 0 failed and 0 skipped.

## Fixed Issues

### CR-01: Catalog vetting does not enforce closure

**Files modified:** `src/Olive.Catalog/CatalogVetting.cs`, `tests/Olive.Tests/CatalogLoaderTests.cs`
**Commit:** 8d1fad0
**Applied fix:** Vetting now uses an allowlist. Boolean `true` is refused anywhere in the `parameters` subtree, and `false` is still allowed. Every object node must constrain what it admits through `type`, `const`, `enum`, `$ref`, or a non-empty `allOf`/`anyOf`/`oneOf`. Each branch of a combinator is vetted under the same rules. Any schema whose `type` includes `array` must have `items`. `prefixItems` alone is refused because it leaves trailing items open. The existing `additionalProperties: false` rule for object schemas is unchanged. The shipped `catalog/crm.case_note@1.yaml` still loads (`API_10_real_catalog_loads_with_typed_scalars`).
**Tests:**
- `API_10_d02_vetting_refuses_untyped_true_and_itemless_array_subschemas` covers 10 shapes: `true`, `{}`, an item-less array, prefixItems-only, `items: true`, `type: [string, array]`, `not`-only, `required`-only, an `anyOf` with a `{}` branch, and `not: true`.
- `API_10_d02_vetting_accepts_typed_arrays_enums_and_typed_combinators` is the positive case.

### CR-02: Dangling local `$ref` passes vetting and the API-10 guard

**Files modified:** `src/Olive.Catalog/CatalogVetting.cs`, `tests/Olive.Tests/CatalogLoaderTests.cs`
**Commit:** 45100c2
**Applied fix:**
- The vetting walk records the RFC 6901 pointer of every schema node it vets, escaping `~0`/`~1`.
- Every `$ref` must be `#` or `#/...`. After percent-decoding, it must equal one of those vetted pointers. A dangling target, or a target that is not a schema (such as `#/properties` or `#/required`), is refused at load as `InvalidOperationException`, which surfaces as the API-10 refusal.
- `$id`, `$anchor`, `$dynamicAnchor` and `$dynamicRef` are refused outright, because they move resolution outside what a static check can verify.
**Tests:**
- `API_10_d02_vetting_refuses_dangling_or_non_schema_references` (8 cases).
- `API_10_d02_vetting_accepts_references_to_escaped_and_root_pointers` covers an escaped `$defs` key, `#`, and `#/properties/a`.
- The existing `#/$defs/n` acceptance test still passes.

### WR-01: JsonSchema.Net exceptions escape the fail-closed wrapper and the API-10 guard

**Files modified:** `src/Olive.Catalog/CatalogLoader.cs`, `src/Olive.Host/Startup/StartupGuards.cs`, `tests/Olive.Tests/CatalogLoaderTests.cs`, `tests/Olive.Tests/CatalogEndpointTests.cs`
**Commit:** e032645
**Applied fix:** `CatalogLoader.Parse` now also catches `Json.Schema.JsonSchemaException`, which includes `RefResolutionException`. `StartupGuards.CheckCatalog` catches every `Exception` and rethrows the static `StartupGuardException("API-10", ...)`.
**Tests:**
- `API_10_schema_compile_fault_is_static_invalid_operation` uses an unknown keyword `x-hidden: secretvalue`. It checks that the message is exactly "Catalog is invalid." and does not echo the keyword or the value.
- `API_10_non_invalid_operation_catalog_fault_refuses_with_static_message` is a host test. A `JsonSchemaException` carrying a secret leads to an API-10 refusal that does not echo the secret.
- Both tests were confirmed to fail before the fix.

### WR-05: Envelope contract schema loads lazily, outside the startup guard

**Files modified:** `src/Olive.Catalog/SubmissionValidator.cs`, `src/Olive.Host/Startup/StartupGuards.cs`, `tests/Olive.Tests/SubmissionValidatorTests.cs`, `tests/Olive.Tests/CatalogEndpointTests.cs`
**Commit:** 232bad0
**Applied fix:**
- Added `SubmissionValidator.EnsureContractLoaded()`.
- Added `SubmissionValidator.LoadContract(assembly, resource)`. It maps a missing resource, a JSON fault or a schema compile fault to the static "Contract schema is missing or invalid."
- `CheckCatalog` calls `EnsureContractLoaded()` and resolves the `SubmissionValidator` singleton inside the same fail-closed try/catch. A broken contract therefore stops the host with API-10 before it serves traffic.
**Tests:**
- `API_10_envelope_contract_loads_eagerly`.
- `API_10_missing_envelope_contract_is_static_invalid_operation`.
- `API_10_faulty_submission_validator_stops_the_host_at_startup` (host).

### WR-02: Idempotent replay ignores the principal context

**Files modified:** `src/Olive.Application/IProposalStore.cs`, `src/Olive.Persistence/ProposalSql.cs`, `src/Olive.Persistence/PostgresProposalStore.cs`, `src/Olive.Host/Endpoints/ProposalEndpoints.cs`, `tests/Olive.Tests/IdempotencyTests.cs`, `tests/Olive.Tests/ProposalStoreHashTests.cs`
**Commit:** 556f9c4
**Status:** fixed: requires human verification (logic change to the replay decision)

**Reasoning:** I checked where `on_behalf_of` and `purpose` come from. In `ProposalEndpoints.PostAsync`, both come from the introspected session key (`key.OnBehalfOf`, `key.Purpose`) and are passed straight into `NewProposal`. They are not request-body fields, so `body_sha256` (the JCS of the body) does not cover them. Replay was therefore unsafe as the reviewer described.

I applied the fail-closed option:
- `FindByRun` now selects `on_behalf_of` and `purpose`, and `ProposalRunRecord` carries them as required fields.
- A replay returns 200 only when the body hash matches and both delegation fields match with an ordinal comparison. Otherwise the response is `409 idempotency_conflict`.
- The conflict log line adds only a boolean `delegation_match` next to the existing ids and truncated hashes. It never includes the delegated user or the purpose.
- The `body_sha256` definition and the JCS vectors are unchanged.

`key_id` is deliberately not compared. A retry under a rotated or renewed session key with the same delegation is a legitimate replay, and the stored row still records the original key.

**Tests:**
- `API_06_same_body_and_run_under_different_delegation_gives_409_not_replay` runs against a custom stub introspector with four keys for the same agent `liam`. A different `on_behalf_of` gets 409, and a different `purpose` gets 409. A rotated `key_id` with the same delegation replays 200 with the original id. One row is stored, and the other user's id never appears in the logs.
- `API_06_find_by_run_returns_id_state_and_hash_and_is_tenant_scoped` now also asserts `OnBehalfOf` and `Purpose`.

### WR-03: Schema-violation pointer echoes caller-supplied property names, ignores its cap, and can split surrogate pairs

**Files modified:** `src/Olive.Catalog/SchemaValidator.cs`, `src/Olive.Catalog/SubmissionValidator.cs`, `tests/Olive.Tests/SchemaValidatorTests.cs`, `tests/Olive.Tests/SubmissionValidatorTests.cs`, `tests/Olive.Tests/ContractSchemaTests.cs`, `tests/Olive.Tests/ApiIntegrityTests.cs`
**Commit:** 7aa9f2b
**Applied fix:**
- `additionalProperties` and `unevaluatedProperties` failures now report the parent object's location, for example `""` or `/parameters`, instead of the unknown key.
- A new public `SchemaValidator.CapPointer` applies the 256-character cap. `SubmissionValidator` applies it after prefixing `/parameters`. The cap backs off rather than leave a lone high surrogate or a dangling `~` from a `~0`/`~1` escape.
- Existing tests that asserted `/zzz`, `/z`, `/extra` or `/parameters/extra` were updated to the parent location.
- The truncation test now uses a long *declared* property name, which is catalog text and not caller text.

**Tests:**
- `API_05_unknown_key_pointer_names_the_parent_not_the_key` covers both `additionalProperties` and `unevaluatedProperties`.
- `API_05_pointer_cap_never_splits_a_surrogate_pair_or_escape` (3 cases).
- `API_05_prefixed_parameters_pointer_is_capped_after_prefixing`.

**Note:** The problem-detail `pointer` value for unknown-key violations changes from `/parameters/extra` to `/parameters`. The response shape is unchanged.

### WR-06: YAML-to-JSON conversion ignores explicit tags and silently loses precision on large integers

**Files modified:** `src/Olive.Catalog/YamlToJson.cs`, `tests/Olive.Tests/CatalogLoaderTests.cs`
**Commit:** a1efdcc
**Applied fix:**
- `!!str` on a scalar is honoured and always yields a string. Any other explicit tag, on a scalar or on a mapping or sequence node, is refused.
- An integer that matches the integer pattern but falls outside the `long` range is refused instead of falling through to `double`.
- A float-pattern scalar must be finite and pass `JsonNumbers.IsLossless`, or it is refused. Previously a non-finite value such as `1e999` silently became a string.

**Tests:**
- `API_10_str_tag_keeps_scalar_a_string`.
- `API_10_explicit_tags_and_lossy_numbers_fail` (6 cases: `!!int`, `!custom`, `!!map`, an out-of-range integer, `9007199254740993.0`, and `1e999`).
- `API_10_in_range_integers_and_exact_decimals_still_load`.

**Note:** The reviewer's example `9007199254740993` is inside the `long` range and was already exact. It now has a positive test.

### WR-04: Migration 0004 does not bind `body_sha256` to `canonical_body`

**Files modified:** `sql/migrations/0005_proposal_body_hash_binding.sql` (new), `tests/Olive.Tests/MigrationRunnerTests.cs`, `tests/Olive.Tests/PrivilegeMatrixTests.cs`
**Commit:** 491050d
**Applied fix:**
- New migration 0005 adds `proposal_body_sha256_matches_canonical_body`: `check (canonical_body is null or body_sha256 = encode(sha256(convert_to(canonical_body, 'UTF8')), 'hex'))`.
- The constraint can be expressed safely. `sha256(bytea)` has been built in since PG 11, and the target is PG 17 (`postgres:17-alpine`, `deploy-postgres-1` 17.10). The UTF-8 bytes of the stored canonical text are exactly what `Jcs.Sha256Hex` hashes, because the gate refuses lone surrogates and NUL. Legacy NULL-hash rows pass.
- Migrations 0001–0004 are untouched.

**Tests:**
- `DB_02_applies_0001_as_owner_and_records_checksum` now expects `0005` and checks its checksum and filename.
- `DB_04_hash_columns_enforce_format_and_all_or_none` gained two `23514` cases: a well-formed but wrong hash, and `'{ }'` stored with the hash of `'{}'`.
- `DB_04_app_can_insert_all_three_hash_columns_and_cannot_update_them` now inserts the real SHA-256 of `{}`.
- The API and store paths still pass end to end.

**Note:** File-new migration created by the fix, as the finding required.

---

_Fixed: 2026-10-22_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
