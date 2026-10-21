-- WR-02 / DB-04: olive_app may update only proposal.state (Decision 13 immutability).
-- Later phases add mutable columns explicitly. 0001 is checksum-pinned and unchanged.
revoke update on proposal from olive_app;
grant update (state) on proposal to olive_app;
