using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Trash.PostgreSQL;

/// <summary>Registers the PostgreSQL store with the trash.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL store, its options bound from <paramref name="sectionName"/> and validated when the host
    /// starts, and the trash with its own options; declares its database (<see cref="TrashDatabase.Declaration"/>),
    /// which an installer sets up under an administration role and the host verifies at startup under the
    /// application's — <c>AddStellaegisDatabaseVerification()</c> on a generic host, <c>VerifyStellaegisDatabasesAsync()</c>
    /// before running an ASP.NET Core one —; and declares that the store holds an isolation by owner
    /// (<see cref="ModuleCapability.OwnerIsolation"/>), the installation applying its rule when the frame declares it.
    /// </summary>
    public static IServiceCollection AddStellaegisTrashPostgreSQL(
        this IServiceCollection services, IConfiguration configuration, string sectionName = TrashDatabaseOptions.SectionName)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);
        services.AddStellaegisOptions<TrashDatabaseOptions>(configuration, sectionName);
        services.AddStore();
        return services.AddStellaegisTrashServices(configuration);
    }

    /// <summary>Registers the PostgreSQL store and the trash, their options configured in code.</summary>
    public static IServiceCollection AddStellaegisTrashPostgreSQL(
        this IServiceCollection services, Action<TrashDatabaseOptions> configureDatabase, Action<TrashOptions>? configureTrash = null)
    {
        Guard.NotNull(services);
        services.AddStellaegisOptions(Guard.NotNull(configureDatabase));
        services.AddStore();
        return services.AddStellaegisTrashServices(configureTrash ?? (_ => { }));
    }

    private static void AddStore(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgreSqlTrashStore>();
        services.TryAddSingleton<ITrashStore>(provider => provider.GetRequiredService<PostgreSqlTrashStore>());
        services.AddStellaegisModuleCapability<TrashModule>(ModuleCapability.OwnerIsolation);
        services.AddStellaegisModuleDatabase(TrashDatabase.Declaration,
            (provider, ct) => provider.GetRequiredService<PostgreSqlTrashStore>().OpenConnectionAsync(ct));
    }
}
