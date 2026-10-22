---
phase: 02
slug: contract-integrity-and-idempotent-submission
status: verified
threats_open: 0
asvs_level: 1
created: 2026-10-22
---

# Phase 02 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

The register comes from the `<threat_model>` blocks of plans 02-01 to 02-07: 36 threats, of which 33 are mitigate and 3 are accept. None of the seven SUMMARY files has a `## Threat Flags` section. On 2026-10-22, gsd-security-auditor checked every `mitigate` row against the code on main at b4d2e79, with file:line and test-name evidence. That code includes the code-review fixes CR-01, CR-02 and WR-01..WR-06 from `02-REVIEW-FIX.md`. `dotnet test` passed 589/589 on main, including the Testcontainers integration tests.

---

## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| agent (Liam) -> `POST /v1/proposals` | An untrusted raw body crosses here. It is validated exactly as received and never mutated or echoed |
| raw bytes -> JCS hash | The canonical form must be unambiguous. Lossy numbers, duplicate keys and lone surrogates are refused first |
| Olive -> Postgres (`proposal`) | Stored bytes and hashes must stay bound together, and tenant predicate plus forced RLS still apply |
| `catalog/*.yaml` (source control) -> runtime catalog | A change-managed trust list. The catalog is vetted closed at startup and cannot be registered at runtime |
| JSON Schema evaluation -> network | `$ref` must never fetch remotely (no Fetch delegate, HttpClient banned) |
| NuGet registry -> build | JsonSchema.Net 8.0.5 and its transitive packages enter the supply chain |
| developer machine / CI -> `contracts/jcs/v1` | Line-ending conversion would change the shared golden vectors that hometown verifies against |
| introspected session key -> idempotency key | `tenant_id`, `agent_id`, `on_behalf_of` and `purpose` come from introspection, never from the body |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-02-01 | Tampering | `Jcs.Canonicalize` number layout | mitigate | ES6 layout in `Jcs.cs:61-97`, BCL SHA-256. Covered by `API_09_appendix_b_number_serialization` and the cyberphone vectors in `JcsVectorTests` | closed |
| T-02-02 | Tampering | Number-precision collision | mitigate | `JsonNumbers.IsLossless`, used by `JsonGate.cs:160`. Negative vectors committed. Covered by `API_09_lossy_number_gate_verdicts_match_json_numbers_rule` | closed |
| T-02-03 | Tampering | `proposal_hash` with an omitted key | mitigate | `Jcs.cs:30-35` throws. Covered by `API_09_proposal_hash_missing_key_throws`, which checks all 4 keys | closed |
| T-02-04 | Repudiation | Vector byte drift via CRLF | mitigate | `.gitattributes` has `contracts/jcs/v1/* -text`, and `git check-attr` reports `text: unset` | closed |
| T-02-05 | Information disclosure | Hash helpers logging input | accept | See accepted risks AR-02-01 | closed |
| T-02-06 | Information disclosure | `SchemaValidator` error output | mitigate | `SchemaViolation(Pointer, Keyword)` only, pointer capped at 256 chars. Covered by `API_05_violation_never_exposes_the_instance_value` | closed |
| T-02-07 | Tampering | Coercion or defaults via the validator | mitigate | Validates the parsed `JsonElement` in place (`SchemaValidator.cs:38`). No `default` anywhere, and vetting refuses one. Covered by `API_05_string_for_integer_fails_type_without_coercion` | closed |
| T-02-08 | Information disclosure / EoP | Remote `$ref` SSRF (validator) | mitigate | `new SchemaRegistry()` with no Fetch. `BannedSymbols.txt` makes RS0030 an error. Covered by `API_05_remote_ref_never_resolves_over_the_network` | closed |
| T-02-09 | Tampering | Dialect drift | mitigate | `$schema` forced to 2020-12 in an in-memory copy. Covered by `API_05_dialect_is_forced_to_2020_12_prefixItems_honoured` | closed |
| T-02-SC | Tampering | NuGet installs (JsonSchema.Net 8.0.5, JsonPointer.Net 6.0.1, Json.More.Net 2.2.0) | mitigate | Human checkpoint approved 2026-10-22. Packages pinned with CPM and transitive pinning. CI runs lockfiles in `--locked-mode`, and the vulnerable-package gate fails closed | closed |
| T-02-10 | Elevation of privilege | `olive_app` UPDATE on hash columns | mitigate | `0004:11` grants INSERT only on the 3 columns. UPDATE stays `(state)`. Covered by `DB_04_app_can_insert_all_three_hash_columns_and_cannot_update_them` | closed |
| T-02-11 | Information disclosure | `FindByRun` cross-tenant read | mitigate | `ProposalSql.cs:20` tenant predicate plus forced RLS. Covered by `API_06_find_by_run_returns_id_state_and_hash_and_is_tenant_scoped` | closed |
| T-02-12 | Repudiation | Stored hash drifting from stored bytes | mitigate | Format and all-or-none checks (0004), and `body_sha256` bound to the canonical body (0005). Covered by `DB_04_hash_columns_enforce_format_and_all_or_none` | closed |
| T-02-13 | Tampering | Legacy NULL-hash row matched as equal | mitigate | `BodySha256` is nullable, and `ProposalEndpoints.cs:77` requires it non-null before replay. Covered by `API_06_legacy_row_without_hashes_surfaces_null_sha` | closed |
| T-02-14 | Tampering | Duplicate-key smuggling | mitigate | `AllowDuplicateProperties = false` (`JsonGate.cs:31`). Tests cover top-level, nested, escaped-equal and array-nested duplicates | closed |
| T-02-15 | Tampering | Lossy number collision (gate) | mitigate | The whole tree is walked and `IsLossless` applied to every number (`JsonGate.cs:133-164`) | closed |
| T-02-16 | Denial of service | Parse exceptions becoming 500s | mitigate | Exceptions are caught and return 400 only. `API_04_gate_failures_give_400_store_nothing_and_do_not_500` asserts no error-level log | closed |
| T-02-17 | Denial of service | Huge numbers, depth, body size | mitigate | 1 MiB Kestrel cap plus bounded read (413), MaxDepth 64, 64-char tokens, exponent range -324..308, and one expansion budget for the whole body | closed |
| T-02-18 | Information disclosure | Echoing the offending value | mitigate | Static problem titles. The gate never logs, and the DB fault log carries SqlState only. Tests assert the secret is absent | closed |
| T-02-19 | Tampering | Silent YAML mistakes | mitigate | Duplicate keys refused, core-schema scalars, strict bind. WR-06 adds: only `!!str` honoured, lossy or out-of-range numbers refused. Covered by `API_10_explicit_tags_and_lossy_numbers_fail` | closed |
| T-02-20 | Tampering | Open parameter schemas | mitigate | Allowlist vetting (CR-01): `true`, unconstrained nodes, arrays without `items`, open objects, `default` and unknown `format` are all refused. Covered by `API_10_d02_vetting_refuses_untyped_true_and_itemless_array_subschemas` | closed |
| T-02-21 | Information disclosure / EoP | Remote `$ref` SSRF (catalog) | mitigate | Only `#`/`#/` refs are allowed, resolved against vetted nodes at load (CR-02). `$id`, `$anchor` and `$dynamic*` are refused. Covered by `API_10_d02_vetting_refuses_dangling_or_non_schema_references` | closed |
| T-02-22 | Tampering | Runtime catalog registration | mitigate | `CatalogSet` has no mutators, the catalog is embedded only, and the only POST route is `/v1/proposals`. Covered by `API_10_catalog_has_no_runtime_registration_member` | closed |
| T-02-23 | Denial of service | Bad catalog serving traffic | mitigate | `StartupGuards.CheckCatalog` loads the catalog, the envelope contract (WR-05) and the validator eagerly. Any exception becomes the static `StartupGuardException("API-10")` (WR-01) | closed |
| T-02-24 | Information disclosure | Catalog exposure | accept | See accepted risks AR-02-02 | closed |
| T-02-25 | Tampering | Coercion, defaults or trimming before hashing | mitigate | Validated in place, and stored values come from the same parsed root. Covered by `API_04_padded_values_are_stored_untouched` | closed |
| T-02-26 | Tampering | Null optional field | mitigate | The v1 envelope has no null types. Covered by `API_04_null_optional_field_gives_400_schema_violation_and_stores_nothing` | closed |
| T-02-27 | Information disclosure | Value echoed in problem detail | mitigate | Pointer and keyword only. WR-03 points at the parent for unknown keys and caps safely. Covered by `API_05_unknown_key_pointer_names_the_parent_not_the_key` | closed |
| T-02-28 | Tampering | Parameter schema bypass via unknown action | mitigate | `not_in_catalog` 400 before insert. Covered by `API_05_unknown_action_type_gives_non_stored_400_not_in_catalog` | closed |
| T-02-29 | Spoofing / Tampering | Validate-after-store ordering | mitigate | Validation runs before `InsertAsync`. Covered by `API_06_schema_invalid_first_submission_is_not_stored_and_corrected_resubmit_is_201` | closed |
| T-02-30 | Tampering | Altered-body replay | mitigate | Whole-body JCS `body_sha256`, plus a match on `on_behalf_of` and `purpose` (WR-02). Covered by `API_06_same_body_and_run_under_different_delegation_gives_409_not_replay` | closed |
| T-02-31 | Information disclosure | Cross-tenant probing via 200/409 | mitigate | Lookup keyed by the introspected tenant, with predicate plus RLS. Covered by `API_06_idempotency_is_scoped_to_tenant` | closed |
| T-02-32 | Repudiation | Two proposals from one run under a race | mitigate | Unique constraint, `ON CONFLICT DO NOTHING`, re-lookup, fail closed with 409. Covered by 8-way `API_07_concurrent_*` tests | closed |
| T-02-33 | Information disclosure | Secrets in the 409 log | mitigate | Static template with ids, 12-char hash prefixes and a bool. Covered by `API_06_different_body_same_run_gives_409_not_stored_and_logged_value_free` | closed |
| T-02-34 | Tampering | Stored bytes drifting from the hash | mitigate | One `byte[]` produces both hash and stored text, and the 0005 DB binding backs this up. Covered by `API_06_stored_canonical_body_and_hashes_are_audit_recomputable` | closed |
| T-02-35 | Elevation of privilege | Replay bypassing later trace/policy | accept | See accepted risks AR-02-03 | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-02-01 | T-02-05 | The Domain JCS code has no logging: no ILogger, and a grep for "log" in `src/Olive.Domain/Jcs/` finds nothing. Any future logging there must stay value-free | plan 02-01 threat model; auditor confirmed | 2026-10-22 |
| AR-02-02 | T-02-24 | The catalog is not tenant data (D-11). `GET /v1/catalog/{version}` still calls `.RequireAuthorization()`, so an inactive or missing key gets 401 | plan 02-05 threat model; auditor confirmed | 2026-10-22 |
| AR-02-03 | T-02-35 | Phase 2 has no trace or policy step. A replay only returns an existing id for an identical body and delegation. `API_06_idempotency_precedes_schema_and_replay_does_not_insert` pins the order, and Phases 3-4 must keep that test | plan 02-07 threat model; auditor confirmed | 2026-10-22 |

*Accepted risks do not resurface in future audit runs.*

---

## Audit Notes (non-blocking)

- **T-02-08:** `API_05_remote_ref_never_resolves_over_the_network` uses `Assert.ThrowsAny<Exception>`. The no-network guarantee itself comes from the missing Fetch delegate plus the RS0030 `HttpClient` ban.
- **T-02-22:** only the reflection test exists. The plan's "grep" evidence was a manual source check during this audit.
- **T-02-13:** no API-level test seeds a legacy NULL-hash row and asserts 409. The guard (`ProposalEndpoints.cs:77`) and the store-level null test cover it.
- **T-02-SC:** `--locked-mode` is enforced in CI, not in `Directory.Build.props`. A local `dotnet restore` without the flag can still update the lockfiles.

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-22 | 36 | 36 | 0 | gsd-security-auditor (verify mode, plan-time register) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-22
