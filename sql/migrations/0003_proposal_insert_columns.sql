-- WR-09 / DB-04 / Decision 13: olive_app may insert only application-supplied proposal columns.
-- Excluded columns take table defaults, so a new row is always in the initial state with a server-set timestamp.
-- 0001 and 0002 are checksum-pinned and unchanged.
revoke insert on proposal from olive_app;
grant insert (tenant_id, proposal_id, agent_id, key_id, on_behalf_of, purpose, key_expires_at, nono_session_id, run_id, action_type, party_ref, parameters, justification, trace_ref) on proposal to olive_app;
