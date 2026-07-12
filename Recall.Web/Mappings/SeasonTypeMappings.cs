using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

namespace Recall.Web.Mappings;

public static class SeasonTypeMappings
{
    public static SeasonType ToDomain(this SeasonTypeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new SeasonType
        {
            AlternateName = dto.AlternateName,
            Id = dto.Id,
            Name = dto.Name,
            Type = dto.Type
        };
    }
}