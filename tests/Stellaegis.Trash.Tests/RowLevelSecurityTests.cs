using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Trash.PostgreSQL;
using static Stellaegis.Trash.Tests.Fixtures;

namespace Stellaegis.Trash.Tests;

/// <summary>
/// What the database holds by itself, under the ordinary role: a session sees only its space, a session without a
/// space sees nothing, and the installation matches the frame.
/// </summary>
public sealed class RowLevelSecurityTests(RowLevelSecurityFixture database) : IClassFixture<RowLevelSecurityFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> TrashAsync(Compartment compartment)
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        await using var harness = TrashHarness.PostgreSql(database.AppConnectionString);
        return (await harness.Trash.TrashAsync(compartment, Request(), Alice, Ct)).TrashItemUniqueId;
    }

    private async Task<long> CountAsync(string? space)
    {
        await using var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(Ct);
        if (space is not null)
        {
            await using var set = new NpgsqlCommand("select set_config('stellaegis.space', @space, false)", connection);
            set.Parameters.AddWithValue("space", space);
            await set.ExecuteScalarAsync(Ct);
        }
        await using var count = new NpgsqlCommand("select count(*) from trash.items", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_query_that_forgets_its_filter_sees_only_its_space()
    {
        var acme = NewCompartment();
        var globex = NewCompartment();
        await TrashAsync(acme);
        await TrashAsync(acme);
        await TrashAsync(globex);

        Assert.Equal(2, await CountAsync(acme.Value));
        Assert.Equal(1, await CountAsync(globex.Value));
        Assert.Equal(0, await CountAsync(space: null));
    }

    [Fact]
    public async Task The_installation_is_verified_against_the_frame()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);

        await DatabaseLifecycle.VerifyAsync([TrashDatabase.Declaration], database.Frame, (_, ct) => OpenAsync(ct), Ct);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseLifecycle.VerifyAsync([TrashDatabase.Declaration], database.Frame.WithOwnerIsolation(), (_, ct) => OpenAsync(ct), Ct));
        Assert.Contains("pl_items_owner on trash.items is missing", exception.Message);
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>Under a frame declaring the isolation by owner: each actor sees, restores and deletes what it trashed.</summary>
public sealed class OwnerIsolationTests(OwnerIsolationFixture database) : IClassFixture<OwnerIsolationFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_actor_sees_only_the_items_it_trashed()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);
        await using var harness = TrashHarness.PostgreSql(database.AppConnectionString);
        var acme = NewCompartment();
        var ofAlice = await harness.Trash.TrashAsync(acme, Request(), Alice, Ct);
        var ofBob = await harness.Trash.TrashAsync(acme, Request(), Bob, Ct);

        Assert.Equal([ofAlice.TrashItemUniqueId], (await harness.Trash.ListAsync(acme, Alice, cancellationToken: Ct)).Select(i => i.TrashItemUniqueId));
        Assert.Null(await harness.Trash.GetAsync(acme, ofBob.TrashItemUniqueId, Alice, Ct));
        await Assert.ThrowsAsync<TrashItemNotFoundException>(() => harness.Trash.RestoreAsync(acme, ofBob.TrashItemUniqueId, Alice, Ct));
        await Assert.ThrowsAsync<TrashItemNotFoundException>(() => harness.Trash.DeleteAsync(acme, ofAlice.TrashItemUniqueId, Bob, Ct));
        Assert.NotNull(await harness.Trash.RestoreAsync(acme, ofBob.TrashItemUniqueId, Bob, Ct));
    }

    [Fact]
    public async Task The_rule_is_in_the_database_and_the_verification_sees_it()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? string.Empty);

        await DatabaseLifecycle.VerifyAsync([TrashDatabase.Declaration], database.Frame, (_, ct) => OpenAsync(ct), Ct);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseLifecycle.VerifyAsync([TrashDatabase.Declaration], ApplicationFrame.ManyCompartments().WithSharedSpace(), (_, ct) => OpenAsync(ct), Ct));
        Assert.Contains("pl_items_owner on trash.items is present", exception.Message);
    }

    private async Task<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(database.AppConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
