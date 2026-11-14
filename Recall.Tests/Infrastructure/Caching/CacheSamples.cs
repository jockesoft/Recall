using Recall.Web.Domain.TheTvDb;

namespace Recall.Tests.Infrastructure.Caching;

/// <summary>
/// One filled-in value of each type the Redis tier holds, for round trips.
/// (OMDb snapshots are in Postgres only; nothing of OMDb's goes to Redis.)
/// </summary>
internal static class CacheSamples
{
    public static SeriesAggregate Series(int id = 78957) => new()
    {
        TvdbId = id,
        Name = "Gold Rush",
        Slug = "gold-rush",
        Overview = "Miners chase gold in Alaska and the Yukon.",
        ImageUrl = "https://artworks.thetvdb.com/banners/posters/78957-1.jpg",
        BackgroundUrl = "https://artworks.thetvdb.com/banners/fanart/78957-1_t.jpg",
        FirstAired = new DateOnly(2010, 12, 3),
        OriginalCountry = "usa",
        AirsTime = "20:00",
        AverageRuntimeMinutes = 43,
        Score = 8.1,
        Status = new SeriesStatus { Id = 1, Name = "Continuing", KeepUpdated = true, RecordType = "series" },
        Genres = ["Reality", "Documentary"],
        Seasons = [new SeasonSummary { Id = 1, Number = 1, Name = "Season 1", Networks = ["Discovery"] }],
        // Repetitive field names and URLs, like a real aggregate: what compression feeds on.
        Episodes = Enumerable.Range(1, 300).Select(n => new EpisodeSummary
        {
            Id = 1_000 + n,
            SeasonNumber = 1 + n / 20,
            EpisodeNumber = 1 + n % 20,
            Name = $"Episode {n}",
            Overview = $"The crews dig through permafrost, episode {n}.",
            Image = $"https://artworks.thetvdb.com/banners/episodes/78957/{1_000 + n}.jpg",
        }).ToList(),
        Characters = [new Character { Id = 5, Name = "Himself", PersonName = "Parker Schnabel", Image = "https://artworks.thetvdb.com/banners/actors/5.jpg" }],
    };

    public static MovieAggregate Movie(int id = 7) => new()
    {
        TvdbId = id,
        Name = "Heat",
        Overview = "A detective hunts a crew of thieves.",
        ReleaseDate = new DateOnly(1995, 12, 15),
        RuntimeMinutes = 170,
        Budget = 60_000_000m,
        Status = new MovieStatus { Id = 5, Name = "Released", KeepUpdated = false },
        Genres = ["Crime", "Thriller"],
        Characters = [new Character { Id = 9, Name = "Neil McCauley", PersonName = "Robert De Niro" }],
    };

    public static Episode Episode(int id = 2001) => new()
    {
        Id = id,
        SeriesId = 78957,
        Name = "Gold Fever",
        Aired = "2026-10-02",
        SeasonNumber = 17,
        Number = 1,
        Runtime = 43,
        Image = "https://artworks.thetvdb.com/banners/episodes/78957/2001.jpg",
        NameTranslations = ["eng"],
    };

    public static Series SeriesExtended(int id = 78957) => new()
    {
        Id = id,
        Name = "Gold Rush",
        FirstAired = "2010-12-03",
        Status = new SeriesStatusInfo { Id = 1, Name = "Continuing", KeepUpdated = true },
        Episodes = [Episode()],
        Characters = [new Character { Id = 5, Name = "Himself" }],
    };
}
