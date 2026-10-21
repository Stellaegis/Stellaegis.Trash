using Npgsql;
using Stellaegis.Primitives;
using static Stellaegis.Trash.Tests.Fixtures;

namespace Stellaegis.Trash.Tests;

/// <summary>The trash on the PostgreSQL store, under the application's role: the same rules, and the encryption.</summary>
public sealed class PostgreSqlTrashStoreTests(PostgreSqlFixture database) : IClassFixture<PostgreSqlFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TrashHarness Harness(int activeSlot = 1, string? secondPassphrase = null)
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        return TrashHarness.PostgreSql(database.AppConnectionString, activeSlot, secondPassphrase);
    }

    [Fact]
    public async Task An_object_goes_in_comes_back_and_is_replaced_when_trashed_again()
    {
        await using var harness = Harness();
        var acme = NewCompartment();
        var source = Guid.NewGuid();

        var item = await harness.Trash.TrashAsync(acme, Request("""{"v":1}""", source), Alice, Ct);
        var opened = await harness.Trash.OpenAsync(acme, item.TrashItemUniqueId, Alice, Ct);
        var again = await harness.Trash.TrashAsync(acme, Request("""{"v":2}""", source), Bob, Ct);

        Assert.Equal("""{"v":1}""", Text(opened!.Payload));
        Assert.Equal((item.TrashItemUniqueId, Bob, TrashItemStatus.Trashed), (again.TrashItemUniqueId, again.TrashedBy, again.Status));
        Assert.Equal("""{"v":2}""", Text((await harness.Trash.OpenAsync(acme, item.TrashItemUniqueId, Alice, Ct))!.Payload));
        Assert.Equal(item.TrashItemUniqueId, Assert.Single(await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct)).TrashItemUniqueId);
    }

    [Fact]
    public async Task A_restoration_is_recorded_once_with_its_actor()
    {
        await using var harness = Harness();
        var acme = NewCompartment();
        var item = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);

        var restored = await harness.Trash.RestoreAsync(acme, item.TrashItemUniqueId, Bob, Ct);

        Assert.Equal((TrashItemStatus.Restored, Bob), (restored.Item.Status, restored.Item.RestoredBy));
        var stored = await harness.Trash.GetAsync(acme, item.TrashItemUniqueId, Alice, Ct);
        Assert.Equal((TrashItemStatus.Restored, Bob, item.SourceUniqueId), (stored!.Status, stored.RestoredBy, stored.SourceUniqueId));
        Assert.NotNull(stored.RestoredAt);
        await Assert.ThrowsAsync<TrashItemRestoredException>(() => harness.Trash.RestoreAsync(acme, item.TrashItemUniqueId, Bob, Ct));
    }

    [Fact]
    public async Task The_shared_space_holds_its_own_items()
    {
        await using var harness = Harness();
        var acme = NewCompartment();
        var kind = "k" + Guid.NewGuid().ToString("N")[..8];
        var shared = await harness.Trash.TrashAsync(Space.Shared, Request(sourceKind: kind, displayName: "Template"), Alice, Ct);
        var ofAcme = await harness.Trash.TrashAsync(acme, Request(sourceKind: kind), Alice, Ct);

        Assert.True(shared.Space.IsShared);
        Assert.Null(await harness.Trash.GetAsync(acme, shared.TrashItemUniqueId, Alice, Ct));
        Assert.Null(await harness.Trash.GetAsync(Space.Shared, ofAcme.TrashItemUniqueId, Alice, Ct));
        Assert.Contains(shared.TrashItemUniqueId, (await harness.Trash.ListAsync(Space.Shared, Alice, kind, Ct)).Select(i => i.TrashItemUniqueId));
        await harness.Trash.DeleteAsync(Space.Shared, shared.TrashItemUniqueId, Alice, Ct);
    }

    [Fact]
    public async Task The_expired_items_and_a_whole_compartment_are_purged()
    {
        await using var harness = Harness();
        var acme = NewCompartment();
        var expiring = await harness.Trash.TrashAsync(acme, Request(purgeAt: harness.Clock.GetUtcNow().AddDays(1)), Alice, Ct);
        var kept = await harness.Trash.TrashAsync(acme, Request(purgeAt: DateTimeOffset.MaxValue), Alice, Ct);
        harness.Clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(1, await harness.Trash.PurgeExpiredAsync(acme, Purger, cancellationToken: Ct));
        Assert.Null(await harness.Trash.GetAsync(acme, expiring.TrashItemUniqueId, Alice, Ct));
        Assert.NotNull(await harness.Trash.GetAsync(acme, kept.TrashItemUniqueId, Alice, Ct));

        for (var i = 0; i < 120; i++)
            await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        Assert.Equal(121, await harness.Trash.PurgeCompartmentAsync(acme, Purger, Ct));
        Assert.Equal(0, await harness.Trash.PurgeCompartmentAsync(acme, Purger, Ct));
    }

    [Fact]
    public async Task The_payload_is_encrypted_at_rest_and_follows_its_key_slot_through_a_rotation()
    {
        var acme = NewCompartment();
        Guid first;
        await using (var harness = Harness(activeSlot: 1))
            first = (await harness.Trash.TrashAsync(acme, Request("""{"secret":"first"}"""), Alice, Ct)).TrashItemUniqueId;

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var raw = new NpgsqlCommand("select payload, key_slot from trash.items where trash_item_unique_id = @id", connection);
        raw.Parameters.AddWithValue("id", first);
        await using (var reader = await raw.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.DoesNotContain("first", System.Text.Encoding.Latin1.GetString((byte[])reader[0]));
            Assert.Equal((short)1, reader.GetInt16(1));
        }

        // The rotation: slot 2 active, the first passphrase kept while a row uses it.
        await using (var rotated = TrashHarness.PostgreSql(database.AppConnectionString, activeSlot: 2))
        {
            var second = await rotated.Trash.TrashAsync(acme, Request("""{"secret":"second"}"""), Alice, Ct);
            Assert.Equal("""{"secret":"first"}""", Text((await rotated.Trash.OpenAsync(acme, first, Alice, Ct))!.Payload));
            Assert.Equal("""{"secret":"second"}""", Text((await rotated.Trash.OpenAsync(acme, second.TrashItemUniqueId, Alice, Ct))!.Payload));
        }
    }

    [Fact]
    public async Task A_wrong_passphrase_reads_nothing()
    {
        var acme = NewCompartment();
        Guid id;
        await using (var harness = Harness())
            id = (await harness.Trash.TrashAsync(acme, Request(), Alice, Ct)).TrashItemUniqueId;

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var wrong = new NpgsqlCommand("select pgp_sym_decrypt_bytea(payload, 'not-the-passphrase') from trash.items where trash_item_unique_id = @id", connection);
        wrong.Parameters.AddWithValue("id", id);
        await Assert.ThrowsAsync<PostgresException>(() => wrong.ExecuteScalarAsync(Ct));
    }
}
