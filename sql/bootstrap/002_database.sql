-- Local psql use only: run by nexus via psql after 001_roles.sql.
-- CREATE DATABASE cannot run inside a DO block, hence \gexec. Passwords are set separately with psql variables.
-- The test fixture performs the equivalent in C#.
select 'create database olive owner olive_owner' where not exists (select from pg_database where datname = 'olive')
\gexec

revoke all on database olive from public;
grant connect on database olive to olive_migrator, olive_app, olive_reader;
