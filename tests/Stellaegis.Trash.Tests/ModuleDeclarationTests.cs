using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;
using Stellaegis.Trash.PostgreSQL;

namespace Stellaegis.Trash.Tests;

/// <summary>What the module and its stores declare to the application, read against its frame.</summary>
public sealed class ModuleDeclarationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().AddInMemoryCollection(
    [
        KeyValuePair.Create<string, string?>("Stellaegis:Trash:PostgreSQL:ConnectionString", "Host=localhost"),
        KeyValuePair.Create<string, string?>("Stellaegis:Trash:PostgreSQL:Passphrases:Keys:1", "x"),
    ]).Build();

    private static ServiceProvider Build(ApplicationFrame frame, Action<IServiceCollection> addStore)
    {
        var services = new ServiceCollection().AddLogging().AddStellaegisFrame(frame);
        addStore(services);
        return services.BuildServiceProvider();
    }

    private static void InMemory(IServiceCollection services) => services.AddStellaegisTrashInMemory(Configuration);

    private static void PostgreSql(IServiceCollection services) => services.AddStellaegisTrashPostgreSQL(Configuration);

    [Fact]
    public void The_module_is_declared_once_with_compartments_the_shared_space_and_its_operations()
    {
        foreach (var addStore in new Action<IServiceCollection>[] { InMemory, PostgreSql })
        {
            using var provider = Build(ApplicationFrame.ManyCompartments().WithSharedSpace(), addStore);

            var module = Assert.Single(provider.GetStellaegisModules());
            Assert.Equal((TrashModule.Name, ModuleCompartments.Required, true), (module.Name, module.Compartments, module.AcceptsSharedSpace));
            Assert.Equal(TrashOperations.All, module.Operations);
            provider.ValidateStellaegisModules();
        }
    }

    [Fact]
    public void An_application_without_compartments_cannot_use_the_module()
    {
        using var provider = Build(ApplicationFrame.WithoutCompartments, InMemory);

        Assert.Throws<InvalidOperationException>(provider.ValidateStellaegisModules);
    }

    [Fact]
    public void An_isolation_by_owner_is_held_by_the_postgresql_store_only()
    {
        var frame = ApplicationFrame.ManyCompartments().WithOwnerIsolation();

        using (var postgreSql = Build(frame, PostgreSql))
            postgreSql.ValidateStellaegisModules();

        using var inMemory = Build(frame, InMemory);
        Assert.Throws<InvalidOperationException>(inMemory.ValidateStellaegisModules);
    }

    [Fact]
    public void The_postgresql_store_declares_its_database_with_its_scripts()
    {
        using var provider = Build(ApplicationFrame.SingleCompartment("acme"), PostgreSql);

        var database = Assert.Single(provider.GetStellaegisModuleDatabases());
        Assert.Same(TrashDatabase.Declaration, database);
        Assert.Equal(("trash", DatabaseEngine.PostgreSql), (database.Schema, database.Engine));
        Assert.Equal(["0001_initial.sql"], database.Scripts.Select(s => s.FileName));
        Assert.Equal(["pgcrypto"], database.Extensions);
        Assert.Equal(["trash.items"], database.SecuredTables);
        Assert.Equal(FrameFeature.OwnerIsolation, Assert.Single(database.FrameScripts).Feature);
    }

    [Fact]
    public void The_options_are_validated_when_the_host_resolves_them()
    {
        var incomplete = new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("Stellaegis:Trash:ListLimit", "0")]).Build();
        using var provider = new ServiceCollection().AddLogging().AddStellaegisFrame(ApplicationFrame.SingleCompartment("acme"))
            .AddStellaegisTrashInMemory(incomplete).BuildServiceProvider();

        var exception = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<ITrashService>());
        Assert.Contains("ListLimit", exception.Message);
    }
}
