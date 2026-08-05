create schema if not exists action_broker;
create schema if not exists integration_outbox;

create table if not exists action_broker.proposals (
    tenant_id uuid not null,
    proposal_id uuid not null,
    action_id text not null,
    parameters jsonb not null,
    trace_ref text not null,
    principal jsonb not null,
    purpose text not null,
    state text not null,
    policy_decisions jsonb not null,
    expires_at timestamptz not null,
    created_at timestamptz not null,
    primary key (tenant_id, proposal_id),
    check (state in ('proposed', 'refused_policy', 'awaiting_approval', 'approved', 'rejected', 'executed', 'execution_failed', 'expired'))
);

create table if not exists action_broker.queue_items (
    tenant_id uuid not null,
    item_id uuid not null,
    proposal_id uuid null,
    kind text not null,
    purpose text null,
    subject_ref text not null,
    payload_ref text not null,
    state text not null,
    claimed_by text null,
    decided_by text null,
    decision text null,
    decided_at timestamptz null,
    rationale text null,
    created_at timestamptz not null,
    primary key (tenant_id, item_id),
    foreign key (tenant_id, proposal_id) references action_broker.proposals (tenant_id, proposal_id),
    check (state in ('awaiting_approval', 'approved', 'rejected')),
    check (decision is null or decision in ('approved', 'rejected')),
    check (payload_ref <> ''),
    check ((decision is null) or (decided_by is not null and rationale is not null and decided_at is not null))
);

create table if not exists action_broker.executions (
    tenant_id uuid not null,
    execution_id uuid not null,
    proposal_id uuid not null,
    outcome text not null,
    attempted_at timestamptz not null,
    target_response_ref text not null,
    approver text not null,
    primary key (tenant_id, execution_id),
    unique (tenant_id, proposal_id),
    foreign key (tenant_id, proposal_id) references action_broker.proposals (tenant_id, proposal_id),
    check (outcome in ('executed', 'execution_failed'))
);

create table if not exists action_broker.idempotency_records (
    tenant_id uuid not null,
    idempotency_key text not null,
    proposal_id uuid not null,
    execution_id uuid not null,
    created_at timestamptz not null,
    primary key (tenant_id, idempotency_key),
    foreign key (tenant_id, proposal_id) references action_broker.proposals (tenant_id, proposal_id),
    foreign key (tenant_id, execution_id) references action_broker.executions (tenant_id, execution_id)
);

create table if not exists integration_outbox.outbox_facts (
    tenant_id uuid not null,
    outbox_fact_id uuid not null,
    aggregate_type text not null,
    aggregate_id uuid not null,
    event_type text not null,
    payload jsonb not null,
    idempotency_key text not null,
    publish_state text not null default 'pending',
    created_at timestamptz not null,
    primary key (tenant_id, outbox_fact_id),
    unique (tenant_id, idempotency_key, event_type),
    check (publish_state in ('pending', 'published', 'execution_failed'))
);

create or replace function action_broker.current_tenant_id() returns uuid language sql stable as $$
    select nullif(current_setting('olive.tenant_id', true), '')::uuid
$$;

alter table action_broker.proposals enable row level security;
alter table action_broker.proposals force row level security;
alter table action_broker.queue_items enable row level security;
alter table action_broker.queue_items force row level security;
alter table action_broker.executions enable row level security;
alter table action_broker.executions force row level security;
alter table action_broker.idempotency_records enable row level security;
alter table action_broker.idempotency_records force row level security;
alter table integration_outbox.outbox_facts enable row level security;
alter table integration_outbox.outbox_facts force row level security;

create policy proposals_tenant_isolation on action_broker.proposals using (tenant_id = action_broker.current_tenant_id()) with check (tenant_id = action_broker.current_tenant_id());
create policy queue_items_tenant_isolation on action_broker.queue_items using (tenant_id = action_broker.current_tenant_id()) with check (tenant_id = action_broker.current_tenant_id());
create policy executions_tenant_isolation on action_broker.executions using (tenant_id = action_broker.current_tenant_id()) with check (tenant_id = action_broker.current_tenant_id());
create policy idempotency_records_tenant_isolation on action_broker.idempotency_records using (tenant_id = action_broker.current_tenant_id()) with check (tenant_id = action_broker.current_tenant_id());
create policy outbox_tenant_isolation on integration_outbox.outbox_facts using (tenant_id = action_broker.current_tenant_id()) with check (tenant_id = action_broker.current_tenant_id());

-- Multi-row writes use SERIALIZABLE isolation. Callers verify INSERT row count before COMMIT.
