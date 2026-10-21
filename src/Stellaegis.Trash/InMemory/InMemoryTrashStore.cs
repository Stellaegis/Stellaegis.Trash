using Stellaegis.Primitives;

namespace Stellaegis.Trash.InMemory;

/// <summary>
/// The store in memory, for development and tests: one dictionary for the whole process, filtered by space on
/// every call. It holds no isolation by owner, and declares none.
/// </summary>
internal sealed class InMemoryTrashStore : ITrashStore
{
    private readonly Dictionary<Guid, (TrashItem Item, byte[] Payload)> _items = [];
    private readonly Lock _lock = new();

    public Task<TrashItem> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(item);
        lock (_lock)
        {
            var previous = _items.Values.Select(entry => entry.Item).FirstOrDefault(known =>
                known.Space == item.Space
                && string.Equals(known.SourceKind, item.SourceKind, StringComparison.Ordinal)
                && known.SourceUniqueId == item.SourceUniqueId);
            var stored = previous is null ? item : item with { TrashItemUniqueId = previous.TrashItemUniqueId };
            _items[stored.TrashItemUniqueId] = (stored, payload.ToArray());
            return Task.FromResult(stored);
        }
    }

    public Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(Find(space, trashItemUniqueId)?.Item);
    }

    public Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var found = Find(space, trashItemUniqueId);
            return Task.FromResult(found is null ? null : new TrashedObject(found.Value.Item, found.Value.Payload));
        }
    }

    public Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<TrashItem> listed = _items.Values.Select(entry => entry.Item)
                .Where(item => item.Space == space && item.Status == TrashItemStatus.Trashed
                    && (sourceKind is null || string.Equals(item.SourceKind, sourceKind, StringComparison.Ordinal)))
                .OrderByDescending(item => item.TrashedAt).ThenByDescending(item => item.TrashItemUniqueId)
                .Take(limit)
                .ToList();
            return Task.FromResult(listed);
        }
    }

    public Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (Find(space, trashItemUniqueId) is not { Item.Status: TrashItemStatus.Trashed } found)
                return Task.FromResult(false);
            _items[trashItemUniqueId] = (found.Item with { Status = TrashItemStatus.Restored, RestoredAt = restoredAt, RestoredBy = actor }, found.Payload);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            return Task.FromResult(Find(space, trashItemUniqueId) is not null && _items.Remove(trashItemUniqueId));
    }

    public Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var expired = _items.Values.Select(entry => entry.Item).Where(item => item.Space == space && item.IsExpiredAt(asOf)).ToList();
            foreach (var item in expired)
                _items.Remove(item.TrashItemUniqueId);
            return Task.FromResult(expired.Count);
        }
    }

    public Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, Actor actor, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var batch = _items.Values.Select(entry => entry.Item).Where(item => item.Space.Compartment == compartment).Take(limit).ToList();
            foreach (var item in batch)
                _items.Remove(item.TrashItemUniqueId);
            return Task.FromResult(batch.Count);
        }
    }

    private (TrashItem Item, byte[] Payload)? Find(Space space, Guid trashItemUniqueId)
        => _items.TryGetValue(trashItemUniqueId, out var entry) && entry.Item.Space == Guard.NotNull(space) ? entry : null;
}
