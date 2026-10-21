-- ============================================================================
-- Stellaegis.Trash — removes the isolation by owner
-- ============================================================================
-- Applied by the installation when the application frame declares no isolation by owner, so
-- that a database once installed under it follows the frame. Replayable.
-- ============================================================================

drop policy if exists pl_items_owner on trash.items;
drop index if exists trash.ix_items_space_owner_trashed;
