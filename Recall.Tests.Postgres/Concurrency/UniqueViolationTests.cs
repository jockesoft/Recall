using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Infrastructure.Persistence.TvdbCache;

namespace Recall.Tests.Postgres.Concurrency;

/// <summary>
/// Every <c>catch … PostgresException { SqlState: UniqueViolation }</c> block in
/// the persistence layer. SQLite raises a different exception for the same
/// situation, so these branches only ever run against PostgreSQL. Each test
/// lets a competing request insert the row first (see
/// <see cref="CompetingWriteInterceptor"/>) and checks that the loser of the
/// race ends in the documented state and leaves its context usable.
/// </summary>
[TestFixture]
public sealed class UniqueViolationTests : PostgresFixture
{
    [Test]
    public async Task EpisodeWatch_MarkWatched_Should_AcceptThatTheEpisodeIsAlreadyWatched()
    {
        var user = await SeedUserAsync();
        var (seriesId, episodeId) = (NextId(), NextId());
        var race = Competing(db => db.EpisodeWatches.Add(new EpisodeWatchEntity
        {
            Id = Guid.NewGuid(), UserId = user, SeriesTvdbId = seriesId, EpisodeTvdbId = episodeId, WatchedUtc = DateTime.UtcNow
        }));
        await using var db = NewContext(race);
        var repository = new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance);

        await repository.MarkWatchedAsync(user, seriesId, episodeId);

