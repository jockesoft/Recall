using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Queries;

/// <summary>
/// What a Discover page view asks the database about the user: everything
/// they already know (<see cref="DiscoverUserRepository.GetKnownTitlesAsync"/>),
/// in one statement, with one user's episode watches (the one large table in
/// it) found through its index.
/// </summary>
[TestFixture]
public sealed class DiscoverQueryTests : PostgresFixture
{
    private const string UserSeriesIndex = "IX_episode_watch_user_id_series_tvdb_id";

    [SetUp]
    public Task EmptyWatchesAsync() => ExecuteAsync("TRUNCATE episode_watch;");

    [Test]
    public async Task KnownTitles_Should_BeOneStatement_ThatReadsOneUsersWatchesThroughTheIndex()
    {
        // 60 users with 40 series of 25 watched episodes each (60,000 rows).
        var user = await SeedUserAsync();
        for (var i = 0; i < 59; i++)
            await SeedUserAsync();

        await ExecuteAsync(
            """
            INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 900000 + s, 9000000 + s * 100 + e, now(), now(), now()
            FROM app_user u, generate_series(1, 40) AS s, generate_series(1, 25) AS e;
            ANALYZE episode_watch;
            """);

        var capture = new CommandCapture();
        await using var db = NewContext(capture);
        var known = await new DiscoverUserRepository(db, TimeProvider.System, NullLogger<DiscoverUserRepository>.Instance)
            .GetKnownTitlesAsync(user);

        known.SeriesIds.Should().HaveCount(40, "one id per series, however many episodes were watched");
        known.MovieIds.Should().BeEmpty();
        capture.Commands.Should().ContainSingle("the page asks once for everything the user knows");

        var (sql, parameterName) = capture.Commands.Single();
        sql.Should().Contain("UNION");

        var plan = await ScalarAsync<string>("EXPLAIN (ANALYZE, FORMAT JSON) " + sql.Replace(parameterName, "$1"), user);
        TestContext.Out.WriteLine(plan);

        plan.Should().Contain(UserSeriesIndex, "one user's watches are found through the index that leads with user_id");
        ScansOf(plan, "episode_watch").Should().NotContain("Seq Scan", "every user's watches must not be read");
    }

    /// <summary>The node types that read <paramref name="table"/> in a JSON plan.</summary>
    private static IReadOnlyList<string> ScansOf(string plan, string table)
    {
        var found = new List<string>();

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == table)
                found.Add(node.GetProperty("Node Type").GetString()!);

            if (node.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child);
        }

        using var document = JsonDocument.Parse(plan);
        Walk(document.RootElement[0].GetProperty("Plan"));
        return found;
    }
}
