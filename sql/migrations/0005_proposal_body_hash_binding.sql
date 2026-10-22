-- WR-04 / API-06: bind body_sha256 to canonical_body so the database refuses a hash that does not match the stored canonical text.
-- body_sha256 is the lowercase hex SHA-256 of the UTF-8 bytes of the JCS canonical body (Jcs.Sha256Hex). sha256(bytea) is built in
-- since PostgreSQL 11. Legacy rows have NULL hash columns and pass; existing hashed rows are validated when the constraint is added.
-- 0001-0004 are checksum-pinned and unchanged.
alter table proposal add constraint proposal_body_sha256_matches_canonical_body check (
    canonical_body is null
    or body_sha256 = encode(sha256(convert_to(canonical_body, 'UTF8')), 'hex'));
