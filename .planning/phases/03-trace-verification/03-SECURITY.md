---
phase: 03
slug: trace-verification
status: verified
threats_open: 0
asvs_level: 1
created: 2026-10-22
---

# Phase 03 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

The register comes from the `<threat_model>` blocks of plans 03-01 to 03-06. It has 37 distinct threats: T-03-01 to T-03-35 are `mitigate`, and T-03-36 and T-03-SC are `accept`. T-03-SC is declared in plans 03-02, 03-03, 03-05 and 03-06. On 2026-10-22, gsd-security-auditor checked every `mitigate` row against the code on main at 371ed2c, which includes code-review fixes WR-01..04, WR-06 and WR-07 from `03-REVIEW-FIX.md`. Each row cites file:line and the name of a test. Rerunning the 15 threat-relevant test classes gave 221/221 passing: TraceVerifier, ServiceTokenProvider, CachingTraceResolver, Architecture, SecretType, TraceFixture, StubTokenIssuer, Containment, SentinelScanSelf, TraceFailClosed, TraceRefusalEndpoint, TraceVerifiedEndpoint, ProposalStoreRefusal, PrivilegeMatrix and StartupGuard. These include the Testcontainers integration tests.

Threat Flags: 03-01, 03-03, 03-04, 03-05 and 03-06 report "None". 03-02-SUMMARY.md has no `## Threat Flags` section.

---

## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| agent -> `POST /v1/proposals` | An untrusted body and `trace_ref` cross here |
| Olive -> popeye (introspector, token issuer) | The only boundary that may receive the session key |
| Olive -> hometown resolver | Receives only the service token grant. It returns untrusted facts |
| resolver facts -> verdict -> stored row | Untrusted facts decide a stored, auditable refusal |
| olive_app role -> `proposal` table | The app role may write only the columns that migrations grant |
| Olive -> agent (201/422/503 bodies, headers) | Responses must not reveal causes, facts or secrets |
| Olive -> logs, stored rows, alarm logs | Surfaces an observer can read, which must stay free of secrets |
| build/config -> production | Stubs, fixtures and fault doubles must not run in production |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation (evidence) | Status |
|-----------|----------|-----------|-------------|------------------------|--------|
| T-03-01 | Tampering | proposal.state / refusal_reason writes | mitigate | 0006 grants INSERT only on `(trace_verification, trace_schema_version)` (`0006_trace_verification.sql:12`) and UPDATE on `refusal_reason` (`:13`). The CHECK `proposal_refusal_state_agrees` is at `:10`. Insert-then-CAS is in `PostgresProposalStore.cs:83-90` with `ProposalSql.cs:25-26`, guarded by `state = 'proposed'`. WR-07 guard: `PostgresProposalStore.cs:140-142`. Tests: `TRC_03_app_cannot_insert_refusal_reason` (42501), `DB_04_app_cannot_insert_non_default_state`, `DB_04_privilege_matrix_equals_expected`, `TRC_09_refusal_state_and_reason_must_agree` (23514), `TRC_03_transition_into_or_out_of_refused_policy_is_rejected_before_the_database` | closed |
| T-03-02 | Tampering | trace_verification value | mitigate | CHECK `trace_verification in ('fixture','hometown')` at `0006:6`. App UPDATE is not granted. Tests: `TRC_09_trace_columns_enforce_values_and_format` (23514), `TRC_09_app_cannot_update_trace_verification` (42501). See audit note | closed |
| T-03-03 | Information disclosure | cross-tenant refusal read | mitigate | `ProposalSql.cs:17,20,23,26` all carry `tenant_id = @tenant_id`. `0001_init.sql:39` has `force row level security`. Test: `TRC_03_cross_tenant_get_of_refused_row_returns_null` | closed |
| T-03-04 | Information disclosure | problem titles and refusal_reason | mitigate | Static titles in `ProblemResults.cs:21-37`. The reason is constrained to `^[a-z][a-z_]{0,63}$` (`0006:9`), and values come only from `TraceRefusalReasons` constants (`Olive.Domain/TraceRefusalReasons.cs:5-8`, used at `TraceVerifier.cs:83-98`). Test: `TRC_09_refusal_state_and_reason_must_agree` (bad format gives 23514) | closed |
| T-03-05 | Repudiation | refusal events | mitigate | The `submitted` and `trace_refused` events are written in one `uow.ExecuteAsync` transaction (`PostgresProposalStore.cs:41-104`). Tests: `TRC_03_insert_refused_stores_state_reason_trace_fields_and_both_events`, and `AssertRefusal` in `TraceRefusalEndpointTests` (`submitted,trace_refused`) | closed |
| T-03-06 | Spoofing | TraceVerifier check 4 | mitigate | `TraceVerifier.cs:91` compares `f.AgentId` ordinally with `grant.DelegatedAgentId`, and null is treated as a mismatch. Tests: `TRC_05_agent_is_compared_to_the_token_delegated_agent`, `TRC_05_null_agent_is_agent_mismatch`, `TRC_05_agent_differing_from_token_delegated_agent_is_trace_agent_mismatch` | closed |
| T-03-07 | Tampering | TraceVerifier check 5 | mitigate | `TraceVerifier.cs:95` uses an ordinal compare and treats null as a mismatch. The verifier has no Jcs or hashing (`TraceVerifier.cs` imports only Olive.Domain). The hash is computed once by the endpoint (`ProposalEndpoints.cs:120`). Tests: `TRC_06_hash_difference_null_or_case_is_proposal_mismatch`, `TRC_06_one_character_changed_in_justification_after_tracing_is_trace_proposal_mismatch` | closed |
| T-03-08 | Elevation of privilege | ServiceTokenProvider | mitigate | Cache key `(TenantId, AgentId, KeyId)` (`ServiceTokenProvider.cs:20,26`). Revalidation on a hit (`:30-42`) and on a miss (`:78-88`). Over-long tokens are rejected without caching (`:84-88`). `UsableUntil = min(token,key) - skew` (`:90-98`). WR-06 refuses dead tokens (`:91-96`). Tests: `IDN_05_new_key_id_issues_a_new_token`, `IDN_06_cache_hit_is_revalidated_against_current_key_expiry`, `IDN_06_token_outliving_session_key_is_rejected_and_not_cached`, `IDN_07_*_mismatch_is_discarded_with_alarm`, `IDN_06_freshly_issued_token_already_expired_fails_without_alarm_and_is_not_cached` | closed |
| T-03-09 | Information disclosure | ServiceTokenGrant / SessionKey | mitigate | `ServiceTokenGrant.cs:7-18` is a sealed class (not a record) with redacting `ToString`, `IFormattable` and `[DebuggerDisplay("[REDACTED]")]`. The `ITraceResolver.cs:16` signature has no SessionKey. `Olive.Application.csproj` references only Domain, and a grep for ILogger/Console in `src/Olive.Application` finds nothing. Tests: `FND_06_service_token_grant_never_renders_token`, `IDN_08_resolver_port_has_exactly_grant_traceid_and_cancellation_parameters` | closed |
| T-03-10 | Denial of service | caches | mitigate | `Capacity = 10_000` with clear-on-full in `CachingTraceResolver.cs:8,24-27` and `ServiceTokenProvider.cs:17,101-104`. Test: `TRC_08_cap_clears_without_changing_results`. See audit note (no token-cache cap test) | closed |
| T-03-11 | Denial of service | resolver hang | mitigate | Linked CTS with `CancelAfter(options.Timeout)` (`TraceVerifier.cs:40-50`). The 5 s default is at `TraceVerdict.cs:15`. WR-01 adds an issuance timeout (`TraceVerifier.cs:15-25`). Tests: `TRC_07_resolver_timeout_is_unavailable_without_alarm`, `TRC_07_token_issuer_hang_times_out_to_unavailable_and_resolver_is_never_called`, `TRC_07_client_abort_propagates_instead_of_unavailable` | closed |
| T-03-12 | Tampering | not-found turned permanent | mitigate | Only `TraceLookup.Found` is cached (`CachingTraceResolver.cs:22`). `NotFound` maps to a 422 that stores nothing (`ProposalEndpoints.cs:138-140`). Tests: `TRC_08_failures_are_never_cached`, `TRC_08_not_found_then_found_hits_inner_twice`, `TRC_02_retry_with_same_run_id_after_fixture_appears_is_accepted_because_not_found_is_never_cached` | closed |
| T-03-13 | Spoofing | fixture resolver / stub issuer in production | mitigate | `FixtureTraceResolver : INonProductionOnly` (`FixtureTraceResolver.cs:11`) and `StubTokenIssuer : INonProductionOnly` (`StubTokenIssuer.cs:9`). `CachingTraceResolver.Inner` is at `:11`. The guard walks the decorator chain (`StartupGuards.cs:45-113`) and is invoked from `StartupGuardFilter.cs:16`. Tests: `TRC_09_production_refuses_fixture_trace_resolver`, `TRC_09_production_refuses_fixture_resolver_behind_caching_decorator`, `IDN_05_production_refuses_stub_token_issuer`, `TRC_08_decorator_exposes_inner_for_production_guard` | closed |
| T-03-14 | Elevation of privilege | non-Verified accepted | mitigate | Only `Verified` and `Refused` fall through to persistence (`ProposalEndpoints.cs:124-150`). The `default` arm returns 503 before any store call (`:141-149`). A missing session key also returns 503 (`:114-118`). Tests: the `AssertUnavailable` helper (0 rows, no `insert`/`insert_refused` event) runs in every `TraceFailClosedTests` fault test | closed |
| T-03-15 | Information disclosure | logs in endpoint and alarm | mitigate | The endpoint logs at `ProposalEndpoints.cs:84,129,136,145,170` carry only ids, a 12-char hash prefix, static codes and the exception type name. None logs trace_ref, a token or parameters (the plan's grep returns nothing). The alarm logs only code and component (`LoggingOperationalAlarm.cs:10`). Tests: sentinel scans in `ContainmentTests` (logs include the Critical alarm line), `TRC_07_resolver_exception_gives_503_retryable_and_stores_nothing` (exception message is absent from logs) | closed |
| T-03-16 | Information disclosure | session key reaching hometown | mitigate | `ITraceResolver.cs:16` takes `(ServiceTokenGrant, string, CancellationToken)`. The endpoint passes `sessionKey` only to `verifier.VerifyAsync` (`ProposalEndpoints.cs:121`), the verifier passes it only to `tokens.GetAsync` (`TraceVerifier.cs:20`), and the provider passes it only to `issuer.IssueAsync` (`ServiceTokenProvider.cs:55`). Tests: `IDN_08_only_popeye_ports_accept_a_session_key`, `IDN_08_session_key_reaches_only_popeye_and_service_token_only_hometown` | closed |
| T-03-17 | Tampering | malformed resolver response | mitigate | `TraceResolutionResponse.Parse` requires the exact seven fields, rejects duplicates (WR-03), checks kinds and patterns, and returns `Malformed` on any `JsonException` (`TraceResolutionResponse.cs:24-69`). The verifier maps `Malformed` to Unavailable plus an alarm (`TraceVerifier.cs:65-67`). Tests: `TRC_01_parser_returns_malformed_for_extra_missing_bad_version_and_uppercase_tenant`, `TRC_07_parser_returns_malformed_for_duplicate_keys_and_never_throws`, `TRC_07_malformed_resolver_response_gives_503_and_alarm` | closed |
| T-03-18 | Tampering | test auto-fixture middleware in production | mitigate | `grep IStartupFilter src` finds only `StartupGuardFilter` (`Program.cs:20`, `StartupGuardFilter.cs:8`). `AutoFixtureStartupFilter` exists only in `tests/Olive.Tests/OliveApiFactory.cs:105`, and `grep AutoFixture src` finds nothing. Test: `TRC_09_auto_fixture_mode_keeps_legacy_bodies_accepted` (exercises the test-only path) | closed |
| T-03-19 | Denial of service | unbounded caches / hang | mitigate | Caps as in T-03-10 and timeouts as in T-03-11. `OLIVE_TRACE_TIMEOUT_MS` is parsed eagerly and must be a positive integer, otherwise `InvalidOperationException` (`Program.cs:43-53`). It is documented in `.env.example:18`. Test: `TRC_07_resolver_hang_times_out_to_503_without_alarm` (100 ms via the setting). See audit note (no test for the invalid-value refusal) | closed |
| T-03-20 | Tampering | mismatched trace accepted | mitigate | `Refused` sets `refusalReason`, which routes to `InsertRefusedAsync` (`ProposalEndpoints.cs:131-137,159-161`). Tests: `TRC_03/04/05/06_*_is_trace_*` (all four reasons), `TRC_03_check_order_run_before_tenant_before_agent_before_hash`, `TRC_06_check_order_is_run_then_tenant_then_agent_then_hash` | closed |
| T-03-21 | Repudiation | refusal without audit | mitigate | As in T-03-05: both events are written in the same transaction (`PostgresProposalStore.cs:71-101`). Test: `AssertRefusal` asserts `submitted,trace_refused` for every reason | closed |
| T-03-22 | Information disclosure | refusal body leaks trace content | mitigate | The 201 body is `ProposalCreatedResponse(id, RefusedPolicy, refusalReason)` (`ProposalEndpoints.cs:180-182`). Test: `AssertRefusal` asserts the exact key set `[proposal_id, refusal_reason, state]` | closed |
| T-03-23 | Tampering | re-verification flips a refusal | mitigate | `DecideExistingAsync` replay (`ProposalEndpoints.cs:89-93`) runs before the trace step (`:114-121`), and the replay returns the stored `RefusalReason` (`:80`). Test: `TRC_03_replay_of_refused_run_returns_200_same_body_without_token_or_resolver`, which registers a now-matching fixture before the replay | closed |
| T-03-24 | Information disclosure | cross-tenant read of a refusal | mitigate | Tenant predicate plus RLS as in T-03-03. `GetAsync` returns 404 when the row is null (`ProposalEndpoints.cs:226-230`). Test: `TRC_03_cross_tenant_get_of_refusal_is_404` | closed |
| T-03-25 | Elevation of privilege | outage becomes acceptance | mitigate | Single default 503 arm (`ProposalEndpoints.cs:141-149`). Every port exception and timeout maps to Unavailable (`TraceVerifier.cs:22-29,47-55`, `ServiceTokenProvider.cs:57-61`). Tests: `AssertUnavailable` (0 rows, no insert event) in `TRC_07_resolver_unavailable/hang/exception/401/403/malformed`, `TRC_07_popeye_token_*`, `IDN_07_*`, `IDN_06_token_expiring_after_session_key_*` | closed |
| T-03-26 | Denial of service | not-found becomes permanent | mitigate | As in T-03-12. Tests: `TRC_02_unknown_trace_gives_422_retryable_and_stores_nothing`, `TRC_02_retry_with_same_run_id_after_fixture_appears_is_accepted_because_not_found_is_never_cached` | closed |
| T-03-27 | Information disclosure | 503 reveals cause | mitigate | The single public reason is `trace_verification_unavailable` (`ProposalEndpoints.cs:188`), with `retryable` and `Retry-After: 5` (`ProblemResults.cs:59-65`). The cause goes only to the Warning log (`:145`). Tests: `TRC_07_503_body_and_headers_never_expose_the_cause` (6 fault modes, body and headers), `TRC_07_503_carries_retryable_true_and_retry_after_5` | closed |
| T-03-28 | Repudiation | credential failure unnoticed | mitigate | Distinct `AlarmCodes` are raised at `TraceVerifier.cs:63,66,70` and `ServiceTokenProvider.cs:33,40,68,71,80,86`. The Host logs them as Critical (`LoggingOperationalAlarm.cs:10`). Tests: per-fault `Assert.Equal([AlarmCodes.X], factory.Alarms.Codes)` in `TraceFailClosedTests`, and `IDN_08_sentinel_key_and_token_absent_on_503_hometown_401_path_including_alarm_log` (Critical count > 0) | closed |
| T-03-29 | Denial of service | hung resolver | mitigate | As in T-03-11 and T-03-19. Tests: `TRC_07_resolver_hang_times_out_to_503_without_alarm` and `TRC_07_popeye_token_hang_times_out_to_503_without_alarm` (`OLIVE_TRACE_TIMEOUT_MS=100`) | closed |
| T-03-30 | Elevation of privilege | popeye token for wrong tenant/agent/key | mitigate | As in T-03-08. Test: theory `IDN_07_token_with_wrong_tenant_delegated_agent_or_key_id_gives_503_and_alarm_token_claims_mismatch` asserts `DoesNotContain("resolve", factory.Events)`, and `IDN_06_token_expiring_after_session_key_*` also asserts the resolver is never called | closed |
| T-03-31 | Tampering | fault doubles shipped | mitigate | `grep -rln "Faulting\|AutoFixture" src` finds nothing. The doubles are in `tests/Olive.Tests/FaultingDoubles.cs`. `StubTokenIssuer.cs` and `FixtureTraceResolver.cs` have no fault toggles (D-16) | closed |
| T-03-32 | Information disclosure | session key sent to hometown | mitigate | `[OutboundPort]` is on `ITraceResolver.cs:15`, `ITokenIssuer.cs:14` and `IKeyIntrospector.cs:8`. Tests: `IDN_08_only_popeye_ports_accept_a_session_key` (reflection), `IDN_08_hometown_project_sources_never_reference_session_key` (source scan), `IDN_08_session_key_reaches_only_popeye_and_service_token_only_hometown` (recorded calls) | closed |
| T-03-33 | Information disclosure | secrets in logs, rows, responses, alarm logs | mitigate | `SentinelScan` covers the response body and headers, Trace-level logs and `rows:*`. Tests: `IDN_08_sentinel_key_and_token_absent_*` on the verified, refused, 503+alarm, 422 and claims-mismatch alarm paths, plus `IDN_08_logging_grant_and_session_key_as_structured_arguments_is_redacted` | closed |
| T-03-34 | Tampering | unmarked outbound port | mitigate | Test: `IDN_08_every_application_port_implemented_in_an_integration_assembly_is_marked_outbound` covers the Popeye, Hometown and Entra assemblies, with an explicit non-port list and a non-vacuous `implemented > 0` | closed |
| T-03-35 | Information disclosure | vacuous scan | mitigate | Tests: `IDN_08_trace_level_logging_is_active_during_the_scan` (Trace count > 0 and "Trace verified" present); `IDN_08_session_key_reaches_only_popeye_and_service_token_only_hometown` (each of the three ports recorded a call); `IDN_08_scan_failure_names_the_source_but_never_echoes_the_sentinel` | closed |
| T-03-36 | Repudiation | wire-level capture absent | accept | See AR-03-01. Port-level capture is in place now. `ContainmentTests.cs:12-15` documents the Phase 6 JSONL source and the LIVE-03 gap | closed |
| T-03-SC | Tampering | package installs | accept | See AR-03-02. `git diff 0a0db3f^..HEAD -- '*.csproj'` shows no `PackageReference` changes. The only csproj change is an `EmbeddedResource` in `Olive.Integrations.Hometown.csproj` | closed |

*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-03-01 | T-03-36 | Phase 1 has no live transports, so containment is proven at the port level: reflection, recorded calls and sentinel scans. Wire-level capture arrives with LIVE-03, and the JSONL execution source is added through `SentinelScan.AddSource` in Phase 6. Until then IDN-08 and Success Criterion 5 are only partly satisfied | plan 03-06 threat model (Q19); auditor confirmed | 2026-10-22 |
| AR-03-02 | T-03-SC | No new NuGet packages were added in Phase 3. This was verified against git history. The Hometown project still has 0 `PackageReference` entries | plans 03-02/03/05/06 threat models; auditor confirmed | 2026-10-22 |

*Accepted risks do not resurface in future audit runs.*

---

## Unregistered Flags

| Flag | Source | Mapping | Severity |
|------|--------|---------|----------|
| Trace cache keyed `(tenant_id, trace_id)` only. Agent B in the same tenant gets agent A's cached `Found` without hometown seeing B's token, which acts as an existence oracle through `trace_agent_mismatch`. | 03-REVIEW.md WR-05 (skipped: TRC-08 specifies the key) | None. No register threat covers the cache bypassing per-token hometown authorization | WARNING (owner decision on TRC-08 needed) |
| `CachingTraceResolver` caches a `Found` whose `TraceId` differs from the requested key. The verifier rejects it on every hit (WR-04), so that trace returns 503 until restart or until the cache clears. | 03-REVIEW-FIX.md WR-04 note 2 | Closest is T-03-12 (failures never cached), but this is a liveness effect, not a mis-acceptance | INFO |

---

## Audit Notes (non-blocking)

- **T-03-10:** resolved 2026-10-22 by `/gsd-validate-phase 3`. `IDN_06_token_cache_cap_clears_without_changing_results` covers the `ServiceTokenProvider` cap.
- **T-03-19:** resolved 2026-10-22 by `/gsd-validate-phase 3`. `TRC_07_invalid_trace_timeout_refuses_start` (theory over 0, -5, abc) and the positive control `TRC_07_valid_trace_timeout_is_applied_at_start`. The same pass found that `OliveApiFactory`'s `extraConfig` was applied after `Program.cs` reads `OLIVE_TRACE_TIMEOUT_MS`, so the earlier "100 ms via the setting" hang tests had actually run on the 5 s default. The factory now also passes that key with `UseSetting`, so the 100 ms claims in T-03-19 and T-03-29 now hold.
- **T-03-02:** the plan says "owner-connection test". The actual tests use the app connection: the CHECK rejection gives 23514 because the app holds the INSERT grant on that column, and the UPDATE gives 42501. Together these prove the constraint and the grant.
- **T-03-14:** the null-session-key 503 guard (`ProposalEndpoints.cs:114-118`) has no test, because the auth handler rejects first with the same `AgentKeyHeader.TryRead`. This is the documented deviation in 03-03-SUMMARY and 03-VERIFICATION.
- **WR-04 alarm choice:** reusing `trace_response_malformed` for a trace_id mismatch is still awaiting human confirmation (03-REVIEW-FIX.md).

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-10-22 | 37 | 37 | 0 | gsd-security-auditor (verify mode, plan-time register) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-10-22
