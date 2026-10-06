namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class TrackedSeriesEntity : IHasAuditTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int TvdbId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? ImageUrl { get; set; }
    public DateOnly? FirstAired { get; set; }

    /// <summary>
    /// When the user stopped watching the series; null while they are watching
    /// it. A stopped series stays in the library with its history, but is left
    /// out of everything that says "there is something for you to watch" (see
    /// <c>SeriesLibraryStateRule</c>).
    /// </summary>
    public DateTime? StoppedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// PostgreSQL xmin-backed concurrency token.
    /// </summary>
    public uint Version { get; set; }
}