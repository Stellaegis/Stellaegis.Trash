using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>
/// The <see cref="ITrashService"/>: checks what the application gives, sets the deadline, and keeps the rules of
/// a restoration — once, before the deadline — above whatever store holds the items.
/// </summary>
internal sealed class TrashService(ITrashStore store, IOptions<TrashOptions> options, ILogger<TrashService> logger, TimeProvider? clock = null) : ITrashService
{
    private const int PurgeBatchSize = 100;
    private const int MaxDisplayNameLength = 256;

    private readonly ITrashStore _store = Guard.NotNull(store);
    private readonly TrashOptions _options = Guard.NotNull(options).Value;
    private readonly ILogger<TrashService> _logger = Guard.NotNull(logger);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TrashItem> TrashAsync(Space space, TrashRequest request, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotNull(actor);
        Check(request);

        var now = _clock.GetUtcNow();
        var item = new TrashItem
        {
            TrashItemUniqueId = Guid.NewGuid(),
            Space = space,
            SourceKind = request.SourceKind,
            SourceUniqueId = request.SourceUniqueId,
            DisplayName = request.DisplayName,
            ContentType = request.ContentType,
            PayloadLength = request.Payload.Length,
            Status = TrashItemStatus.Trashed,
            TrashedAt = now,
            TrashedBy = actor,
            PurgeAt = request.PurgeAt ?? (_options.DefaultRetention is { } retention ? now + retention : null),
        };
        if (item.PurgeAt == DateTimeOffset.MaxValue)
            item = item with { PurgeAt = null };

        return await _store.UpsertAsync(item, request.Payload, actor, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TrashItem>> ListAsync(Space space, Actor actor, string? sourceKind = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotNull(actor);
        if (sourceKind is not null && !NameRules.IsIdentifier(sourceKind))
            throw new TrashRequestException(TrashErrors.InvalidRequest, $"'{sourceKind}' is not a kind of object: {NameRules.IdentifierRule}.");
        return _store.ListAsync(space, sourceKind, _options.ListLimit, actor, cancellationToken);
    }

    public Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
        => _store.GetAsync(Guard.NotNull(space), trashItemUniqueId, Guard.NotNull(actor), cancellationToken);

    public Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
        => _store.OpenAsync(Guard.NotNull(space), trashItemUniqueId, Guard.NotNull(actor), cancellationToken);

    public async Task<TrashedObject> RestoreAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotNull(actor);

        var trashed = await _store.OpenAsync(space, trashItemUniqueId, actor, cancellationToken).ConfigureAwait(false)
            ?? throw new TrashItemNotFoundException(trashItemUniqueId);
        if (trashed.Item.Status == TrashItemStatus.Restored)
            throw new TrashItemRestoredException(trashItemUniqueId);
        var now = _clock.GetUtcNow();
        if (trashed.Item.IsExpiredAt(now))
            throw new TrashItemExpiredException(trashItemUniqueId, trashed.Item.PurgeAt!.Value);

        // Marked after being read: a second restoration meanwhile finds the mark and is refused by the store.
        if (!await _store.MarkRestoredAsync(space, trashItemUniqueId, actor, now, cancellationToken).ConfigureAwait(false))
            throw new TrashItemRestoredException(trashItemUniqueId);

        _logger.LogInformation("Trash item {TrashItemUniqueId} ({SourceKind} {SourceUniqueId}) restored by {Actor} in {Space}.",
            trashItemUniqueId, trashed.Item.SourceKind, trashed.Item.SourceUniqueId, actor, space);
        return trashed with { Item = trashed.Item with { Status = TrashItemStatus.Restored, RestoredAt = now, RestoredBy = actor } };
    }

    public async Task DeleteAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotNull(actor);
        if (!await _store.DeleteAsync(space, trashItemUniqueId, actor, cancellationToken).ConfigureAwait(false))
            throw new TrashItemNotFoundException(trashItemUniqueId);
    }

    public async Task<int> PurgeExpiredAsync(Space space, Actor actor, DateTimeOffset? asOf = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(space);
        Guard.NotNull(actor);
        var deleted = await _store.DeleteExpiredAsync(space, asOf ?? _clock.GetUtcNow(), actor, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("{Count} expired trash item(s) purged by {Actor} in {Space}.", deleted, actor, space);
        return deleted;
    }

    public async Task<int> PurgeCompartmentAsync(Compartment compartment, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(compartment);
        Guard.NotNull(actor);

        _logger.LogInformation("Purge of the trash of compartment {Compartment} started by {Actor}.", compartment, actor);
        var total = 0;
        int batch;
        do
        {
            batch = await _store.DeleteCompartmentBatchAsync(compartment, PurgeBatchSize, actor, cancellationToken).ConfigureAwait(false);
            total += batch;
        }
        while (batch == PurgeBatchSize);

        _logger.LogInformation("Purge of the trash of compartment {Compartment} by {Actor} completed: {Count} item(s) deleted.", compartment, actor, total);
        return total;
    }

    private void Check(TrashRequest request)
    {
        Guard.NotNull(request);
        if (!NameRules.IsIdentifier(request.SourceKind))
            throw new TrashRequestException(TrashErrors.InvalidRequest, $"'{request.SourceKind}' is not a kind of object: {NameRules.IdentifierRule}.");
        if (request.SourceUniqueId == Guid.Empty)
            throw new TrashRequestException(TrashErrors.InvalidRequest, "The source identifier of the object is required.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > MaxDisplayNameLength)
            throw new TrashRequestException(TrashErrors.InvalidRequest, $"A display name of 1 to {MaxDisplayNameLength} characters is required.");
        if (string.IsNullOrWhiteSpace(request.ContentType))
            throw new TrashRequestException(TrashErrors.InvalidRequest, "A content type is required.");
        if (request.Payload.IsEmpty)
            throw new TrashRequestException(TrashErrors.InvalidRequest, "A payload is required.");
        if (request.Payload.Length > _options.MaxPayloadLength)
            throw new TrashRequestException(TrashErrors.PayloadTooLarge, $"The payload is longer than {_options.MaxPayloadLength} bytes.");
    }
}
