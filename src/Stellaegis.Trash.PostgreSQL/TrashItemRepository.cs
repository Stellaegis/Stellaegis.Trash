using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stellaegis.Primitives;

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

internal sealed class TrashItemRepository(NpgsqlConnection connection, Func<NpgsqlTransaction?> transaction) : ITrashItemRepository
{
    private const string Columns =
        "trash_item_unique_id, compartment, source_kind, source_unique_id, display_name, content_type, payload_length, "
        + "status, trashed_at, trashed_by, purge_at, restored_at, restored_by";

    // pgp_sym_decrypt_bytea receives only the passphrase of the row's own slot: a retired slot without a passphrase
    // never reaches it unless a row still uses that slot.
    private const string DecryptedPayload = "pgp_sym_decrypt_bytea(payload, case key_slot when 1 then @passphrase1 else @passphrase2 end) as payload";

    public async Task<Guid> UpsertAsync(TrashItem item, ReadOnlyMemory<byte> payload, int keySlot, string passphrase, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
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
            connection, transaction());
        command.Parameters.Add("trashItemUniqueId", NpgsqlDbType.Uuid).Value = item.TrashItemUniqueId;
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = (object?)item.Space.Compartment?.Value ?? DBNull.Value;
        command.Parameters.Add("sourceKind", NpgsqlDbType.Text).Value = item.SourceKind;
        command.Parameters.Add("sourceUniqueId", NpgsqlDbType.Uuid).Value = item.SourceUniqueId;
        command.Parameters.Add("displayName", NpgsqlDbType.Text).Value = item.DisplayName;
        command.Parameters.Add("contentType", NpgsqlDbType.Text).Value = item.ContentType;
        command.Parameters.Add("payload", NpgsqlDbType.Bytea).Value = payload;
        command.Parameters.Add("passphrase", NpgsqlDbType.Text).Value = passphrase;
        command.Parameters.Add("payloadLength", NpgsqlDbType.Bigint).Value = item.PayloadLength;
        command.Parameters.Add("keySlot", NpgsqlDbType.Smallint).Value = (short)keySlot;
        command.Parameters.Add("trashedAt", NpgsqlDbType.TimestampTz).Value = item.TrashedAt;
        command.Parameters.Add("trashedBy", NpgsqlDbType.Text).Value = item.TrashedBy.Value;
        command.Parameters.Add("purgeAt", NpgsqlDbType.TimestampTz).Value = (object?)item.PurgeAt ?? DBNull.Value;
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<TrashItem?> GetAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {Columns} from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("trashItemUniqueId", NpgsqlDbType.Uuid).Value = trashItemUniqueId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<TrashedObject?> OpenAsync(Space space, Guid trashItemUniqueId, string passphrase1, string passphrase2, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select {Columns}, {DecryptedPayload} from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("trashItemUniqueId", NpgsqlDbType.Uuid).Value = trashItemUniqueId;
        command.Parameters.Add("passphrase1", NpgsqlDbType.Text).Value = passphrase1;
        command.Parameters.Add("passphrase2", NpgsqlDbType.Text).Value = passphrase2;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TrashedObject(Read(reader), reader.GetFieldValue<byte[]>("payload"))
            : null;
    }

    public async Task<IReadOnlyList<TrashItem>> ListAsync(Space space, string? sourceKind, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            select {Columns} from trash.items
            where {InSpace(space)} and status = 'trashed' and (@sourceKind::text is null or source_kind = @sourceKind)
            order by trashed_at desc, trash_item_unique_id desc
            limit @limit
            """,
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("sourceKind", NpgsqlDbType.Text).Value = (object?)sourceKind ?? DBNull.Value;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<TrashItem>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(Read(reader));
        return items;
    }

    public async Task<bool> MarkRestoredAsync(Space space, Guid trashItemUniqueId, Actor actor, DateTimeOffset restoredAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            update trash.items set status = 'restored', restored_at = @restoredAt, restored_by = @restoredBy
            where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId and status = 'trashed'
            """,
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("trashItemUniqueId", NpgsqlDbType.Uuid).Value = trashItemUniqueId;
        command.Parameters.Add("restoredAt", NpgsqlDbType.TimestampTz).Value = restoredAt;
        command.Parameters.Add("restoredBy", NpgsqlDbType.Text).Value = actor.Value;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> DeleteAsync(Space space, Guid trashItemUniqueId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"delete from trash.items where {InSpace(space)} and trash_item_unique_id = @trashItemUniqueId",
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("trashItemUniqueId", NpgsqlDbType.Uuid).Value = trashItemUniqueId;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<int> DeleteExpiredAsync(Space space, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"delete from trash.items where {InSpace(space)} and purge_at is not null and purge_at <= @asOf",
            connection, transaction());
        AddSpace(command, space);
        command.Parameters.Add("asOf", NpgsqlDbType.TimestampTz).Value = asOf;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteCompartmentBatchAsync(Compartment compartment, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            delete from trash.items
            where trash_item_unique_id in (select trash_item_unique_id from trash.items where compartment = @compartment limit @limit)
            """,
            connection, transaction());
        command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment.Value;
        command.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // The query filters on the space itself: the policy verifies, the filter guides the index.
    private static string InSpace(Space space) => space.IsShared ? "compartment is null" : "compartment = @compartment";

    // The parameter InSpace names, which the filter of the shared space does not have.
    private static void AddSpace(NpgsqlCommand command, Space space)
    {
        if (space.Compartment is { } compartment)
            command.Parameters.Add("compartment", NpgsqlDbType.Text).Value = compartment.Value;
    }

    private static TrashItem Read(NpgsqlDataReader row) => new()
    {
        TrashItemUniqueId = row.GetGuid("trash_item_unique_id"),
        Space = row.IsDBNull("compartment") ? Space.Shared : Space.Of(Compartment.Parse(row.GetString("compartment"))),
        SourceKind = row.GetString("source_kind"),
        SourceUniqueId = row.GetGuid("source_unique_id"),
        DisplayName = row.GetString("display_name"),
        ContentType = row.GetString("content_type"),
        PayloadLength = row.GetInt64("payload_length"),
        Status = row.GetString("status") == "restored" ? TrashItemStatus.Restored : TrashItemStatus.Trashed,
        TrashedAt = row.GetFieldValue<DateTimeOffset>("trashed_at"),
        TrashedBy = Actor.Parse(row.GetString("trashed_by")),
        PurgeAt = row.IsDBNull("purge_at") ? null : row.GetFieldValue<DateTimeOffset>("purge_at"),
        RestoredAt = row.IsDBNull("restored_at") ? null : row.GetFieldValue<DateTimeOffset>("restored_at"),
        RestoredBy = row.IsDBNull("restored_by") ? null : Actor.Parse(row.GetString("restored_by")),
    };
}
