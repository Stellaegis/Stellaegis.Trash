using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>The error codes of the module, as the exceptions and the problem details carry them.</summary>
public static class TrashErrors
{
    /// <summary>No item of the space has the identifier asked for.</summary>
    public const string NotFound = "TrashItem.NotFound";

    /// <summary>The item was restored already: its payload went back to the application once.</summary>
    public const string AlreadyRestored = "TrashItem.AlreadyRestored";

    /// <summary>The deadline of the item has passed: it awaits the purge, and goes back to nobody.</summary>
    public const string Expired = "TrashItem.Expired";

    /// <summary>The request does not describe an object the trash can take.</summary>
    public const string InvalidRequest = "TrashRequest.Invalid";

    /// <summary>The payload is longer than the module accepts.</summary>
    public const string PayloadTooLarge = "TrashRequest.PayloadTooLarge";
}

/// <summary>No item of the space has the identifier asked for; <see cref="TrashErrors.NotFound"/>, answered 404.</summary>
public sealed class TrashItemNotFoundException(Guid trashItemUniqueId)
    : UserFacingException(Error.NotFound(TrashErrors.NotFound, $"No item of the trash has the identifier {trashItemUniqueId:N}."))
{
    /// <summary>The identifier asked for.</summary>
    public Guid TrashItemUniqueId { get; } = trashItemUniqueId;
}

/// <summary>The item was restored already; <see cref="TrashErrors.AlreadyRestored"/>, answered 409.</summary>
public sealed class TrashItemRestoredException(Guid trashItemUniqueId)
    : UserFacingException(Error.Conflict(TrashErrors.AlreadyRestored, $"The item {trashItemUniqueId:N} was restored already."))
{
    /// <summary>The item.</summary>
    public Guid TrashItemUniqueId { get; } = trashItemUniqueId;
}

/// <summary>The deadline of the item has passed; <see cref="TrashErrors.Expired"/>, answered 409.</summary>
public sealed class TrashItemExpiredException(Guid trashItemUniqueId, DateTimeOffset purgeAt)
    : UserFacingException(Error.Conflict(TrashErrors.Expired, $"The item {trashItemUniqueId:N} expired on {purgeAt:O}: it awaits the purge."))
{
    /// <summary>The item.</summary>
    public Guid TrashItemUniqueId { get; } = trashItemUniqueId;

    /// <summary>Its deadline.</summary>
    public DateTimeOffset PurgeAt { get; } = purgeAt;
}

/// <summary>
/// The request does not describe an object the trash can take; <see cref="TrashErrors.InvalidRequest"/> or
/// <see cref="TrashErrors.PayloadTooLarge"/>, answered 400. The message names what the caller gave and can correct.
/// </summary>
public sealed class TrashRequestException(string code, string message)
    : UserFacingException(Error.Validation(code, message));
