using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Stellaegis.Primitives;
using Stellaegis.Trash.PostgreSQL;

namespace Stellaegis.Trash.Tests;

/// <summary>Actors, compartments and payloads shared by the tests.</summary>
internal static class Fixtures
{
    public static Actor Alice { get; } = Actor.Parse("alice");

    public static Actor Bob { get; } = Actor.Parse("bob");

    public static Actor Purger { get; } = Actor.Job("trash_purge");

    /// <summary>A compartment no other test uses: the PostgreSQL tests of a class share one database.</summary>
    public static Compartment NewCompartment() => Compartment.Parse("t" + Guid.NewGuid().ToString("N"));

    public static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    public static string Text(ReadOnlyMemory<byte> payload) => Encoding.UTF8.GetString(payload.Span);

    public static TrashRequest Request(string json = """{"number":"F-2026-001"}""", Guid? sourceUniqueId = null, string sourceKind = "invoices", string displayName = "Invoice F-2026-001", DateTimeOffset? purgeAt = null)
        => new()
        {
            SourceKind = sourceKind,
            SourceUniqueId = sourceUniqueId ?? Guid.NewGuid(),
            DisplayName = displayName,
            Payload = Json(json),
            PurgeAt = purgeAt,
        };
}

/// <summary>
/// A whole trash as a host would register it, on the in-memory or the PostgreSQL store, with a clock the tests
/// move.
/// </summary>
internal sealed class TrashHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private TrashHarness(Action<IServiceCollection, IConfiguration> addStore, IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        addStore(services, configuration);
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateAsyncScope();
        Trash = _scope.ServiceProvider.GetRequiredService<ITrashService>();
    }

    public ITrashService Trash { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));

    public static TrashHarness InMemory(int? retentionDays = 30, int listLimit = 500, long maxPayloadLength = 16 * 1024 * 1024) => new(
        (services, configuration) => services.AddStellaegisTrashInMemory(configuration),
        new Dictionary<string, string?>
        {
            ["Stellaegis:Trash:DefaultRetention"] = retentionDays is { } days ? TimeSpan.FromDays(days).ToString() : string.Empty,
            ["Stellaegis:Trash:ListLimit"] = listLimit.ToString(),
            ["Stellaegis:Trash:MaxPayloadLength"] = maxPayloadLength.ToString(),
        });

    public static TrashHarness PostgreSql(string connectionString, int activeSlot = 1, string? secondPassphrase = null) => new(
        (services, configuration) => services.AddStellaegisTrashPostgreSQL(configuration),
        new Dictionary<string, string?>
        {
            ["Stellaegis:Trash:PostgreSQL:ConnectionString"] = connectionString,
            ["Stellaegis:Trash:PostgreSQL:Passphrases:ActiveKeySlot"] = activeSlot.ToString(),
            ["Stellaegis:Trash:PostgreSQL:Passphrases:Keys:1"] = "first-test-passphrase",
            ["Stellaegis:Trash:PostgreSQL:Passphrases:Keys:2"] = secondPassphrase ?? (activeSlot == 2 ? "second-test-passphrase" : string.Empty),
        });

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
