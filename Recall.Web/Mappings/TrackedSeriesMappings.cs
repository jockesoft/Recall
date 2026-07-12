using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Mappings;

public static class TrackedSeriesMappings
{
    public static TrackedSeries ToDomain(this TrackedSeriesEntity entity)
    {
        return new TrackedSeries
        {
            Id = entity.Id,
            UserId = entity.UserId,
            TvdbId = entity.TvdbId,
            Name = entity.Name,
            Overview = entity.Overview,
            ImageUrl = entity.ImageUrl,
            FirstAired = entity.FirstAired,
            CreatedUtc = entity.CreatedUtc,
            UpdatedUtc = entity.UpdatedUtc,
            Version = entity.Version
        };
    }

    public static TrackedSeriesEntity ToEntity(this TrackedSeries domain)
    {
        return new TrackedSeriesEntity
        {
            Id = domain.Id == Guid.Empty ? Guid.NewGuid() : domain.Id,
            UserId = domain.UserId,
            TvdbId = domain.TvdbId,
            Name = domain.Name,
            Overview = domain.Overview,
            ImageUrl = domain.ImageUrl,
            FirstAired = domain.FirstAired,
            CreatedUtc = domain.CreatedUtc == default ? DateTime.UtcNow : domain.CreatedUtc,
            UpdatedUtc = domain.UpdatedUtc == default ? DateTime.UtcNow : domain.UpdatedUtc,
            Version = domain.Version
        };
    }

    public static TrackedSeries FromTvDbDetails(Guid userId, TvSeriesDetails details)
    {
        DateOnly? firstAired = null;
        if (!string.IsNullOrWhiteSpace(details.FirstAired) &&
            DateOnly.TryParse(details.FirstAired, out var parsed))
        {
            firstAired = parsed;
        }

        return new TrackedSeries
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TvdbId = details.TvdbId,
            Name = details.Name,
            Overview = details.Overview,
            ImageUrl = details.ImageUrl,
            FirstAired = firstAired,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
    }
}