namespace Olive.Persistence;

// Every statement keeps the application tenant predicate even though forced RLS also applies (AGENTS.md).
// Time comes from the caller (@now), never from the database clock, so expiry is testable and reads stay write-free.
public static class QueueSql
{
    public const string InsertItem =
        "insert into queue_item (tenant_id, item_id, kind, subtype, producer, producer_ref, subject_ref, payload_ref, proposal_id, expires_at) " +
        "values (@tenant_id, @item_id, @kind, @subtype, @producer, @producer_ref, @subject_ref, @payload_ref, @proposal_id, @expires_at)";

    public const string InsertEvent =
        "insert into queue_item_event (tenant_id, event_id, item_id, event_type, from_state, to_state, actor, detail) " +
        "values (@tenant_id, @event_id, @item_id, @event_type, @from_state, @to_state, @actor, @detail::jsonb)";

    private const string ItemColumns =
        "tenant_id, item_id, kind, subtype, producer, producer_ref, state, subject_ref, payload_ref, claimed_by, created_at, expires_at";

    private const string DetailColumns =
        ItemColumns + ", decision, decided_by, decided_at, auth_source, withdrawn_reason, proposal_id";

    public const string GetItem =
        "select " + DetailColumns + " from queue_item where tenant_id = @tenant_id and item_id = @item_id";

    public const string ItemExists =
        "select exists (select 1 from queue_item where tenant_id = @tenant_id and item_id = @item_id)";

    public const string GetItemForUpdate = GetItem + " for update";

    public const string List =
        "select " + ItemColumns + " from queue_item where tenant_id = @tenant_id " +
        "and (@kind::text is null or kind = @kind) and (@state::text is null or state = @state) " +
        "and not (state in ('open','claimed') and expires_at is not null and expires_at <= @now) " +
        "order by created_at, item_id limit @limit";

    public const string Events =
        "select seq, event_type, from_state, to_state, actor, created_at, detail::text as detail_json from queue_item_event " +
        "where tenant_id = @tenant_id and item_id = @item_id order by seq";

    public const string Payload =
        "select q.item_id, q.kind, p.action_type, p.parameters::text as parameters_json, p.justification, p.on_behalf_of, p.expires_at, p.trace_ref " +
        "from queue_item q join proposal p on p.tenant_id = q.tenant_id and p.proposal_id = q.proposal_id " +
        "where q.tenant_id = @tenant_id and q.item_id = @item_id";

    // QUE-05: guarded by state so the row lock plus this predicate leave exactly one winner.
    public const string Claim =
        "update queue_item set state = 'claimed', claimed_by = @actor, claimed_at = @now " +
        "where tenant_id = @tenant_id and item_id = @item_id and state = 'open'";

    public const string Release =
        "update queue_item set state = 'open', claimed_by = null, claimed_at = null " +
        "where tenant_id = @tenant_id and item_id = @item_id and state = 'claimed' and claimed_by = @actor";

    public const string Withdraw =
        "update queue_item set state = 'withdrawn', withdrawn_reason = @reason " +
        "where tenant_id = @tenant_id and item_id = @item_id and state in ('open','claimed')";
}
