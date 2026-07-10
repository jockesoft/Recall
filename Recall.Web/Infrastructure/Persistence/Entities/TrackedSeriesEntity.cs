namespace Recall.Web.Infrastructure.Persistence.Entities;

public sealed class TrackedSeriesEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public AppUserEntity User { get; set; } = null!;

    public int TvdbId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? ImageUrl { get; set; }
    public DateOnly? FirstAired { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// PostgreSQL xmin-backed concurrency token.
    /// </summary>
    public uint Version { get; set; }
}