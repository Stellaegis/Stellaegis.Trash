using Stellaegis.Primitives;

namespace Stellaegis.Trash;

/// <summary>The state of an item of the trash.</summary>
public enum TrashItemStatus
{
    /// <summary>In the trash, awaiting a restoration, a deletion or its deadline.</summary>
    Trashed,

    /// <summary>Handed back to its application, which recreated its object; kept until its deadline, as a trace.</summary>
    Restored,
}

/// <summary>
/// What identifies an item of the trash, without its payload: the object it stands for, who trashed it and when,
/// its deadline, and who restored it if anybody did. Immutable.
/// </summary>
public sealed record TrashItem
{
    /// <summary>The identifier of the item in the trash, which the routes carry.</summary>
    public required Guid TrashItemUniqueId { get; init; }

    /// <summary>The space the item belongs to: a compartment, or the shared space.</summary>
    public required Space Space { get; init; }

    /// <summary>
    /// What kind of object the item stands for, as the application names its kinds — <c>invoices</c>,
    /// <c>parties</c>: an identifier, which the trash does not interpret.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// The identifier of the object in the application, which the application gives back to the object it
    /// recreates from the payload: a restored object keeps its identity.
    /// </summary>
    public required Guid SourceUniqueId { get; init; }

    /// <summary>What to show a user who looks into the trash: the name of the object, as the application words it.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The media type of the payload, <c>application/json</c> for instance.</summary>
    public required string ContentType { get; init; }

    /// <summary>The length of the payload, in bytes.</summary>
    public required long PayloadLength { get; init; }

    /// <summary>The state of the item.</summary>
    public required TrashItemStatus Status { get; init; }

    /// <summary>When the object was put in the trash.</summary>
    public required DateTimeOffset TrashedAt { get; init; }

    /// <summary>Who put it there.</summary>
    public required Actor TrashedBy { get; init; }

    /// <summary>When the item is deleted for good by the purge of the expired items, or <see langword="null"/> when it never is.</summary>
    public DateTimeOffset? PurgeAt { get; init; }

    /// <summary>When the item was restored, or <see langword="null"/>.</summary>
    public DateTimeOffset? RestoredAt { get; init; }

    /// <summary>Who restored it, or <see langword="null"/>.</summary>
    public Actor? RestoredBy { get; init; }

    /// <summary>Whether the deadline of the item has passed at <paramref name="asOf"/>.</summary>
    public bool IsExpiredAt(DateTimeOffset asOf) => PurgeAt is { } purgeAt && purgeAt <= asOf;
}

/// <summary>An item of the trash with its payload: what a restoration hands back.</summary>
/// <param name="Item">What identifies the item.</param>
/// <param name="Payload">The serialized object, as the application gave it.</param>
public sealed record TrashedObject(TrashItem Item, ReadOnlyMemory<byte> Payload);

/// <summary>
/// What an application gives to put an object in the trash: the object serialized by the application, in the
/// form of its choice, with what identifies it and what to show for it.
/// </summary>
public sealed class TrashRequest
{
    /// <summary>What kind of object this is, an identifier the application chooses — <c>invoices</c>.</summary>
    public required string SourceKind { get; init; }

    /// <summary>The identifier of the object in the application, never empty.</summary>
    public required Guid SourceUniqueId { get; init; }

    /// <summary>What to show for the object in the trash, 1 to 256 characters.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The serialized object, 1 byte at least, no more than the module's <see cref="TrashOptions.MaxPayloadLength"/>.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The media type of the payload. <c>application/json</c> unless set.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>
    /// When the item is deleted for good by the purge of the expired items. Unset, the module's
    /// <see cref="TrashOptions.DefaultRetention"/> decides; <see cref="DateTimeOffset.MaxValue"/> keeps the item
    /// until somebody deletes it.
    /// </summary>
    public DateTimeOffset? PurgeAt { get; init; }
}
