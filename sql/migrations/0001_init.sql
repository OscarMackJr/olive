-- 0001: proposal and proposal_event (D-03). Applied as olive_owner in the public schema.
create table proposal (
    tenant_id uuid not null,
    proposal_id uuid not null,
    state text not null default 'proposed'
        check (state in ('proposed', 'refused_policy', 'awaiting_approval', 'approved', 'rejected', 'executed', 'execution_failed', 'expired')),
    agent_id text not null,
    key_id text not null,
    on_behalf_of text not null,
    purpose text not null,
    key_expires_at timestamptz not null,
    nono_session_id text null,
    run_id text not null check (length(run_id) between 1 and 256),
    action_type text null,
    party_ref text null,
    parameters jsonb null check (parameters is null or jsonb_typeof(parameters) = 'object'),
    justification text null,
    trace_ref text null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, proposal_id),
    constraint proposal_run_unique unique (tenant_id, agent_id, run_id)
);

create table proposal_event (
    tenant_id uuid not null,
    event_id uuid not null,
    proposal_id uuid not null,
    event_type text not null,
    from_state text null,
    to_state text not null,
    actor text not null,
    detail jsonb null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, event_id),
    foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id)
);

alter table proposal enable row level security;
alter table proposal force row level security;
create policy tenant_isolation on proposal
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);

alter table proposal_event enable row level security;
alter table proposal_event force row level security;
create policy tenant_isolation on proposal_event
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);

revoke all on schema public from public;
grant usage on schema public to olive_app, olive_reader;
revoke all on proposal, proposal_event from public;
grant select, insert, update on proposal to olive_app;
grant select, insert on proposal_event to olive_app;
grant select on proposal, proposal_event to olive_reader;

-- Multi-row writes use SERIALIZABLE only where a phase needs it.
