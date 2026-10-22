-- TRC-03 / TRC-09 / D-05: record the trace verification outcome and a stored refusal reason on the proposal.
-- Columns are nullable on purpose: pre-Phase-3 rows and refusals that never reach trace verification (e.g. Phase 4 not_in_catalog) carry no trace data.
-- 0003 deliberately kept state out of the olive_app INSERT grant; a refusal is stored by insert (default 'proposed') then compare-and-set to refused_policy.
-- 0001-0005 are checksum-pinned and unchanged.
alter table proposal add column trace_verification text, add column trace_schema_version text, add column refusal_reason text;
alter table proposal add constraint proposal_trace_verification_values check (trace_verification is null or trace_verification in ('fixture', 'hometown'));
alter table proposal add constraint proposal_trace_schema_version_format check (trace_schema_version is null or (trace_verification is not null and trace_schema_version ~ '^ate/[0-9]+\.[0-9]+$'));
-- The reason list is deliberately not pinned here so Phase 4 can add reasons without another constraint change.
alter table proposal add constraint proposal_refusal_reason_format check (refusal_reason is null or refusal_reason ~ '^[a-z][a-z_]{0,63}$');
alter table proposal add constraint proposal_refusal_state_agrees check ((state = 'refused_policy') = (refusal_reason is not null));
-- Additive: state stays excluded from INSERT; the column UPDATE grant on state from 0002 is untouched.
grant insert (trace_verification, trace_schema_version) on proposal to olive_app;
grant update (refusal_reason) on proposal to olive_app;
