-- Olive cluster-level roles (spec section 13, DB-01). Idempotent; run as a superuser (nexus).
-- No credentials here: they are set separately, outside version control.
do $$
begin
    if not exists (select from pg_roles where rolname = 'olive_owner') then
        create role olive_owner nologin;
    end if;
    if not exists (select from pg_roles where rolname = 'olive_migrator') then
        create role olive_migrator login;
    end if;
    if not exists (select from pg_roles where rolname = 'olive_app') then
        create role olive_app login;
    end if;
    if not exists (select from pg_roles where rolname = 'olive_reader') then
        create role olive_reader login;
    end if;
end
$$;

-- Re-asserted on every run so drift self-heals.
alter role olive_owner nosuperuser nobypassrls nocreaterole nocreatedb noreplication;
alter role olive_migrator nosuperuser nobypassrls nocreaterole nocreatedb noreplication;
alter role olive_app nosuperuser nobypassrls nocreaterole nocreatedb noreplication;
alter role olive_reader nosuperuser nobypassrls nocreaterole nocreatedb noreplication;

grant olive_owner to olive_migrator;
