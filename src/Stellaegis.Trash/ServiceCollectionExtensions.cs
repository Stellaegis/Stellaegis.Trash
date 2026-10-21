using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;
using Stellaegis.Trash.InMemory;

namespace Stellaegis.Trash;

/// <summary>Registers the trash, its module and its options; a store comes from this package or from an adapter.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITrashService"/> with its options, bound from <paramref name="sectionName"/> and validated
    /// when the host starts, and declares <see cref="TrashModule"/> to the application: a module with compartments,
    /// serving the shared space, with its operations. An <see cref="ITrashStore"/> must be registered too — by
    /// <see cref="AddStellaegisTrashInMemory(IServiceCollection, IConfiguration, string)"/> or by the PostgreSQL adapter,
    /// which call this method themselves: a host calls one of them, once.
    /// </summary>
    public static IServiceCollection AddStellaegisTrashServices(
        this IServiceCollection services, IConfiguration configuration, string sectionName = TrashOptions.SectionName)
    {
        Guard.NotNull(services);
        services.AddStellaegisOptions<TrashOptions>(Guard.NotNull(configuration), sectionName);
        return services.AddTrashCore();
    }

    /// <summary>Registers <see cref="ITrashService"/> with its options configured in code, validated when the host starts.</summary>
    public static IServiceCollection AddStellaegisTrashServices(this IServiceCollection services, Action<TrashOptions> configure)
    {
        Guard.NotNull(services);
        services.AddStellaegisOptions(Guard.NotNull(configure));
        return services.AddTrashCore();
    }

    /// <summary>Registers the store in memory with the service. For development and tests: nothing survives the process.</summary>
    public static IServiceCollection AddStellaegisTrashInMemory(
        this IServiceCollection services, IConfiguration configuration, string sectionName = TrashOptions.SectionName)
    {
        Guard.NotNull(services).TryAddSingleton<ITrashStore, InMemoryTrashStore>();
        return services.AddStellaegisTrashServices(configuration, sectionName);
    }

    /// <summary>Registers the store in memory with the service, its options configured in code.</summary>
    public static IServiceCollection AddStellaegisTrashInMemory(this IServiceCollection services, Action<TrashOptions> configure)
    {
        Guard.NotNull(services).TryAddSingleton<ITrashStore, InMemoryTrashStore>();
        return services.AddStellaegisTrashServices(configure);
    }

    private static IServiceCollection AddTrashCore(this IServiceCollection services)
    {
        services.AddStellaegisModule(ModuleDeclaration.Compartmented<TrashModule>()
            .AcceptingSharedSpace()
            .WithOperations(TrashOperations.All));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ITrashService, TrashService>();
        return services;
    }
}
