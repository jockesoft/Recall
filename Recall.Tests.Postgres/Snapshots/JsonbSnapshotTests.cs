using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Recall.Web.Domain.Omdb;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.OmdbCache;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Postgres.Snapshots;

/// <summary>
/// The seven cache tables keep their payload in a <c>jsonb</c> column. On
/// SQLite that column is plain text, so the store tests there prove the
/// serialization and nothing about what PostgreSQL does with it: it parses the
/// document, rejects what is not JSON, and stores its own normalized form.
/// </summary>
[TestFixture]
public sealed class JsonbSnapshotTests : PostgresFixture
{
    // Text that has to survive: accents, an en dash, CJK, an emoji, quotes and a backslash.
    private const string AwkwardText = "Bron/Broen – säsong 1 · 日本語 😀 \"quoted\" \\ back";

    [TestCase("cached_series_aggregate")]
    [TestCase("cached_series_extended")]
    [TestCase("cached_episode_extended")]
    [TestCase("cached_movie_aggregate")]
    [TestCase("cached_series_omdb")]
    [TestCase("cached_episode_omdb")]
    [TestCase("cached_movie_omdb")]
    public async Task PayloadColumn_Should_BeJsonb(string table)
    {
        var type = await ScalarAsync<string>(
            "SELECT data_type FROM information_schema.columns WHERE table_name = $1 AND column_name = 'payload'",
            table);

        type.Should().Be("jsonb");
    }

    [Test]
    public async Task SeriesAggregate_Should_RoundTrip()
    {
        var id = NextId();
        var aggregate = new SeriesAggregate
        {
            TvdbId = id,
            Name = AwkwardText,
            Slug = "bron-broen",
            Overview = "Line one\nLine two\ttabbed",
            FirstAired = new DateOnly(2011, 9, 21),
            NextAired = null,
            Score = 8.6,
            AverageRuntimeMinutes = 58,
            Status = new SeriesStatus { Id = 2, Name = "Ended", KeepUpdated = false },
            Aliases = ["The Bridge", "Broen"],
            Seasons = [new SeasonSummary { Id = 1, Number = 1, Name = "Season 1", Networks = ["SVT1", "DR1"] }],
            Episodes =
            [
                new EpisodeSummary { Id = 11, SeasonNumber = 1, EpisodeNumber = 1, Name = "Avsnitt 1", Aired = new DateOnly(2011, 9, 21), RuntimeMinutes = 58 },
                new EpisodeSummary { Id = 12, SeasonNumber = 1, EpisodeNumber = 2, Name = "Avsnitt 2", Image = null }
            ],
            Characters = [new Character { Id = 5, Name = "Saga Norén", PersonName = "Sofia Helin", IsFeatured = true }],
            RemoteIds = [new SeriesRemoteId { Id = "tt1733785", Type = 2, SourceName = "IMDB" }]
        };

        await TvdbStore().SaveSeriesAggregateAsync(aggregate, "eng");
        var loaded = await TvdbStore().GetSeriesAggregateAsync(id, "eng");

        loaded.Should().BeEquivalentTo(aggregate);
    }

