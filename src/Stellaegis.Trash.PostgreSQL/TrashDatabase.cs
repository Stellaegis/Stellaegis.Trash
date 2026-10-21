using Stellaegis.Primitives.Data;

namespace Stellaegis.Trash.PostgreSQL;

/// <summary>The marker of the database holding the <c>trash</c> schema, for the unit of work and its repositories.</summary>
public sealed class TrashDatabase
{
    private const string ResourcePrefix = "Stellaegis.Trash.PostgreSQL.Sql.PostgreSql";

    private TrashDatabase()
    {
    }

    /// <summary>
    /// What the adapter declares about its database: the schema <c>trash</c>, its scripts embedded in this package,
    /// the extension <c>pgcrypto</c>, the table under forced row-level security and its policy, and the isolation by
    /// owner as a script following the frame. An installer applies it under an administration role
    /// (<c>DatabaseLifecycle.InstallAsync</c>); the application verifies it at startup under its own.
    /// </summary>
    public static ModuleDatabase Declaration { get; } = ModuleDatabase.PostgreSql<TrashModule>("trash")
        .WithScripts(SqlScripts.FromResources(typeof(TrashDatabase).Assembly, ResourcePrefix))
        .RequiringExtension("pgcrypto")
        .WithRowSecurity("trash.items", "pl_items_space")
        .WhenFrameDeclares(FrameFeature.OwnerIsolation,
            "owner_isolation", SqlScripts.ReadResource(typeof(TrashDatabase).Assembly, $"{ResourcePrefix}.owner_isolation.sql"),
            "owner_isolation_remove", SqlScripts.ReadResource(typeof(TrashDatabase).Assembly, $"{ResourcePrefix}.owner_isolation_remove.sql"),
            new RowPolicy("trash.items", "pl_items_owner"));
}
