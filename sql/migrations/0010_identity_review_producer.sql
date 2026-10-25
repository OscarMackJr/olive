-- 0010: identity_review producer support (IRQ-01, IRQ-02, IRQ-03).
-- body_sha256 binds an identity_review item to the producer body hash (D-13); required for that kind.
-- queue_item_event gains the 'deferred' event type (D-09). A poll index serves the decided-items feed.
-- body_sha256 is INSERT-only for olive_app: it is in no UPDATE grant.
-- 0001-0009 are checksum-pinned and unchanged.
alter table queue_item add column body_sha256 text null;

alter table queue_item add constraint queue_item_body_sha256_format
    check (body_sha256 is null or body_sha256 ~ '^[0-9a-f]{64}$');

alter table queue_item add constraint queue_item_body_sha256_by_kind
    check (kind <> 'identity_review' or body_sha256 is not null);

alter table queue_item_event drop constraint queue_item_event_event_type_check;
alter table queue_item_event add constraint queue_item_event_event_type_check
    check (event_type in ('created', 'claimed', 'released', 'decided', 'withdrawn', 'deferred'));

create index queue_item_decided_poll_idx on queue_item (tenant_id, producer, decided_at, item_id)
    where state = 'decided';

grant insert (body_sha256) on queue_item to olive_app;
