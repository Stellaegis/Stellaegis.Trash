using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Trash.PostgreSQL;

/// <summary>
/// The options of the PostgreSQL store, bound from <c>Stellaegis:Trash:PostgreSQL</c> and validated when the host
/// starts. Payloads are encrypted under one of two passphrase slots, which alternate for a rotation: new writes use
/// the active slot, each row records the slot that encrypted it, and a retired passphrase stays configured as long
/// as a row uses it.
/// <code>
/// Stellaegis:Trash:PostgreSQL:ConnectionString          = Host=…;Database=…;Username=myapp;…
/// Stellaegis:Trash:PostgreSQL:Passphrases:ActiveKeySlot = 1
/// Stellaegis:Trash:PostgreSQL:Passphrases:Keys:1        = passphrase-for-slot-1
/// Stellaegis:Trash:PostgreSQL:Passphrases:Keys:2        = (empty until the first rotation)
/// </code>
/// </summary>
/// <remarks>
/// The passphrases come from the configuration of the process — a secret store or the environment, never a
/// committed file —, not from a store of the application: a reader of the database without them reads nothing.
/// The connection string is the application's role, which the installation granted its data rights and no DDL.
/// </remarks>
public sealed class TrashDatabaseOptions : IValidatableOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Trash:PostgreSQL";

    /// <summary>The connection string to the database holding the <c>trash</c> schema, under the application's role.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>The passphrases encrypting the payloads, by slot.</summary>
    public KeySlots Passphrases { get; init; } = new();

    /// <inheritdoc />
    public string? FindError()
        => string.IsNullOrWhiteSpace(ConnectionString) ? "ConnectionString is required." : Passphrases.FindError();
}
