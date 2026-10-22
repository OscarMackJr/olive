-- API-06 / D-06 / D-08: store canonical JCS body, its SHA-256 and the four-key proposal hash so the hash is recomputable at audit time.
-- Columns are nullable: durable Phase 1 rows have no hashes; a NULL-hash row is never a hash match (consumer treats it as a conflict).
-- 0001-0003 are checksum-pinned and unchanged.
alter table proposal add column canonical_body text, add column body_sha256 text, add column proposal_hash text;
alter table proposal add constraint proposal_body_sha256_format check (body_sha256 is null or body_sha256 ~ '^[0-9a-f]{64}$');
alter table proposal add constraint proposal_hash_format check (proposal_hash is null or proposal_hash ~ '^[0-9a-f]{64}$');
alter table proposal add constraint proposal_hash_columns_all_or_none check (
    (canonical_body is null and body_sha256 is null and proposal_hash is null)
    or (canonical_body is not null and body_sha256 is not null and proposal_hash is not null));
-- Additive: 0003 already revoked table-wide INSERT; the column UPDATE grant from 0002 (state only) is untouched.
grant insert (canonical_body, body_sha256, proposal_hash) on proposal to olive_app;
