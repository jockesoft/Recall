using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Queries;

/// <summary>
/// The grouped query behind the "Continue watching" order
/// (<see cref="EpisodeWatchRepository.GetLastWatchedUtcBySeriesAsync"/>): the
/// latest watch per series for one user, and the plan PostgreSQL gives it.
/// </summary>
[TestFixture]
public sealed class ContinueWatchingQueryTests : PostgresFixture
{
    private const string UserSeriesIndex = "IX_episode_watch_user_id_series_tvdb_id";

    [SetUp]
    public Task EmptyWatchesAsync() => ExecuteAsync("TRUNCATE episode_watch;");

    [Test]
    public async Task LastWatched_Should_BeTheLatestWatchPerSeries_ForThatUserOnly()
    {
        var user = await SeedUserAsync();
        var someoneElse = await SeedUserAsync();
        var (first, second, untouched) = (NextId(), NextId(), NextId());

        await WatchAsync(user, first, "2026-09-01 20:00:00+00");
        await WatchAsync(user, first, "2026-09-20 21:30:00+00");
        await WatchAsync(user, first, "2026-09-10 20:00:00+00");
        await WatchAsync(user, second, "2026-08-05 19:00:00+00");
        // Later than anything of the user's, on the same series: must not leak in.
        await WatchAsync(someoneElse, first, "2026-09-30 23:00:00+00");
        await WatchAsync(someoneElse, untouched, "2026-09-30 23:00:00+00");

        await using var db = NewContext();
        var lastWatched = await new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance)
            .GetLastWatchedUtcBySeriesAsync(user);

        lastWatched.Should().HaveCount(2, "one entry per series the user has watched anything of");
        lastWatched[first].Should().Be(new DateTime(2026, 9, 20, 21, 30, 0, DateTimeKind.Utc));
        lastWatched[second].Should().Be(new DateTime(2026, 8, 5, 19, 0, 0, DateTimeKind.Utc));
        lastWatched.Should().NotContainKey(untouched);
    }

    [Test]
    public async Task LastWatched_Should_BeEmpty_ForAUserWhoHasWatchedNothing()
    {
        var user = await SeedUserAsync();
        await WatchAsync(await SeedUserAsync(), NextId(), "2026-09-30 23:00:00+00");

        await using var db = NewContext();
        var lastWatched = await new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance)
            .GetLastWatchedUtcBySeriesAsync(user);

        lastWatched.Should().BeEmpty();
    }

    [Test]
    public async Task LastWatched_Should_RunAsOneQuery_ThatUsesTheUserSeriesIndex()
    {
        // Enough rows that reading the whole table is the wrong plan: 60 users
        // with 40 series of 25 watched episodes each (60,000 rows).
        var user = await SeedUserAsync();
        for (var i = 0; i < 59; i++)
            await SeedUserAsync();

        await ExecuteAsync(
            """
            INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 900000 + s, 9000000 + s * 100 + e,
                   now() - (s * 25 + e) * interval '1 hour', now(), now()
            FROM app_user u, generate_series(1, 40) AS s, generate_series(1, 25) AS e;
            ANALYZE episode_watch;
            """);

        var capture = new CommandCapture();
        await using var db = NewContext(capture);
        var lastWatched = await new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance)
            .GetLastWatchedUtcBySeriesAsync(user);

        lastWatched.Should().HaveCount(40);
        capture.Commands.Should().ContainSingle("the page gets every series' activity in one grouped query");

        var (sql, parameterName) = capture.Commands.Single();
        sql.Should().Contain("GROUP BY").And.Contain("max(", "the grouping and the maximum happen in the database");

        // The same statement, explained with the same parameter.
        var plan = await ScalarAsync<string>("EXPLAIN (ANALYZE, FORMAT JSON) " + sql.Replace(parameterName, "$1"), user);
        TestContext.Out.WriteLine(plan);

        plan.Should().Contain(UserSeriesIndex, "the (user_id, series_tvdb_id) index is what finds one user's watches");
        plan.Should().NotContain("Seq Scan", "the query must not read every user's watches");
    }

    private Task WatchAsync(Guid userId, int seriesId, string watchedUtc) =>
        ExecuteAsync(
            $"""
             INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
             VALUES (gen_random_uuid(), '{userId}', {seriesId}, {NextId()}, '{watchedUtc}', now(), now())
             """);

    /// <summary>Records the SQL EF sends, with the name of its (single) parameter.</summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<(string Sql, string ParameterName)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var name = command.Parameters.Count == 1 ? command.Parameters[0].ParameterName : string.Empty;
            Commands.Add((command.CommandText, name.StartsWith('@') ? name : "@" + name));
            return ValueTask.FromResult(result);
        }
    }
}
