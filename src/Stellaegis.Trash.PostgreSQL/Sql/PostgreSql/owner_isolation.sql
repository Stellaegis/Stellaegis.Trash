-- ============================================================================
-- Stellaegis.Trash — isolation by owner
-- ============================================================================
-- Applied by the installation when the application frame declares WithOwnerIsolation(): each
-- actor sees, restores and deletes the items it trashed, within its space, and nobody else's.
-- Restrictive: it narrows the space policy, never widens it. The actor is the one the store sets
-- in the session context, stellaegis.actor; a session without one sees nothing.
--
-- A plain equality, so that the index below serves it: the planner takes an index condition from
-- current_setting, which is stable, and none from an "or". The purge of a compartment is not
-- offered under this policy: it would delete the items of its caller only.
--
-- Replayable.
-- ============================================================================

drop policy if exists pl_items_owner on trash.items;

create policy pl_items_owner on trash.items
  as restrictive
  using (trashed_by = current_setting('stellaegis.actor', true))
  with check (trashed_by = current_setting('stellaegis.actor', true));

create index if not exists ix_items_space_owner_trashed
  on trash.items (compartment, trashed_by, trashed_at desc, trash_item_unique_id desc);
