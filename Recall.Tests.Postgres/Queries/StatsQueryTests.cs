using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Queries;

/// <summary>
/// The three queries a Stats page load sends (every episode watch, every movie
/// watch and the rating counts of one user): what they return on PostgreSQL,
/// that each is one statement, and that the plan finds the user's rows through
/// an index that leads with <c>user_id</c> instead of reading the table.
/// </summary>
[TestFixture]
public sealed class StatsQueryTests : PostgresFixture
{
    [SetUp]
    public Task EmptyTablesAsync() => ExecuteAsync("TRUNCATE episode_watch, user_movie_watch, user_rating;");

    [Test]
    public async Task EpisodeWatches_Should_ComeBackWithTheirSource_ForThatUserOnly()
    {
        var user = await SeedUserAsync();
        var someoneElse = await SeedUserAsync();
        var series = NextId();
        var (single, bulk, legacy) = (NextId(), NextId(), NextId());

        await using (var db = NewContext())
        {
            var repository = new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance);
            await repository.MarkWatchedRangeAsync(user, series, [bulk, single], WatchSource.Bulk, clickedEpisodeTvdbId: single);
            await repository.MarkWatchedAsync(someoneElse, series, NextId());
        }

        // A row written without a source, as every row was before the column existed.
        await ExecuteAsync(
            $"""
             INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
             VALUES (gen_random_uuid(), '{user}', {series}, {legacy}, '2026-08-01 20:00:00+00', now(), now())
             """);

        await using var read = NewContext();
        var watches = await new EpisodeWatchRepository(read, NullLogger<EpisodeWatchRepository>.Instance).GetWatchesAsync(user);

