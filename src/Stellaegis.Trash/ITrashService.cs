using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>
/// The trash of an application: a serialized object goes in with its identity, comes back out to be recreated
/// under the same identity, or is deleted for good — by its owner before its deadline, by the purge of the expired
/// items after it, by the purge of its compartment when the compartment goes. Every call acts in a
/// <see cref="Space"/>, for an <see cref="Actor"/>, recorded on what it changes.
/// </summary>
/// <remarks>
/// <para>
/// The trash keeps a snapshot, opaque to it: what the object was when it was trashed. Recreating the object from
/// the payload is the application's, as is making sure the object is gone from its own data when it is trashed —
/// trash first, then delete, so that a crash in between leaves a trashed copy of an object that still exists,
/// which trashing again replaces, rather than an object that is nowhere.
/// </para>
/// <para>
/// A restored object keeps its identity: the application recreates it under <see cref="TrashItem.SourceUniqueId"/>,
/// and the trash never gives that identifier to anything else. Who trashed and who restored are recorded on the
/// item, as the trace of both acts.
/// </para>
/// </remarks>
public interface ITrashService
{
    /// <summary>
    /// Puts an object in the trash. An object trashed again — the same kind and identifier in the same space —
    /// replaces its previous item, restored or not: trashing is idempotent, so a replay after a crash is harmless.
    /// </summary>
    /// <exception cref="TrashRequestException">The request does not describe an object the trash can take.</exception>
    Task<TrashItem> TrashAsync(Space space, TrashRequest request, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// The items of the space still in the trash, the most recently trashed first, at most
    /// <see cref="TrashOptions.ListLimit"/> of them; those of one kind only when <paramref name="sourceKind"/> is given.
    /// </summary>
    Task<IReadOnlyList<TrashItem>> ListAsync(Space space, Actor actor, string? sourceKind = null, CancellationToken cancellationToken = default);

    /// <summary>What identifies an item, or <see langword="null"/> when the space has none with that identifier.</summary>
    Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>An item with its payload, without restoring it, or <see langword="null"/>.</summary>
    Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the payload back, and records the item as restored by <paramref name="actor"/>. The application
    /// recreates its object from the payload, under the item's <see cref="TrashItem.SourceUniqueId"/>.
    /// </summary>
    /// <exception cref="TrashItemNotFoundException">The space has no item with that identifier.</exception>
    /// <exception cref="TrashItemRestoredException">The item was restored already.</exception>
    /// <exception cref="TrashItemExpiredException">The deadline of the item has passed.</exception>
    Task<TrashedObject> RestoreAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item for good, before its deadline.</summary>
    /// <exception cref="TrashItemNotFoundException">The space has no item with that identifier.</exception>
    Task DeleteAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes for good the items of the space whose deadline has passed at <paramref name="asOf"/> — now unless
    /// given —, and answers how many. The work of a scheduled task, acting under its own name, or of an
    /// administrator.
    /// </summary>
    Task<int> PurgeExpiredAsync(Space space, Actor actor, DateTimeOffset? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every item of a compartment for good, whatever its state or deadline, and answers how many. Acts on
    /// the compartment, under <paramref name="actor"/>, in batches: a replayed purge completes an interrupted one,
    /// and answers 0 once nothing is left. Not offered under an isolation by owner, where it would delete the
    /// items of its caller only.
    /// </summary>
    Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default);
}
