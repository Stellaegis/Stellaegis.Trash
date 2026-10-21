using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Trash;

/// <summary>
/// The options of the trash, bound from <c>Stellaegis:Trash</c> and validated when the host starts.
/// <code>
/// Stellaegis:Trash:DefaultRetention  = 30.00:00:00
/// Stellaegis:Trash:ListLimit         = 500
/// Stellaegis:Trash:MaxPayloadLength  = 16777216
/// </code>
/// </summary>
public sealed class TrashOptions : IValidatableOptions
{
    /// <summary>Reference configuration section for these options.</summary>
    public const string SectionName = "Stellaegis:Trash";

    /// <summary>
    /// How long an item stays before the purge of the expired items deletes it, when the request sets no deadline.
    /// Thirty days unless set; <see langword="null"/> keeps items until somebody deletes them.
    /// </summary>
    public TimeSpan? DefaultRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The most items a list returns, the most recently trashed first. 500 unless set, between 1 and 10,000.</summary>
    public int ListLimit { get; init; } = 500;

    /// <summary>The longest payload the trash accepts, in bytes. 16 MiB unless set.</summary>
    public long MaxPayloadLength { get; init; } = 16 * 1024 * 1024;

    /// <inheritdoc />
    public string? FindError()
    {
        if (DefaultRetention is { } retention && retention <= TimeSpan.Zero)
            return "DefaultRetention must be positive, or unset to keep items until they are deleted.";
        if (ListLimit is < 1 or > 10_000)
            return "ListLimit must be between 1 and 10000.";
        if (MaxPayloadLength < 1)
            return "MaxPayloadLength must be positive.";
        return null;
    }
}