        watches.ToDictionary(w => w.EpisodeTvdbId, w => w.Source).Should().BeEquivalentTo(new Dictionary<int, WatchSource>
        {
            [single] = WatchSource.Single,
            [bulk] = WatchSource.Bulk,
            [legacy] = WatchSource.Unknown
        });
        watches.Should().OnlyContain(w => w.SeriesTvdbId == series);
        watches.Single(w => w.EpisodeTvdbId == legacy).WatchedUtc
            .Should().Be(new DateTime(2026, 8, 1, 20, 0, 0, DateTimeKind.Utc));
        (await ScalarAsync<string>($"SELECT source FROM episode_watch WHERE episode_tvdb_id = {single}"))
            .Should().Be("Single", "the source is stored as its name");
    }

    [Test]
    public async Task MovieWatches_Should_ComeBackWithTheirSource()
    {
        var user = await SeedUserAsync();
        var (byHand, imported) = (NextId(), NextId());

        await using (var db = NewContext())
        {
            var repository = new MovieWatchRepository(db, NullLogger<MovieWatchRepository>.Instance);
            await repository.ToggleAsync(user, byHand, WatchSource.Single);
            await repository.ToggleAsync(user, imported, WatchSource.Import);
            await repository.ToggleAsync(await SeedUserAsync(), imported, WatchSource.Single);
        }

        await using var read = NewContext();
        var watches = await new MovieWatchRepository(read, NullLogger<MovieWatchRepository>.Instance).GetWatchedMoviesAsync(user);

        watches.ToDictionary(w => w.MovieTvdbId, w => w.Source).Should().BeEquivalentTo(new Dictionary<int, WatchSource>
        {
            [byHand] = WatchSource.Single,
            [imported] = WatchSource.Import
        });
    }

    [Test]
    public async Task RatingCounts_Should_CountEveryKindOfTitle_PerValue_ForThatUserOnly()
    {
        var user = await SeedUserAsync();
        var someoneElse = await SeedUserAsync();
        await RateAsync(user, "Series", 9);
        await RateAsync(user, "Movie", 9);
        await RateAsync(user, "Episode", 9);
        await RateAsync(user, "Movie", 4);
        await RateAsync(someoneElse, "Movie", 4);
        await RateAsync(someoneElse, "Movie", 1);

        await using var db = NewContext();
        var counts = await new RatingRepository(db, NullLogger<RatingRepository>.Instance).GetValueCountsAsync(user);

        counts.Should().BeEquivalentTo(new Dictionary<int, int> { [9] = 3, [4] = 1 });
    }

    [Test]
    public async Task TheThreeStatsQueries_Should_EachBeOneStatement_ThatFindsTheUsersRowsByIndex()
    {
        // Enough rows that reading a whole table is the wrong plan: 60 users
        // with 1,000 episode watches, 200 movie watches and 100 ratings each.
        var user = await SeedUserAsync();
        for (var i = 0; i < 59; i++)
            await SeedUserAsync();

        await ExecuteAsync(
            """
            INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, source, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 900000 + s, 9000000 + s * 100 + e,
                   now() - (s * 25 + e) * interval '1 hour',
                   CASE WHEN e % 3 = 0 THEN 'Bulk' ELSE 'Single' END, now(), now()
            FROM app_user u, generate_series(1, 40) AS s, generate_series(1, 25) AS e;

            INSERT INTO user_movie_watch (id, user_id, movie_tvdb_id, watched_utc, source, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 800000 + m, now() - m * interval '1 day',
                   CASE WHEN m % 2 = 0 THEN 'Import' ELSE 'Single' END, now(), now()
            FROM app_user u, generate_series(1, 200) AS m;

            INSERT INTO user_rating (id, user_id, target_type, target_tvdb_id, series_tvdb_id, value, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 'Movie', 800000 + m, 800000 + m, 1 + m % 10, now(), now()
            FROM app_user u, generate_series(1, 100) AS m;

            ANALYZE episode_watch;
            ANALYZE user_movie_watch;
            ANALYZE user_rating;
            """);

        // ---- every episode watch of the user ----
        var episodes = new CommandCapture();
        await using (var db = NewContext(episodes))
        {
            var watches = await new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance).GetWatchesAsync(user);
            watches.Should().HaveCount(1000);
            watches.Count(w => w.Source == WatchSource.Bulk).Should().Be(40 * 8);
        }

        await AssertOneIndexedStatementAsync(episodes, user, "IX_episode_watch_user_id_");

        // ---- every movie watch of the user ----
        var movies = new CommandCapture();
        await using (var db = NewContext(movies))
        {
            var watches = await new MovieWatchRepository(db, NullLogger<MovieWatchRepository>.Instance).GetWatchedMoviesAsync(user);
            watches.Should().HaveCount(200);
        }

        await AssertOneIndexedStatementAsync(movies, user, "IX_user_movie_watch_user_id_movie_tvdb_id");

        // ---- the user's ratings, counted per value ----
        var ratings = new CommandCapture();
        await using (var db = NewContext(ratings))
        {
            var counts = await new RatingRepository(db, NullLogger<RatingRepository>.Instance).GetValueCountsAsync(user);
            counts.Should().HaveCount(10);
            counts.Values.Sum().Should().Be(100);
        }

        ratings.Commands.Single().Sql.Should().Contain("GROUP BY", "the counting happens in the database");
        await AssertOneIndexedStatementAsync(ratings, user, "IX_user_rating_user_id_");
    }

    /// <summary>Explains the one statement that was captured, with the same parameter, and checks the plan.</summary>
    private async Task AssertOneIndexedStatementAsync(CommandCapture capture, Guid user, string indexPrefix)
    {
        capture.Commands.Should().ContainSingle("each stats query is one round trip");
        var (sql, parameterName) = capture.Commands.Single();

        var plan = await ScalarAsync<string>("EXPLAIN (ANALYZE, FORMAT JSON) " + sql.Replace(parameterName, "$1"), user);
        TestContext.Out.WriteLine(plan);

        plan.Should().Contain(indexPrefix, "an index that leads with user_id is what finds one user's rows");
        plan.Should().NotContain("Seq Scan", "the query must not read every user's rows");
    }

    private Task RateAsync(Guid userId, string targetType, int value)
    {
        var target = NextId();
        return ExecuteAsync(
            $"""
             INSERT INTO user_rating (id, user_id, target_type, target_tvdb_id, series_tvdb_id, value, created_utc, updated_utc)
             VALUES (gen_random_uuid(), '{userId}', '{targetType}', {target}, {target}, {value}, now(), now())
             """);
    }
}
