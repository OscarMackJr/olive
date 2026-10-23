-- POL-07 / D-14: absolute server-computed proposal expiry, plus the rate-count index used by the rate_limit check (D-08).
-- expires_at is nullable because pre-existing rows and refusals carry none.
-- olive_app gets INSERT only: no UPDATE grant, so expiry cannot be changed after insert.
-- 0001-0006 are checksum-pinned and unchanged. No new table, so no new RLS.
alter table proposal add column expires_at timestamptz null;
grant insert (expires_at) on proposal to olive_app;
create index proposal_rate_idx on proposal (tenant_id, agent_id, action_type, created_at);
