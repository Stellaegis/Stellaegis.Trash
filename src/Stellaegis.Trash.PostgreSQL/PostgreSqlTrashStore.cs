using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Trash.PostgreSQL;

/// <summary>
/// The <see cref="ITrashStore"/> on PostgreSQL, over <c>trash.items</c>. Each call opens a unit of work of
/// <c>Stellaegis.Primitives.Data</c> in its space, for its actor: both are set in the session context of the
/// connection, where the row-level security of the schema reads them. Payloads are encrypted with pgcrypto under
/// the passphrases of <see cref="TrashDatabaseOptions"/>.
/// </summary>
internal sealed class PostgreSqlTrashStore : ITrashStore, IAsyncDisposable, IDisposable
{
    private static readonly RepositoryRegistry<TrashDatabase> Registry = new RepositoryRegistry<TrashDatabase>()
        .Add<ITrashItemRepository>((connection, transaction) => new TrashItemRepository(connection, transaction));

    private readonly NpgsqlDataSource _dataSource;
    private readonly AdoSpaceUnitOfWorkFactory<TrashDatabase> _units;
    private readonly KeySlots _passphrases;

    /// <summary>Creates the store on the options the host validated at startup.</summary>
    public PostgreSqlTrashStore(IOptions<TrashDatabaseOptions> options)
    {
        var value = Guard.NotNull(options).Value;
        if (value.FindError() is { } error)
            throw new InvalidOperationException($"{TrashDatabaseOptions.SectionName}: {error}");
        _passphrases = value.Passphrases;
        _dataSource = NpgsqlDataSource.Create(value.ConnectionString);
        _units = new AdoSpaceUnitOfWorkFactory<TrashDatabase>(_dataSource, Registry, SessionContext.PostgreSql);
    }

    /// <summary>Opens a connection under the application's role, for the verification of the database at startup.</summary>
    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    public async Task<TrashItem> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, Actor actor, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(item);
        await using var uow = await _units.OpenAndBeginAsync(item.Space, actor, cancellationToken).ConfigureAwait(false);
        var id = await uow.Repo<ITrashItemRepository>()
            .UpsertAsync(item, payload, _passphrases.ActiveKeySlot, _passphrases.ActiveKey, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id == item.TrashItemUniqueId ? item : item with { TrashItemUniqueId = id };
    }

    public async Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAsync(space, actor, cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ITrashItemRepository>().GetAsync(space, trashItemUniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAsync(space, actor, cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ITrashItemRepository>()
            .OpenAsync(space, trashItemUniqueId, _passphrases.KeyOf(1), _passphrases.KeyOf(2), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAsync(space, actor, cancellationToken).ConfigureAwait(false);
        return await uow.Repo<ITrashItemRepository>().ListAsync(space, sourceKind, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAndBeginAsync(space, actor, cancellationToken).ConfigureAwait(false);
        var marked = await uow.Repo<ITrashItemRepository>().MarkRestoredAsync(space, trashItemUniqueId, actor, restoredAt, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return marked;
    }

    public async Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAndBeginAsync(space, actor, cancellationToken).ConfigureAwait(false);
        var deleted = await uow.Repo<ITrashItemRepository>().DeleteAsync(space, trashItemUniqueId, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAndBeginAsync(space, actor, cancellationToken).ConfigureAwait(false);
        var deleted = await uow.Repo<ITrashItemRepository>().DeleteExpiredAsync(space, asOf, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, Actor actor, CancellationToken cancellationToken = default)
    {
        await using var uow = await _units.OpenAndBeginAsync(Space.Of(compartment), actor, cancellationToken).ConfigureAwait(false);
        var deleted = await uow.Repo<ITrashItemRepository>().DeleteCompartmentBatchAsync(compartment, limit, cancellationToken).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    public void Dispose() => _dataSource.Dispose();
}