    [Test]
    public async Task SeriesAggregate_Should_BeStoredAsADocument_NotAsAQuotedString()
    {
        var id = NextId();
        await TvdbStore().SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = id, Name = AwkwardText }, "eng");

        // A payload sent as a JSON string would be stored double-encoded: still
        // readable by the app, but not a document PostgreSQL can look into.
        (await ScalarAsync<string>("SELECT jsonb_typeof(payload) FROM cached_series_aggregate WHERE tvdb_id = $1", id))
            .Should().Be("object");
        (await ScalarAsync<string>("SELECT payload ->> 'name' FROM cached_series_aggregate WHERE tvdb_id = $1", id))
            .Should().Be(AwkwardText);
    }

    [Test]
    public async Task MovieAggregate_Should_RoundTrip()
    {
        var id = NextId();
        var aggregate = new MovieAggregate
        {
            TvdbId = id,
            Name = AwkwardText,
            ReleaseDate = new DateOnly(2023, 7, 21),
            RuntimeMinutes = 180,
            Score = 8.3,
            Budget = 100_000_000.50m,
            BoxOffice = 975_811_030m,
            Status = new MovieStatus { Id = 5, Name = "Released", KeepUpdated = true },
            Genres = ["Drama", "History"],
            Studios = ["Syncopy"],
            Characters = [new Character { Id = 1, Name = "J. Robert Oppenheimer", PersonName = "Cillian Murphy" }],
            RemoteIds = [new MovieRemoteId { Id = "tt15398776", Type = 2, SourceName = "IMDB" }]
        };

        await TvdbStore().SaveMovieAggregateAsync(aggregate, "eng");
        var loaded = await TvdbStore().GetMovieAggregateAsync(id, "eng");

        loaded.Should().BeEquivalentTo(aggregate);
    }

    [Test]
    public async Task SeriesExtended_Should_RoundTrip()
    {
        var id = NextId();
        var series = new Series
        {
            Id = id,
            Name = AwkwardText,
            FirstAired = "2011-09-21",
            Score = 12345.5,
            Status = new SeriesStatusInfo { Id = 2, Name = "Ended", KeepUpdated = false },
            Aliases = [new SeriesAlias { Language = "eng", Name = "The Bridge" }],
            NameTranslations = ["eng", "swe", "dan"],
            Episodes = [new Episode { Id = 11, Name = "Avsnitt 1", SeasonNumber = 1, Number = 1, SeriesId = id }]
        };

        await TvdbStore().SaveSeriesExtendedAsync(series);
        var loaded = await TvdbStore().GetSeriesExtendedAsync(id);

        loaded.Should().BeEquivalentTo(series);
    }

    [Test]
    public async Task EpisodeExtended_Should_RoundTrip_AndFillTheIndexedColumns()
    {
        var id = NextId();
        var episode = new Episode
        {
            Id = id,
            SeriesId = NextId(),
            Name = AwkwardText,
            Aired = "2011-09-21",
            Image = "https://artworks.thetvdb.com/banners/episodes/1.jpg",
            IsMovie = false,
            Runtime = 58,
            SeasonNumber = 1,
            Number = 1,
            Awards = [new EpisodeAward { Id = 1, Name = "Kristallen", Year = "2012", IsWinner = true }],
            ContentRatings = [new EpisodeContentRating { Name = "15", Country = "swe" }],
            RemoteIds = [new EpisodeRemoteId { Id = "tt1829891", Type = 2, SourceName = "IMDB" }]
        };

        await TvdbStore().SaveEpisodeExtendedAsync(episode);
        var loaded = await TvdbStore().GetEpisodeExtendedAsync(id);

        loaded.Should().BeEquivalentTo(episode);

        await using var db = NewContext();
        var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == id);
        row.Aired.Should().Be(new DateOnly(2011, 9, 21));
        row.HasImage.Should().BeTrue();
    }

    [Test]
    public async Task EpisodeExtended_Should_BeRewritten_WhenAnImageIsBackfilled()
    {
        var (seriesId, episodeId) = (NextId(), NextId());
        await TvdbStore().SaveEpisodeExtendedAsync(new Episode { Id = episodeId, SeriesId = seriesId, Name = "No still yet" });

        var patched = await TvdbStore().BackfillEpisodeImagesFromAggregateAsync(new SeriesAggregate
        {
            TvdbId = seriesId,
            Episodes = [new EpisodeSummary { Id = episodeId, Image = "https://artworks.thetvdb.com/still.jpg" }]
        });

        patched.Should().ContainSingle();
        (await TvdbStore().GetEpisodeExtendedAsync(episodeId))!.Image.Should().Be("https://artworks.thetvdb.com/still.jpg");
        (await ScalarAsync<string>("SELECT payload ->> 'image' FROM cached_episode_extended WHERE episode_tvdb_id = $1", episodeId))
            .Should().Be("https://artworks.thetvdb.com/still.jpg");
    }

    [Test]
    public async Task OmdbSeries_Should_RoundTrip()
    {
        var id = NextId();
        var data = new OmdbSeries
        {
            Title = "Lioness",
            Year = "2023–",
            ImdbRating = "7.8",
            ImdbVotes = "98,123",
            ImdbId = "tt13111078",
            Type = "series",
            TotalSeasons = "3",
            Response = "True",
            Ratings = [new OmdbRating { Source = "Internet Movie Database", Value = "7.8/10" }]
        };

        await new OmdbSnapshotStore(NewFactory(), NullLogger<OmdbSnapshotStore>.Instance).UpsertAsync(id, "tt13111078", data);
        var loaded = await new OmdbSnapshotStore(NewFactory(), NullLogger<OmdbSnapshotStore>.Instance).GetAsync(id);

        loaded.Should().BeEquivalentTo(data);

        // OMDb's own mixed-case property names are what is stored.
        (await ScalarAsync<string>("SELECT payload ->> 'imdbRating' FROM cached_series_omdb WHERE tvdb_id = $1", id))
            .Should().Be("7.8");
        (await ScalarAsync<string>("SELECT payload ->> 'Year' FROM cached_series_omdb WHERE tvdb_id = $1", id))
            .Should().Be("2023–");
    }

    [Test]
    public async Task OmdbMovie_Should_RoundTrip()
    {
        var id = NextId();
        var data = new OmdbMovie
        {
            Title = "Oppenheimer",
            Year = "2023",
            Runtime = "180 min",
            ImdbRating = "8.3",
            Metascore = "90",
            ImdbId = "tt15398776",
            Type = "movie",
            Response = "True",
            Ratings =
            [
                new OmdbRating { Source = "Rotten Tomatoes", Value = "93%" },
                new OmdbRating { Source = "Metacritic", Value = "90/100" }
            ]
        };
        var store = new MovieOmdbSnapshotStore(NewFactory(), NullLogger<MovieOmdbSnapshotStore>.Instance);

        await store.UpsertAsync(id, "tt15398776", data);

        (await store.GetAsync(id)).Should().BeEquivalentTo(data);
    }

    [Test]
    public async Task OmdbEpisode_Should_RoundTrip_AndBeOverwrittenByTheNextUpsert()
    {
        var id = NextId();
        var first = new OmdbEpisode { Title = "Pilot", ImdbRating = "N/A", ImdbId = "tt0959621", Response = "True" };
        var second = first with { ImdbRating = "9.0", ImdbVotes = "45,001" };

        await using (var db = NewContext())
        {
            await new EpisodeOmdbSnapshotStore(db, NullLogger<EpisodeOmdbSnapshotStore>.Instance).UpsertAsync(id, "tt0959621", first);
        }

        await using (var db = NewContext())
        {
            var store = new EpisodeOmdbSnapshotStore(db, NullLogger<EpisodeOmdbSnapshotStore>.Instance);
            (await store.GetAsync(id)).Should().BeEquivalentTo(first);
            await store.UpsertAsync(id, "tt0959621", second);
        }

        await using var verify = NewContext();
        var reloaded = await new EpisodeOmdbSnapshotStore(verify, NullLogger<EpisodeOmdbSnapshotStore>.Instance).GetAsync(id);
        reloaded.Should().BeEquivalentTo(second);
        (await ScalarAsync<long>("SELECT count(*) FROM cached_episode_omdb WHERE tvdb_id = $1", id)).Should().Be(1);
    }

    [Test]
    public async Task Omdb_Should_StoreSqlNull_ForATitleThatCouldNotBeEnriched()
    {
        var id = NextId();
        var store = new OmdbSnapshotStore(NewFactory(), NullLogger<OmdbSnapshotStore>.Instance);

        await store.UpsertAsync(id, imdbId: null, data: null);

        (await store.GetAsync(id)).Should().BeNull();
        (await ScalarAsync<bool>("SELECT payload IS NULL FROM cached_series_omdb WHERE tvdb_id = $1", id))
            .Should().BeTrue("the marker row has no document at all, not a JSON null");
    }

    [Test]
    public async Task Jsonb_Should_NormalizeTheStoredText_AndStillDeserialize()
    {
        var id = NextId();
        const string written = """{ "tvdbId": 1,   "name": "first",  "name": "Spaced Out" }""";

        await using (var db = NewContext())
        {
            db.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
            {
                TvdbId = id, Language = "eng", Name = "Spaced Out", Payload = written, RetrievedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // jsonb keeps the meaning, not the text: whitespace goes, keys are
        // reordered, and of a repeated key only the last value is kept.
        var stored = await ScalarAsync<string>("SELECT payload::text FROM cached_series_aggregate WHERE tvdb_id = $1", id);
        stored.Should().Be("""{"name": "Spaced Out", "tvdbId": 1}""");

        (await TvdbStore().GetSeriesAggregateAsync(id, "eng"))!.Name.Should().Be("Spaced Out");
    }

    [Test]
    public async Task Jsonb_Should_RejectAPayloadThatIsNotJson()
    {
        // The "corrupt row" the SQLite tests seed cannot exist here: the write fails instead.
        await using var db = NewContext();
        db.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
        {
            TvdbId = NextId(), Language = "eng", Name = "Corrupt", Payload = "{ this is not json", RetrievedUtc = DateTime.UtcNow
        });

        var act = () => db.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
    }

    [Test]
    public async Task Store_Should_TreatAValidDocumentOfTheWrongShapeAsAMiss()
    {
        // What "corrupt" can still mean on PostgreSQL: valid JSON that is not
        // the object the app expects, such as a row from an older format.
        var id = NextId();
        await using (var db = NewContext())
        {
            db.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
            {
                TvdbId = id, Language = "eng", Name = "Old format", Payload = """["not", "an", "object"]""", RetrievedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        (await TvdbStore().GetSeriesAggregateAsync(id, "eng")).Should().BeNull();
    }

    private TvdbSnapshotStore TvdbStore() => new(NewFactory(), NullLogger<TvdbSnapshotStore>.Instance);
}
