using System.Data.Common;
using Dapper;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Trash.PostgreSQL;

/// <summary>
/// The repository of <c>trash.items</c>, built by the unit of work on its connection and transaction: every query
/// filters on the space it is asked for, which the row-level security of the schema repeats from the session.
/// </summary>
internal interface ITrashItemRepository
{
    Task<Guid> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, int keySlot, string passphrase, CancellationToken cancellationToken);

    Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken);

    Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, string passphrase1, string passphrase2, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, CancellationToken cancellationToken);

    Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, CancellationToken cancellationToken);

    Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, CancellationToken cancellationToken);
}

internal sealed class TrashItemRepository(DbConnection connection, Func<DbTransaction?> transaction) : ITrashItemRepository
{
    private const string Columns =
        "trash_item_unique_id, compartment, source_kind, source_unique_id, display_name, content_type, payload_length, "
        + "status, trashed_at, trashed_by, purge_at, restored_at, restored_by";

    // pgp_sym_decrypt_bytea receives only the passphrase of the row's own slot: a retired slot without a passphrase
    // never reaches it unless a row still uses that slot.
    private const string DecryptedPayload = "pgp_sym_decrypt_bytea(payload, case key_slot when 1 then @passphrase1 else @passphrase2 end) as payload";

    static TrashItemRepository()
    {
        SnakeCaseTypeMap<ItemRow>.Register();
        SnakeCaseTypeMap<OpenedRow>.Register();
    }

    public async Task<Guid> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, int keySlot, string passphrase, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<Guid>(Command(
            """
            insert into trash.items
              (trash_item_unique_id, compartment, source_kind, source_unique_id, display_name, content_type,
               payload, payload_length, key_slot, status, trashed_at, trashed_by, purge_at)
            values
              (@trashItemUniqueId, @compartment, @sourceKind, @sourceUniqueId, @displayName, @contentType,
               pgp_sym_encrypt_bytea(@payload, @passphrase), @payloadLength, @keySlot, 'trashed', @trashedAt, @trashedBy, @purgeAt)
            on conflict (coalesce(compartment, '_shared'), source_kind, source_unique_id) do update set
              display_name = excluded.display_name,
              content_type = excluded.content_type,
              payload = excluded.payload,
              payload_length = excluded.payload_length,
              key_slot = excluded.key_slot,
              status = 'trashed',
              trashed_at = excluded.trashed_at,
              trashed_by = excluded.trashed_by,
              purge_at = excluded.purge_at,
              restored_at = null,
              restored_by = null
            returning trash_item_unique_id
            """,
            new
            {
                trashItemUniqueId = item.TrashItemUniqueId,
                compartment = item.Space.Compartment?.Value,
                sourceKind = item.SourceKind,
                sourceUniqueId = item.SourceUniqueId,
                displayName = item.DisplayName,
                contentType = item.ContentType,
                payload = payload.ToArray(),
                passphrase,
                payloadLength = item.PayloadLength,
                keySlot = (short)keySlot,
                trashedAt = item.TrashedAt,
                trashedBy = item.TrashedBy.Value,
                purgeAt = item.PurgeAt,
            }, cancellationToken)).ConfigureAwait(false);

    public async Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ItemRow>(Command(
            $"select {Columns} from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            new { compartment = space.Compartment?.Value, trashItemUniqueId }, cancellationToken)).ConfigureAwait(false);
        return row?.ToItem();
    }

    public async Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, string passphrase1, string passphrase2, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<OpenedRow>(Command(
            $"select {Columns}, {DecryptedPayload} from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            new { compartment = space.Compartment?.Value, trashItemUniqueId, passphrase1, passphrase2 }, cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new TrashedObject(row.ToItem(), row.Payload);
    }

    public async Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ItemRow>(Command(
            $"""
            select {Columns} from trash.items
            where {InSpace(space)} and status = 'trashed' and (@sourceKind::text is null or source_kind = @sourceKind)
            order by trashed_at desc, trash_item_unique_id desc
            limit @limit
            """,
            new { compartment = space.Compartment?.Value, sourceKind, limit }, cancellationToken)).ConfigureAwait(false);
        return rows.Select(row => row.ToItem()).ToList();
    }

    public async Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken)
        => await connection.ExecuteAsync(Command(
            $"""
            update trash.items set status = 'restored', restored_at = @restoredAt, restored_by = @restoredBy
            where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId and status = 'trashed'
            """,
            new { compartment = space.Compartment?.Value, trashItemUniqueId, restoredAt, restoredBy = actor.Value }, cancellationToken)).ConfigureAwait(false) > 0;

    public async Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken)
        => await connection.ExecuteAsync(Command(
            $"delete from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            new { compartment = space.Compartment?.Value, trashItemUniqueId }, cancellationToken)).ConfigureAwait(false) > 0;

    public Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, CancellationToken cancellationToken)
        => connection.ExecuteAsync(Command(
            $"delete from trash.items where {InSpace(space)} and purge_at is not null and purge_at <= @asOf",
            new { compartment = space.Compartment?.Value, asOf }, cancellationToken));

    public Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, CancellationToken cancellationToken)
        => connection.ExecuteAsync(Command(
            """
            delete from trash.items
            where trash_item_unique_id in (select trash_item_unique_id from trash.items where compartment = @compartment limit @limit)
            """,
            new { compartment = compartment.Value, limit }, cancellationToken));

    // The query filters on the space itself: the policy verifies, the filter guides the index.
    private static string InSpace(Space space) => space.IsShared ? "compartment is null" : "compartment = @compartment";

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken)
        => new(sql, parameters, transaction(), cancellationToken: cancellationToken);

    private class ItemRow
    {
        public Guid TrashItemUniqueId { get; init; }
        public string? Compartment { get; init; }
        public string SourceKind { get; init; } = string.Empty;
        public Guid SourceUniqueId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long PayloadLength { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset TrashedAt { get; init; }
        public string TrashedBy { get; init; } = string.Empty;
        public DateTimeOffset? PurgeAt { get; init; }
        public DateTimeOffset? RestoredAt { get; init; }
        public string? RestoredBy { get; init; }

        public TrashItem ToItem() => new()
        {
            TrashItemUniqueId = TrashItemUniqueId,
            Space = Compartment is null ? Space.Shared : Space.Of(Primitives.Compartment.Parse(Compartment)),
            SourceKind = SourceKind,
            SourceUniqueId = SourceUniqueId,
            DisplayName = DisplayName,
            ContentType = ContentType,
            PayloadLength = PayloadLength,
            Status = Status == "restored" ? TrashItemStatus.Restored : TrashItemStatus.Trashed,
            TrashedAt = TrashedAt,
            TrashedBy = Actor.Parse(TrashedBy),
            PurgeAt = PurgeAt,
            RestoredAt = RestoredAt,
            RestoredBy = RestoredBy is null ? null : Actor.Parse(RestoredBy),
        };
    }

    private sealed class OpenedRow : ItemRow
    {
        public byte[] Payload { get; init; } = [];
    }
}