        race.Fired.Should().BeTrue();
        (await CountAsync(x => x.EpisodeWatches.CountAsync(w => w.UserId == user && w.EpisodeTvdbId == episodeId)))
            .Should().Be(1);
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task MovieWatch_Toggle_Should_ReportWatched_WhenTheMarkAlreadyExists()
    {
        var user = await SeedUserAsync();
        var movieId = NextId();
        var race = Competing(db => db.UserMovieWatches.Add(new UserMovieWatchEntity
        {
            Id = Guid.NewGuid(), UserId = user, MovieTvdbId = movieId, WatchedUtc = DateTime.UtcNow
        }));
        await using var db = NewContext(race);
        var repository = new MovieWatchRepository(db, NullLogger<MovieWatchRepository>.Instance);

        var watched = await repository.ToggleAsync(user, movieId, WatchSource.Single);

        race.Fired.Should().BeTrue();
        watched.Should().BeTrue();
        (await CountAsync(x => x.UserMovieWatches.CountAsync(w => w.UserId == user && w.MovieTvdbId == movieId)))
            .Should().Be(1);
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task Like_Toggle_Should_ReportLiked_WhenTheLikeAlreadyExists()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var race = Competing(db => db.UserLikes.Add(new UserLikeEntity
        {
            Id = Guid.NewGuid(), UserId = user, TargetType = LikeTargetType.Series, TargetTvdbId = seriesId, SeriesTvdbId = seriesId
        }));
        await using var db = NewContext(race);
        var repository = new LikeRepository(db, NullLogger<LikeRepository>.Instance);

        var liked = await repository.ToggleAsync(user, LikeTargetType.Series, seriesId, seriesId);

        race.Fired.Should().BeTrue();
        liked.Should().BeTrue();
        (await CountAsync(x => x.UserLikes.CountAsync(l => l.UserId == user && l.TargetTvdbId == seriesId)))
            .Should().Be(1);
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task Rating_Rate_Should_OverwriteTheCompetingRating_InsteadOfLosingItsOwn()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var race = Competing(db => db.UserRatings.Add(new UserRatingEntity
        {
            Id = Guid.NewGuid(), UserId = user, TargetType = RatingTargetType.Series, TargetTvdbId = seriesId, SeriesTvdbId = seriesId, Value = 3
        }));
        await using var db = NewContext(race);
        var repository = new RatingRepository(db, NullLogger<RatingRepository>.Instance);

        await repository.RateAsync(user, RatingTargetType.Series, seriesId, seriesId, value: 9);

        race.Fired.Should().BeTrue();
        await using var verify = NewContext();
        var ratings = await verify.UserRatings
            .Where(r => r.UserId == user && r.TargetTvdbId == seriesId)
            .ToListAsync();
        ratings.Should().ContainSingle().Which.Value.Should().Be(9);
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task TrackedSeries_Add_Should_ReturnFalse_WhenTheSeriesIsAlreadyTracked()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var race = Competing(db => db.TrackedSeries.Add(new TrackedSeriesEntity
        {
            Id = Guid.NewGuid(), UserId = user, TvdbId = seriesId, Name = "Added first"
        }));
        await using var db = NewContext(race);
        var repository = new TrackedSeriesRepository(db, NullLogger<TrackedSeriesRepository>.Instance);

        var added = await repository.AddAsync(
            new TrackedSeries { Id = Guid.NewGuid(), UserId = user, TvdbId = seriesId, Name = "Added second" });

        race.Fired.Should().BeTrue();
        added.Should().BeFalse();
        (await repository.GetByUserAsync(user)).Should().ContainSingle().Which.Name.Should().Be("Added first");
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task TrackedMovie_Add_Should_ReturnFalse_WhenTheMovieIsAlreadyOnTheWatchlist()
    {
        var user = await SeedUserAsync();
        var movieId = NextId();
        var race = Competing(db => db.TrackedMovies.Add(new TrackedMovieEntity
        {
            Id = Guid.NewGuid(), UserId = user, TvdbId = movieId, Name = "Added first"
        }));
        await using var db = NewContext(race);
        var repository = new TrackedMovieRepository(db, NullLogger<TrackedMovieRepository>.Instance);

        var added = await repository.AddAsync(user, movieId, "Added second");

        race.Fired.Should().BeTrue();
        added.Should().BeFalse();
        (await repository.GetByUserAsync(user)).Should().ContainSingle().Which.Name.Should().Be("Added first");
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task AppUser_UpdateUsername_Should_ReturnTaken_WhenAnotherUserClaimsTheNameFirst()
    {
        var name = $"wanted-{Guid.NewGuid():N}";
        var user = await SeedUserAsync();
        var race = Competing(db => db.AppUsers.Add(new AppUserEntity
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com", Username = name
        }));
        await using var db = NewContext(race);
        var repository = new AppUserRepository(db);

        var result = await repository.UpdateUsernameAsync(user, name);

        race.Fired.Should().BeTrue();
        result.Should().Be(UsernameUpdateResult.Taken);
        await using var verify = NewContext();
        (await verify.AppUsers.SingleAsync(u => u.Id == user)).Username.Should().Be($"user-{user:N}");
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task Notification_AddNewEpisode_Should_ReturnFalse_AndWriteNothing_WhenAnEpisodeWasAlreadyClaimed()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        int[] episodes = [NextId(), NextId(), NextId()];
        var race = Competing(db => db.NotifiedEpisodes.Add(new NotifiedEpisodeEntity
        {
            Id = Guid.NewGuid(), UserId = user, SeriesTvdbId = seriesId, EpisodeTvdbId = episodes[1], CreatedUtc = DateTime.UtcNow
        }));
        await using var db = NewContext(race);
        var repository = new NotificationRepository(db, NullLogger<NotificationRepository>.Instance);

        var added = await repository.AddNewEpisodeNotificationAsync(
            user, seriesId, episodes[0], episodes.Length, "New episodes", null, episodes);

        race.Fired.Should().BeTrue();
        added.Should().BeFalse();

        // All or nothing: the notification and the other two ledger rows were
        // part of the same save and must not have been written either.
        (await CountAsync(x => x.Notifications.CountAsync(n => n.UserId == user))).Should().Be(0);
        (await repository.GetAlreadyNotifiedEpisodeIdsAsync(user, episodes)).Should().Equal(episodes[1]);
        await ContextShouldStillSaveAsync(db);
    }

    [Test]
    public async Task SnapshotStore_SaveSeriesAggregate_Should_KeepTheSnapshotStoredFirst()
    {
        var id = NextId();
        var race = Competing(db => db.CachedSeriesAggregates.Add(new CachedSeriesAggregateEntity
        {
            TvdbId = id, Language = "eng", Name = "Stored first", Payload = """{"tvdbId":1,"name":"Stored first"}""", RetrievedUtc = DateTime.UtcNow
        }));
        var store = NewSnapshotStore(race);

        await store.SaveSeriesAggregateAsync(new SeriesAggregate { TvdbId = id, Name = "Stored second" }, "eng");

        race.Fired.Should().BeTrue();
        (await store.GetSeriesAggregateAsync(id, "eng"))!.Name.Should().Be("Stored first");
    }

    [Test]
    public async Task SnapshotStore_SaveMovieAggregate_Should_KeepTheSnapshotStoredFirst()
    {
        var id = NextId();
        var race = Competing(db => db.CachedMovieAggregates.Add(new CachedMovieAggregateEntity
        {
            TvdbId = id, Language = "eng", Name = "Stored first", Payload = """{"tvdbId":1,"name":"Stored first"}""", RetrievedUtc = DateTime.UtcNow
        }));
        var store = NewSnapshotStore(race);

        await store.SaveMovieAggregateAsync(new MovieAggregate { TvdbId = id, Name = "Stored second" }, "eng");

        race.Fired.Should().BeTrue();
        (await store.GetMovieAggregateAsync(id, "eng"))!.Name.Should().Be("Stored first");
    }

    [Test]
    public async Task SnapshotStore_SaveSeriesExtended_Should_KeepTheSnapshotStoredFirst()
    {
        var id = NextId();
        var race = Competing(db => db.CachedSeriesExtended.Add(new CachedSeriesExtendedEntity
        {
            TvdbId = id, Name = "Stored first", Payload = """{"id":1,"name":"Stored first"}""", RetrievedUtc = DateTime.UtcNow
        }));
        var store = NewSnapshotStore(race);

        await store.SaveSeriesExtendedAsync(new Series { Id = id, Name = "Stored second" });

        race.Fired.Should().BeTrue();
        (await store.GetSeriesExtendedAsync(id))!.Name.Should().Be("Stored first");
    }

    [Test]
    public async Task SnapshotStore_SaveEpisodeExtended_Should_KeepTheSnapshotStoredFirst()
    {
        var id = NextId();
        var race = Competing(db => db.CachedEpisodesExtended.Add(new CachedEpisodeExtendedEntity
        {
            EpisodeTvdbId = id, Name = "Stored first", Payload = """{"id":1,"name":"Stored first"}""", RetrievedUtc = DateTime.UtcNow
        }));
        var store = NewSnapshotStore(race);

        await store.SaveEpisodeExtendedAsync(new Episode { Id = id, Name = "Stored second" });

        race.Fired.Should().BeTrue();
        (await store.GetEpisodeExtendedAsync(id))!.Name.Should().Be("Stored first");
    }

    [Test]
    public async Task AnyOtherDatabaseError_Should_StillPropagate()
    {
        // The catch blocks filter on the unique-violation code only. A foreign
        // key violation (a user that does not exist) must not be swallowed.
        await using var db = NewContext();
        var repository = new LikeRepository(db, NullLogger<LikeRepository>.Instance);

        var act = () => repository.ToggleAsync(Guid.NewGuid(), LikeTargetType.Series, NextId(), NextId());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Test]
    public async Task Digest_Record_Should_QueueNothing_WhenAnotherRunRecordedTheWeekFirst()
    {
        var user = await SeedUserAsync();
        var week = new DateOnly(2026, 10, 2);
        var race = Competing(db => db.DigestSends.Add(new DigestSendEntity
        {
            Id = Guid.NewGuid(), UserId = user, PeriodStart = week, Status = DigestSendStatus.Queued, CreatedUtc = DateTime.UtcNow
        }));
        await using var db = NewContext(race);
        var address = $"{user:N}@example.com";

        var recorded = await new DigestRepository(db).RecordAsync(user, week, DigestSendStatus.Queued, new Recall.Web.Domain.Internal.OutboundEmail
        {
            Id = Guid.NewGuid(), ToAddress = address, Subject = "Your week on Recall", Body = "text"
        });

        race.Fired.Should().BeTrue();
        recorded.Should().BeFalse("the other run got there first");
        (await CountAsync(x => x.DigestSends.CountAsync(d => d.UserId == user && d.PeriodStart == week))).Should().Be(1);
        (await CountAsync(x => x.Emails.CountAsync(e => e.ToAddress == address)))
            .Should().Be(0, "the ledger row and the email are one transaction: no second digest is queued");
        await ContextShouldStillSaveAsync(db);
    }

    private CompetingWriteInterceptor Competing(Action<AppDbContext> add) =>
        new(async () =>
        {
            await using var competitor = NewContext();
            add(competitor);
            await competitor.SaveChangesAsync();
        });

    private TvdbSnapshotStore NewSnapshotStore(CompetingWriteInterceptor race) =>
        new(NewFactory(race), NullLogger<TvdbSnapshotStore>.Instance);

    private async Task<int> CountAsync(Func<AppDbContext, Task<int>> count)
    {
        await using var db = NewContext();
        return await count(db);
    }

    /// <summary>
    /// The context is scoped to the request, so whatever the handler does next
    /// saves through it again. That must not retry the insert that just lost.
    /// </summary>
    private static async Task ContextShouldStillSaveAsync(AppDbContext db)
    {
        var act = () => db.SaveChangesAsync();
        await act.Should().NotThrowAsync("the row that lost the race must no longer be pending in the context");
    }
}
