using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>
/// The port of the storage of the trash, implemented in memory in this package and on PostgreSQL in
/// <c>Stellaegis.Trash.PostgreSQL</c>. Every call receives the space and the actor: the store opens its work in
/// them, and a database isolates on what it reads from the session.
/// </summary>
public interface ITrashStore
{
    /// <summary>
    /// Stores an item with its payload, replacing the item standing for the same object — same space, kind and
    /// source identifier — if there is one. The identifier of the item stored is the one given, or the replaced
    /// item's when the object was in the trash already, so that a replay keeps its identity.
    /// </summary>
    Task<TrashItem> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>What identifies an item of the space, or <see langword="null"/>.</summary>
    Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>An item of the space with its payload, or <see langword="null"/>.</summary>
    Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>The items of the space in the trash, the most recently trashed first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an item of the space as restored by <paramref name="actor"/> at <paramref name="restoredAt"/>, when
    /// it is in the trash; <see langword="false"/> when it is absent or restored already.
    /// </summary>
    Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken = default);

    /// <summary>Deletes an item of the space; <see langword="false"/> when it is absent.</summary>
    Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes the items of the space whose deadline has passed at <paramref name="asOf"/>, and answers how many.</summary>
    Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> items of the compartment, whatever their state, and answers how many.</summary>
    Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, Actor actor, CancellationToken cancellationToken = default);
}
