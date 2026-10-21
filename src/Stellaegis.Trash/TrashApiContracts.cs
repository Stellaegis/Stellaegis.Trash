using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>
/// What <c>POST trash</c> takes: the object serialized by the application, in base64, with what identifies it. Kept
/// in the core so that a client written against the endpoints shares the contract.
/// </summary>
/// <param name="SourceKind">What kind of object this is, an identifier.</param>
/// <param name="SourceUniqueId">The identifier of the object in the application.</param>
/// <param name="DisplayName">What to show for the object in the trash.</param>
/// <param name="Payload">The serialized object, in base64.</param>
/// <param name="ContentType">The media type of the payload, <c>application/json</c> unless given.</param>
/// <param name="PurgeAt">When the item is deleted for good; the module's default retention unless given.</param>
public sealed record TrashRequestContract(
    string SourceKind, Guid SourceUniqueId, string DisplayName, string Payload, string? ContentType = null, DateTimeOffset? PurgeAt = null);

/// <summary>What identifies an item of the trash, as the endpoints answer it.</summary>
public sealed record TrashItemResponse(
    Guid TrashItemUniqueId,
    Compartment? Compartment,
    bool IsShared,
    string SourceKind,
    Guid SourceUniqueId,
    string DisplayName,
    string ContentType,
    long PayloadLength,
    TrashItemStatus Status,
    DateTimeOffset TrashedAt,
    Actor TrashedBy,
    DateTimeOffset? PurgeAt,
    DateTimeOffset? RestoredAt,
    Actor? RestoredBy)
{
    /// <summary>The answer standing for <paramref name="item"/>.</summary>
    public static TrashItemResponse From(TrashItem item) => new(
        Guard.NotNull(item).TrashItemUniqueId, item.Space.Compartment, item.Space.IsShared, item.SourceKind, item.SourceUniqueId,
        item.DisplayName, item.ContentType, item.PayloadLength, item.Status, item.TrashedAt, item.TrashedBy, item.PurgeAt,
        item.RestoredAt, item.RestoredBy);
}

/// <summary>An item with its payload in base64, as <c>GET trash/{id}/payload</c> and <c>POST trash/{id}/restore</c> answer it.</summary>
public sealed record TrashedObjectResponse(TrashItemResponse Item, string Payload)
{
    /// <summary>The answer standing for <paramref name="trashed"/>.</summary>
    public static TrashedObjectResponse From(TrashedObject trashed)
        => new(TrashItemResponse.From(Guard.NotNull(trashed).Item), Convert.ToBase64String(trashed.Payload.Span));
}
