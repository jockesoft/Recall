using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Infrastructure.Persistence;

[TestFixture]
public sealed class TvdbSnapshotStoreTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    private TvdbSnapshotStore NewStore() =>
        new(new PooledLikeFactory(_dbOptions), NullLogger<TvdbSnapshotStore>.Instance);

    // Minimal IDbContextFactory over the shared in-memory SQLite connection —
    // every CreateDbContext() call hands back a context bound to the same DB.
    private sealed class PooledLikeFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    [Test]
    public async Task SaveThenGet_SeriesAggregate_RoundTripsTheGraph()
    {
        var aggregate = new SeriesAggregate
        {
            TvdbId = 100,
            Name = "Round Trip Show",
            Slug = "round-trip",
            Status = new SeriesStatus { Name = "Ended", KeepUpdated = false },
            Episodes =
            [
                new EpisodeSummary { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot", Aired = new DateOnly(2020, 1, 1) }
            ]
        };

        await NewStore().SaveSeriesAggregateAsync(aggregate, "eng");

        var loaded = await NewStore().GetSeriesAggregateAsync(100, "eng");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Round Trip Show");
        loaded.Status!.Name.Should().Be("Ended");
        loaded.Episodes.Should().ContainSingle();
        loaded.Episodes[0].Name.Should().Be("Pilot");
        loaded.Episodes[0].Aired.Should().Be(new DateOnly(2020, 1, 1));
    }

    [Test]
    public async Task SaveSeriesAggregate_IsInsertOnly_DoesNotOverwriteExisting()
    {
        await NewStore().SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Original" }, "eng");
        await NewStore().SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = 200, Name = "Replacement" }, "eng");

        var loaded = await NewStore().GetSeriesAggregateAsync(200, "eng");

        loaded!.Name.Should().Be("Original");
    }

    [Test]
    public async Task GetSeriesAggregate_ReturnsNull_WhenMissing()
    {
        (await NewStore().GetSeriesAggregateAsync(999, "eng")).Should().BeNull();
    }

    [Test]
    public async Task GetSeriesAggregate_ReturnsNull_WhenPayloadIsCorrupt()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
            {
                TvdbId = 300,
                Language = "eng",
                Name = "Corrupt",
                Payload = "{ this is not json",
                RetrievedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        (await NewStore().GetSeriesAggregateAsync(300, "eng")).Should().BeNull();
    }

    [Test]
    public async Task SaveThenGet_MovieAggregate_RoundTripsTheGraph()
    {
        var aggregate = new MovieAggregate
        {
            TvdbId = 287533,
            Name = "Oppenheimer",
            Status = new MovieStatus { Name = "Released", KeepUpdated = true },
            Genres = ["Drama", "History"],
            Characters = [new Character { Id = 1, Name = "J. Robert Oppenheimer", PersonName = "Cillian Murphy" }]
        };

        await NewStore().SaveMovieAggregateAsync(aggregate, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(287533, "eng");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Oppenheimer");
        loaded.Status!.Name.Should().Be("Released");
        loaded.Genres.Should().BeEquivalentTo(["Drama", "History"]);
        loaded.Characters.Should().ContainSingle().Which.PersonName.Should().Be("Cillian Murphy");
    }

    [Test]
    public async Task SaveMovieAggregate_IsInsertOnly_DoesNotOverwriteExisting()
    {
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 200, Name = "Original" }, "eng");
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 200, Name = "Replacement" }, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(200, "eng");

        loaded!.Name.Should().Be("Original");
    }

    [Test]
    public async Task GetMovieAggregate_ReturnsNull_WhenMissing()
    {
        (await NewStore().GetMovieAggregateAsync(999, "eng")).Should().BeNull();
    }

    [Test]
    public async Task GetMovieAggregate_ReturnsNull_WhenPayloadIsCorrupt()
    {
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedMovieAggregates.Add(new CachedMovieAggregateEntity
            {
                TvdbId = 300,
                Language = "eng",
                Name = "Corrupt",
                Payload = "{ this is not json",
                RetrievedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        (await NewStore().GetMovieAggregateAsync(300, "eng")).Should().BeNull();
    }

    [Test]
    public async Task UpsertMovieAggregate_OverwritesExisting()
    {
        await NewStore().SaveMovieAggregateAsync(new MovieAggregate { TvdbId = 400, Name = "Working Title" }, "eng");
        await NewStore().UpsertMovieAggregateAsync(new MovieAggregate { TvdbId = 400, Name = "Final Title" }, "eng");

        var loaded = await NewStore().GetMovieAggregateAsync(400, "eng");

        loaded!.Name.Should().Be("Final Title");
    }

    [Test]
    public async Task GetMovieAggregatesNeedingRefresh_PicksStaleKeepUpdated_ButNotFreshOrUnflagged()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedMovieAggregates.AddRange(
                new CachedMovieAggregateEntity { TvdbId = 1, Language = "eng", Name = "Stale, keep-updated", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddHours(-13) },
                new CachedMovieAggregateEntity { TvdbId = 2, Language = "eng", Name = "Fresh, keep-updated", Payload = "{}", KeepUpdated = true, RetrievedUtc = now.AddHours(-1) },
                new CachedMovieAggregateEntity { TvdbId = 3, Language = "eng", Name = "Stale, not keep-updated", Payload = "{}", KeepUpdated = false, RetrievedUtc = now.AddHours(-13) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetMovieAggregatesNeedingRefreshAsync(
            staleBeforeUtc: now.AddHours(-12), limit: 10);

        due.Should().ContainSingle().Which.TvdbId.Should().Be(1);
    }

    [Test]
    public async Task SaveThenGet_SeriesExtended_And_EpisodeExtended_RoundTrip()
    {
        var store = NewStore();
        await store.SaveSeriesExtendedAsync(new Series { Id = 400, Name = "Extended", Slug = "ext" });
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 4001, SeriesId = 400, Name = "Ep One" });

        var readStore = NewStore();

        var series = await readStore.GetSeriesExtendedAsync(400);
        series!.Name.Should().Be("Extended");
        series.Slug.Should().Be("ext");

        var episode = await readStore.GetEpisodeExtendedAsync(4001);
        episode!.Name.Should().Be("Ep One");
        episode.SeriesId.Should().Be(400);
    }

    [Test]
    public async Task SaveEpisodeExtended_WithNullId_IsNoOp()
    {
        await NewStore().SaveEpisodeExtendedAsync(new Episode { Id = null, Name = "No Id" });

        await using var read = new AppDbContext(_dbOptions);
        (await read.CachedEpisodesExtended.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task UpsertEpisodeExtended_OverwritesExisting()
    {
        await NewStore().SaveEpisodeExtendedAsync(new Episode { Id = 5001, SeriesId = 500, Name = "TBA" });
        await NewStore().UpsertEpisodeExtendedAsync(new Episode { Id = 5001, SeriesId = 500, Name = "The Real Title" });

        var loaded = await NewStore().GetEpisodeExtendedAsync(5001);

        loaded!.Name.Should().Be("The Real Title");
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_PicksStaleAndTba_ButNotFresh()
    {
        var now = DateTime.UtcNow;

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedEpisodesExtended.AddRange(
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 1, Name = "Old", Payload = "{}", RetrievedUtc = now.AddDays(-40) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 2, Name = "tba", Payload = "{}", RetrievedUtc = now.AddHours(-13) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 3, Name = "TBA", Payload = "{}", RetrievedUtc = now.AddHours(-3) },
                new CachedEpisodeExtendedEntity { EpisodeTvdbId = 4, Name = "Fresh", Payload = "{}", RetrievedUtc = now.AddDays(-2) });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc: now.AddDays(-30),
            tbaStaleBeforeUtc: now.AddHours(-12),
            imageChaseBeforeUtc: now.AddHours(-12),
            today: DateOnly.FromDateTime(now),
            maxImageChaseAttempts: 5,
            limit: 10);

        // 1: older than 30d. 2: still "TBA" and older than 12h.
        // 3: "TBA" but only 3h old. 4: fresh and titled.
        due.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task GetEpisodesNeedingRefresh_ChasesAiredEpisodesMissingImage_ButRespectsCapAndBackoff()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.CachedEpisodesExtended.AddRange(
                // 10: aired, no image, under the attempt cap, refreshed long enough ago — due.
                new CachedEpisodeExtendedEntity
                {
                    EpisodeTvdbId = 10, Name = "Aired No Image", Payload = "{}",
                    Aired = today.AddDays(-1), HasImage = false, RefreshAttempts = 2,
                    RetrievedUtc = now.AddHours(-13)
                },
                // 11: aired, no image, but already at the attempt cap — not due.
                new CachedEpisodeExtendedEntity
                {
                    EpisodeTvdbId = 11, Name = "Given Up", Payload = "{}",
                    Aired = today.AddDays(-5), HasImage = false, RefreshAttempts = 5,
                    RetrievedUtc = now.AddHours(-13)
                },
                // 12: aired, no image, under the cap, but refreshed too recently — not due.
                new CachedEpisodeExtendedEntity
                {
                    EpisodeTvdbId = 12, Name = "Too Soon", Payload = "{}",
                    Aired = today.AddDays(-1), HasImage = false, RefreshAttempts = 1,
                    RetrievedUtc = now.AddHours(-1)
                },
                // 13: not aired yet — not due even though it's imageless.
                new CachedEpisodeExtendedEntity
                {
                    EpisodeTvdbId = 13, Name = "Future", Payload = "{}",
                    Aired = today.AddDays(1), HasImage = false, RefreshAttempts = 0,
                    RetrievedUtc = now.AddHours(-13)
                },
                // 14: aired, already has an image — not due.
                new CachedEpisodeExtendedEntity
                {
                    EpisodeTvdbId = 14, Name = "Has Image", Payload = "{}",
                    Aired = today.AddDays(-1), HasImage = true, RefreshAttempts = 0,
                    RetrievedUtc = now.AddHours(-13)
                });
            await seed.SaveChangesAsync();
        }

        var due = await NewStore().GetEpisodesNeedingRefreshAsync(
            staleBeforeUtc: now.AddDays(-30),
            tbaStaleBeforeUtc: now.AddHours(-12),
            imageChaseBeforeUtc: now.AddHours(-12),
            today: today,
            maxImageChaseAttempts: 5,
            limit: 10);

        due.Should().BeEquivalentTo(new[] { 10 });
    }

    [Test]
    public async Task UpsertEpisodeExtended_TracksImageAndAttempts()
    {
        var store = NewStore();

        await store.SaveEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = null });

        await store.UpsertEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = null });

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 6001);
            row.HasImage.Should().BeFalse();
            row.RefreshAttempts.Should().Be(1);
            row.Aired.Should().Be(new DateOnly(2026, 1, 1));
        }

        await store.UpsertEpisodeExtendedAsync(new Episode { Id = 6001, Name = "Ep", Aired = "2026-01-01", Image = "https://example.test/still.jpg" });

        await using (var db = new AppDbContext(_dbOptions))
        {
            var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 6001);
            row.HasImage.Should().BeTrue();
            row.RefreshAttempts.Should().Be(0);
        }
    }

    [Test]
    public async Task BackfillEpisodeImagesFromAggregateAsync_PatchesMissingImages_AndResetsAttempts()
    {
        var store = NewStore();
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 7001, SeriesId = 700, Name = "No Still", Image = null });
        await store.UpsertEpisodeExtendedAsync(new Episode { Id = 7001, SeriesId = 700, Name = "No Still", Image = null });

        var aggregate = new SeriesAggregate
        {
            TvdbId = 700,
            Name = "Backfill Show",
            Episodes = [new EpisodeSummary { Id = 7001, Name = "No Still", Image = "https://example.test/screencap.jpg" }]
        };

        var patched = await store.BackfillEpisodeImagesFromAggregateAsync(aggregate);

        patched.Should().ContainSingle();
        patched[0].Image.Should().Be("https://example.test/screencap.jpg");

        var loaded = await store.GetEpisodeExtendedAsync(7001);
        loaded!.Image.Should().Be("https://example.test/screencap.jpg");

        await using var db = new AppDbContext(_dbOptions);
        var row = await db.CachedEpisodesExtended.SingleAsync(x => x.EpisodeTvdbId == 7001);
        row.HasImage.Should().BeTrue();
        row.RefreshAttempts.Should().Be(0);
    }

    [Test]
    public async Task BackfillEpisodeImagesFromAggregateAsync_SkipsRows_ThatAlreadyHaveAnImage()
    {
        var store = NewStore();
        await store.SaveEpisodeExtendedAsync(new Episode { Id = 8001, SeriesId = 800, Name = "Has Still", Image = "https://example.test/original.jpg" });

        var aggregate = new SeriesAggregate
        {
            TvdbId = 800,
            Name = "No Backfill Needed",
            Episodes = [new EpisodeSummary { Id = 8001, Name = "Has Still", Image = "https://example.test/different.jpg" }]
        };

        var patched = await store.BackfillEpisodeImagesFromAggregateAsync(aggregate);

        patched.Should().BeEmpty();

        var loaded = await store.GetEpisodeExtendedAsync(8001);
        loaded!.Image.Should().Be("https://example.test/original.jpg");
    }
}
