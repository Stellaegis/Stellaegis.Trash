-- ============================================================================
-- Stellaegis.Trash — schema trash, script 0001
-- ============================================================================
-- Applied by DatabaseLifecycle.InstallAsync (Stellaegis.Primitives.Data), which creates the schema,
-- the pgcrypto extension and trash._schema_migrations before this script runs, and grants the
-- application role its rights after it. All constraints and indexes are explicitly named.
--
-- One table: an item is a frozen, opaque snapshot of an object of the application — serialized
-- by the application, encrypted here — with what identifies it, who trashed it and when, its
-- deadline, and who restored it if anybody did. The trash interprets nothing of the payload.
--
-- Isolation: every item carries its compartment, null for the shared space, and every query of
-- the store filters on it. Row-level security repeats the filter: the store opens each connection
-- in a space, set in the session context, and a row of another space is invisible whatever the
-- query. owner_isolation.sql, applied by the installation when the frame declares an isolation by
-- owner, further keeps each actor to the items it trashed.
--
-- Keys: trash_item_unique_id uuid is the primary key; no bigint surrogate, every access starts
-- from the public identifier. One item per object: the unique index on the space, the kind and
-- the source identifier makes trashing idempotent (on conflict ... do update), null standing for
-- the shared space through coalesce.
-- ============================================================================

create table trash.items (
  trash_item_unique_id  uuid        not null default gen_random_uuid(),
  compartment           text,
  source_kind           text        not null,
  source_unique_id      uuid        not null,
  display_name          text        not null,
  content_type          text        not null,
  payload               bytea       not null,
  payload_length        bigint      not null,
  key_slot              smallint    not null,
  status                text        not null default 'trashed',
  trashed_at            timestamptz not null default now(),
  trashed_by            text        not null,
  purge_at              timestamptz,
  restored_at           timestamptz,
  restored_by           text,

  constraint pk_items primary key (trash_item_unique_id),
  constraint ck_items_compartment check (compartment is null or (compartment ~ '^[a-z][a-z0-9-]{1,61}[a-z0-9]$' and compartment !~ '--')),
  constraint ck_items_source_kind check (source_kind ~ '^[a-z](_?[a-z0-9])*$' and char_length(source_kind) <= 63),
  constraint ck_items_display_name check (char_length(display_name) between 1 and 256),
  constraint ck_items_content_type check (char_length(content_type) between 1 and 255),
  constraint ck_items_payload_length check (payload_length >= 1),
  constraint ck_items_key_slot check (key_slot in (1, 2)),
  constraint ck_items_status check (status in ('trashed', 'restored')),
  constraint ck_items_trashed_by check (char_length(trashed_by) between 1 and 128),
  constraint ck_items_restored check (
    (status = 'trashed' and restored_at is null and restored_by is null)
    or (status = 'restored' and restored_at is not null and restored_by is not null)
  )
);

comment on column trash.items.payload is
  'The object as the application serialized it, encrypted with pgp_sym_encrypt_bytea under the passphrase of key_slot: a reader of the database without the configuration of the application reads nothing.';
comment on column trash.items.purge_at is
  'When the purge of the expired items deletes the row, restored or not; null keeps it until somebody deletes it.';

-- One item per object of a space; the shared space stands as '_shared', which no compartment can carry.
create unique index uq_items_source on trash.items (coalesce(compartment, '_shared'), source_kind, source_unique_id);

-- The list of a space, the most recently trashed first.
create index ix_items_space_trashed on trash.items (compartment, trashed_at desc, trash_item_unique_id desc) where status = 'trashed';

-- The purge of the expired items.
create index ix_items_purge_at on trash.items (compartment, purge_at) where purge_at is not null;

-- ============================================================================
-- Row-level security: the session context tells the database which space is working, as
-- Stellaegis.Primitives.Data sets it: stellaegis.space holds the compartment, or '_shared'. A
-- session without a space sees nothing and writes nothing.
--
-- force: without it, the owner of the table escapes the policy, and an application connected
-- under the installing role would run with no isolation and no error.
-- ============================================================================

alter table trash.items enable row level security;
alter table trash.items force row level security;

create policy pl_items_space on trash.items
  using (
    case current_setting('stellaegis.space', true)
      when '_shared' then compartment is null
      else compartment = current_setting('stellaegis.space', true)
    end)
  with check (
    case current_setting('stellaegis.space', true)
      when '_shared' then compartment is null
      else compartment = current_setting('stellaegis.space', true)
    end);
