-- 0008: generic review queue (QUE-01, QUE-09). queue_item and queue_item_event.
-- Q8: a decided item must carry decided_by = claimed_by, a non-blank rationale, a decision and an auth_source.
-- Q9: queue_item_event is INSERT-only for olive_app (append-only audit).
-- D-10: refs are reference-only and fixed-shape per kind; action_approval refs are olive:proposal/<proposal_id>.
-- 0001-0007 are checksum-pinned and unchanged.
create table queue_item (
    tenant_id uuid not null,
    item_id uuid not null,
    kind text not null check (kind in ('action_approval', 'identity_review')),
    subtype text not null,
    producer text not null check (producer in ('olive', 'bluto')),
    producer_ref uuid not null,
    subject_ref text not null,
    payload_ref text not null,
    proposal_id uuid null,
    ruleset_version text null,
    state text not null default 'open' check (state in ('open', 'claimed', 'decided', 'withdrawn')),
    claimed_by text null,
    claimed_at timestamptz null,
    decision text null,
    decided_by text null,
    decided_at timestamptz null,
    rationale text null check (rationale is null or length(rationale) <= 4000),
    auth_source text null check (auth_source is null or auth_source in ('entra', 'stub')),
    withdrawn_reason text null check (withdrawn_reason is null or withdrawn_reason ~ '^[a-z][a-z_]{0,63}$'),
    expires_at timestamptz null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, item_id),
    constraint queue_item_producer_unique unique (tenant_id, producer, producer_ref),
    foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id),
    constraint queue_item_subtype_by_kind check (
        (kind = 'action_approval' and subtype = 'proposal')
        or (kind = 'identity_review' and subtype = 'candidate_pair')),
    constraint queue_item_refs_by_kind check (
        (kind = 'action_approval'
            and producer = 'olive'
            and proposal_id is not null
            and subject_ref = 'olive:proposal/' || proposal_id::text
            and payload_ref = subject_ref
            and producer_ref = proposal_id)
        or (kind = 'identity_review'
            and producer = 'bluto'
            and proposal_id is null
            and subject_ref ~ '^bluto:candidate/[0-9a-f-]{36}$'
            and payload_ref ~ '^bluto:review-evidence/[0-9a-f-]{36}$')),
    constraint queue_item_state_shape check (
        (state = 'open'
            and claimed_by is null and claimed_at is null
            and decision is null and decided_by is null and decided_at is null
            and rationale is null and auth_source is null and withdrawn_reason is null)
        or (state = 'claimed'
            and claimed_by is not null
            and decision is null and decided_by is null and decided_at is null
            and withdrawn_reason is null)
        or (state = 'decided'
            and claimed_by is not null
            and decided_by = claimed_by
            and decision is not null and decided_at is not null
            and auth_source is not null
            and rationale is not null and rationale !~ '^\s*$'
            and withdrawn_reason is null)
        or (state = 'withdrawn'
            and decision is null and withdrawn_reason is not null)),
    constraint queue_item_decision_by_kind check (
        decision is null
        or (kind = 'action_approval' and decision in ('approve', 'reject'))
        or (kind = 'identity_review' and decision in ('accept', 'reject', 'defer')))
);

create table queue_item_event (
    tenant_id uuid not null,
    event_id uuid not null,
    seq bigint generated always as identity,
    item_id uuid not null,
    event_type text not null check (event_type in ('created', 'claimed', 'released', 'decided', 'withdrawn')),
    from_state text null,
    to_state text not null,
    actor text not null,
    detail jsonb null,
    created_at timestamptz not null default now(),
    primary key (tenant_id, event_id),
    foreign key (tenant_id, item_id) references queue_item (tenant_id, item_id)
);

create index queue_item_open_idx on queue_item (tenant_id, kind, state, created_at);
create index queue_item_event_item_idx on queue_item_event (tenant_id, item_id, seq);

alter table queue_item enable row level security;
alter table queue_item force row level security;
create policy tenant_isolation on queue_item
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);

alter table queue_item_event enable row level security;
alter table queue_item_event force row level security;
create policy tenant_isolation on queue_item_event
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);

revoke all on queue_item, queue_item_event from public;
grant select on queue_item, queue_item_event to olive_reader;
grant select on queue_item to olive_app;
grant insert (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, proposal_id, ruleset_version, expires_at)
    on queue_item to olive_app;
grant update (state, claimed_by, claimed_at, decision, decided_by, decided_at, rationale, auth_source, withdrawn_reason)
    on queue_item to olive_app;
grant select, insert on queue_item_event to olive_app;
