using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Infrastructure.Persistence.Entities;

/// <summary>
/// "Not interested": a title the user hid from Discover. Discover leaves it
/// out of every row; nothing else reads it. Undo deletes the row. One row per
/// user, kind and title.
/// </summary>
public sealed class DiscoverDismissalEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DiscoverKind Kind { get; set; }

    public int TvdbId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
