using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>
/// The identity of the trash module, <c>trash</c>: the routes of <c>Stellaegis.Trash.AspNetCore</c> belong to it,
/// and its declaration to the application is made from <c>AddStellaegisTrashServices</c>.
/// </summary>
public sealed class TrashModule : IStellaegisModule
{
    /// <inheritdoc />
    public static string Name => "trash";
}

/// <summary>
/// The operations of the trash, one per route of <c>Stellaegis.Trash.AspNetCore</c>, declared on the module. The
/// host keys its policies on their identity (<c>RequireAuthorizationPerOperation</c>) and receives them as the
/// requirement of <c>AuthorizeResourceAsync</c> on each item a route loads. Their kind is a fact about the data and
/// says nothing of who may perform them.
/// </summary>
public static class TrashOperations
{
    /// <summary>The target every operation acts on: an item of the trash.</summary>
    public const string Target = "trash_items";

    /// <summary><c>POST trash</c>: puts an object in the trash, replacing a previous trashing of the same object.</summary>
    public static readonly Operation Put = new(Target, "put", OperationKind.Write);

    /// <summary><c>GET trash</c>: lists the items of the space, the most recently trashed first.</summary>
    public static readonly Operation List = new(Target, "list", OperationKind.Read);

    /// <summary><c>GET trash/{id}</c>: reads what identifies an item, without its payload.</summary>
    public static readonly Operation Read = new(Target, "read", OperationKind.Read);

    /// <summary><c>GET trash/{id}/payload</c>: reads the payload of an item, without restoring it.</summary>
    public static readonly Operation ReadPayload = new(Target, "read_payload", OperationKind.Read);

    /// <summary><c>POST trash/{id}/restore</c>: hands the payload back, the item recorded as restored by its actor.</summary>
    public static readonly Operation Restore = new(Target, "restore", OperationKind.Write);

    /// <summary><c>DELETE trash/{id}</c>: deletes an item for good, before its deadline.</summary>
    public static readonly Operation Delete = new(Target, "delete", OperationKind.Delete);

    /// <summary>
    /// <c>DELETE trash/compartments/{compartment}/expired</c>, mapped by <c>MapTrashAdminEndpoints</c>, and the work
    /// done outside requests: deletes the items whose deadline has passed.
    /// </summary>
    public static readonly Operation PurgeExpired = new(Target, "purge_expired", OperationKind.Delete);

    /// <summary>
    /// <c>DELETE trash/compartments/{compartment}</c>, mapped by <c>MapTrashAdminEndpoints</c>: deletes every item of
    /// a compartment for good.
    /// </summary>
    public static readonly Operation Purge = new(Target, "purge", OperationKind.Delete);

    /// <summary>Every operation of the module, as its declaration carries them.</summary>
    public static IReadOnlyList<Operation> All { get; } = [Put, List, Read, ReadPayload, Restore, Delete, PurgeExpired, Purge];
}
