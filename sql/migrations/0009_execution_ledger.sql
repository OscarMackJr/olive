-- 0009: INSERT-only execution ledger and the execution_chain view (EXE-02, EXE-04, EXE-06, DB-03, DB-04).
-- EXE-02 reading (D-07): "unique ledger row per (tenant_id, proposal_id)" is one row per phase, i.e. unique (tenant_id, proposal_id, phase), so at most two rows per proposal (started, finished).
-- D-13: execution_mode is pinned to 'simulated'. D-09: reason is a static snake_case code, never exception text.
-- 0001-0008 are checksum-pinned and unchanged.
create table execution_ledger (
    tenant_id uuid not null,
    execution_id uuid not null,
    proposal_id uuid not null,
    phase text not null check (phase in ('started', 'finished')),
    execution_mode text not null check (execution_mode = 'simulated'),
    outcome text null check (outcome is null or outcome in ('succeeded', 'failed')),
    reason text null check (reason is null or reason ~ '^[a-z][a-z_]{0,63}$'),
    result_ref text null,
    recorded_at timestamptz not null,
    primary key (tenant_id, execution_id),
    constraint execution_ledger_phase_unique unique (tenant_id, proposal_id, phase),
    foreign key (tenant_id, proposal_id) references proposal (tenant_id, proposal_id),
    constraint execution_ledger_shape check (
        (phase = 'started' and outcome is null and reason is null and result_ref is null)
        or (phase = 'finished' and outcome = 'succeeded' and reason is null and result_ref is not null)
        or (phase = 'finished' and outcome = 'failed' and reason is not null))
);

alter table execution_ledger enable row level security;
alter table execution_ledger force row level security;
create policy tenant_isolation on execution_ledger
    using (tenant_id = current_setting('olive.tenant_id')::uuid)
    with check (tenant_id = current_setting('olive.tenant_id')::uuid);

revoke all on execution_ledger from public;
grant select, insert on execution_ledger to olive_app;
grant select on execution_ledger to olive_reader;

-- D-14: the chain view is anchored on the started row (its execution_id is the execution record id).
-- security_invoker makes the caller's RLS bind on every base table. It deliberately omits request bodies and free text supplied by the agent.
create view execution_chain with (security_invoker = true) as
select
    s.tenant_id,
    s.execution_id,
    s.proposal_id,
    'olive:proposal/' || s.proposal_id::text as proposal_ref,
    p.agent_id,
    p.on_behalf_of,
    p.purpose,
    p.action_type,
    p.state as proposal_state,
    p.trace_ref,
    p.trace_verification,
    p.trace_schema_version,
    p.proposal_hash,
    pe.detail as policy_decision,
    q.decided_by,
    q.auth_source,
    q.decided_at,
    q.rationale,
    s.execution_mode,
    s.recorded_at as started_at,
    f.outcome,
    f.reason,
    f.result_ref,
    f.recorded_at as finished_at
from execution_ledger s
join proposal p on p.tenant_id = s.tenant_id and p.proposal_id = s.proposal_id
left join lateral (
    select e.detail from proposal_event e
    where e.tenant_id = s.tenant_id and e.proposal_id = s.proposal_id and e.event_type = 'policy_evaluated'
    order by e.created_at, e.event_id
    limit 1) pe on true
left join queue_item q on q.tenant_id = s.tenant_id and q.proposal_id = s.proposal_id
    and q.kind = 'action_approval' and q.state = 'decided'
left join execution_ledger f on f.tenant_id = s.tenant_id and f.proposal_id = s.proposal_id and f.phase = 'finished'
where s.phase = 'started';

revoke all on execution_chain from public;
grant select on execution_chain to olive_reader, olive_app;
