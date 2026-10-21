using System.Data.Common;
using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Trash.PostgreSQL;

namespace Stellaegis.Trash.Tests;

/// <summary>A database for the store tests, installed without the isolation by owner.</summary>
public sealed class PostgreSqlFixture() : PostgreSqlDatabase(databaseSuffix: null, ownerIsolation: false);

/// <summary>A database for the tests of row-level security, the name of the first suffixed with <c>_rls</c>.</summary>
public sealed class RowLevelSecurityFixture() : PostgreSqlDatabase(databaseSuffix: "_rls", ownerIsolation: false);

/// <summary>A database installed under a frame declaring the isolation by owner, suffixed with <c>_owner</c>.</summary>
public sealed class OwnerIsolationFixture() : PostgreSqlDatabase(databaseSuffix: "_owner", ownerIsolation: true);

/// <summary>
/// A test database, its <c>trash</c> schema dropped and installed again at every run by the life cycle of
/// <c>Stellaegis.Primitives.Data</c> under the configured role, and the ordinary role the tests connect under,
/// which row-level security applies to.
/// </summary>
/// <remarks>
/// The connection string comes from <c>STELLAEGIS_TRASH_TEST_POSTGRES</c>, defaulting to a local server and the
/// database <c>stellaegis_trash_tests</c>, created if missing. When no server answers, <see cref="Unavailable"/> says
/// why, and the tests that need it are skipped rather than failed — and fail the publication, which refuses a
/// skipped test. xunit builds one fixture per test class and runs the classes in parallel: each class has a
/// database of its own.
/// </remarks>
public abstract class PostgreSqlDatabase : IAsyncLifetime
{
    private const string Variable = "STELLAEGIS_TRASH_TEST_POSTGRES";
    private const string Default = "Host=127.0.0.1;Username=postgres;Database=stellaegis_trash_tests";
    private const string AppRole = "stellaegis_trash_app";

    private readonly bool _ownerIsolation;

    private protected PostgreSqlDatabase(string? databaseSuffix, bool ownerIsolation)
    {
        var configured = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable) ?? Default);
        configured.Database += databaseSuffix;
        ConnectionString = configured.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = AppRole, Password = AppRole }.ConnectionString;
        _ownerIsolation = ownerIsolation;
        Frame = ownerIsolation
            ? ApplicationFrame.ManyCompartments().WithSharedSpace().WithOwnerIsolation()
            : ApplicationFrame.ManyCompartments().WithSharedSpace();
    }

    /// <summary>The connection string under the configured role, which installs the schema and owns it.</summary>
    public string ConnectionString { get; }

    /// <summary>The connection string under the ordinary role of the application, which row-level security applies to.</summary>
    public string AppConnectionString { get; }

    /// <summary>The frame the database was installed under.</summary>
    public ApplicationFrame Frame { get; }

    /// <summary>Why the database cannot be used, or <c>null</c> when it can.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var server = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
        try
        {
            await using (var connection = new NpgsqlConnection(server.ConnectionString))
            {
                await connection.OpenAsync();
                await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @name", connection);
                exists.Parameters.AddWithValue("name", target.Database!);
                if (await exists.ExecuteScalarAsync() is null)
                {
                    await using var create = new NpgsqlCommand($"create database \"{target.Database}\"", connection);
                    await create.ExecuteNonQueryAsync();
                }

                // Two fixtures may create it at once: the second finds it there.
                await using var role = new NpgsqlCommand(
                    $"""
                    do $$ begin
                      create role {AppRole} login password '{AppRole}';
                    exception when duplicate_object or unique_violation then null;
                    end $$;
                    """, connection);
                await role.ExecuteNonQueryAsync();
            }

            await using (var connection = new NpgsqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var reset = new NpgsqlCommand("drop schema if exists trash cascade", connection);
                await reset.ExecuteNonQueryAsync();
            }

            await DatabaseLifecycle.InstallAsync([TrashDatabase.Declaration], Frame, (_, ct) => OpenAsync(ConnectionString, ct), AppRole);
            await DatabaseLifecycle.VerifyAsync([TrashDatabase.Declaration], Frame, (_, ct) => OpenAsync(AppConnectionString, ct));
        }
        catch (NpgsqlException e)
        {
            Unavailable = $"No PostgreSQL for the store tests ({Variable} or '{Default}'): {e.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<DbConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
