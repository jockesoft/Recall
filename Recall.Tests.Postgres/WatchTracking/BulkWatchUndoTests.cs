using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Tests.Postgres.Queries;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.WatchTracking;

/// <summary>
/// An undoable "mark watched" is undone by deleting the rows that carry its
/// batch id (<c>episode_watch.batch_id</c>). Until 2026-11-13 the undo matched
/// the batch's <c>WatchedUtc</c> instead, and also took any other watch of the
/// user's in that series stamped in the same millisecond; these tests hold the
/// id to exactly one marking action, against the real column type.
/// </summary>
[TestFixture]
public sealed class BulkWatchUndoTests : PostgresFixture
{
    private const string UserSeriesIndex = "IX_episode_watch_user_id_series_tvdb_id";

    [Test]
    public async Task Batch_Should_StampEveryRowItInsertedWithItsId_AndOneTimestamp()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        int[] episodes = [NextId(), NextId(), NextId()];

        var batch = await MarkRangeAsync(user, seriesId, episodes);

        batch.InsertedCount.Should().Be(3);
        batch.Id.Should().NotBe(Guid.Empty);

        await using var db = NewContext();
        var stored = await db.EpisodeWatches
            .Where(x => x.UserId == user && x.SeriesTvdbId == seriesId)
            .Select(x => new { x.BatchId, x.WatchedUtc })
            .ToListAsync();
        stored.Should().HaveCount(3).And.OnlyContain(row => row.BatchId == batch.Id);
        stored.Select(row => row.WatchedUtc).Distinct().Should().ContainSingle("a batch still shares one WatchedUtc");
    }

    [Test]
    public async Task Undo_Should_RemoveTheBatch_WhenTheIdComesBackAsText()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var batch = await MarkRangeAsync(user, seriesId, [NextId(), NextId(), NextId()]);

        // The round trip the toast makes: the id into a form field, back into a Guid.
        var posted = Guid.Parse(batch.Id.ToString("D"));

        var removed = await UndoAsync(user, seriesId, posted);

        removed.Should().Be(3);
        (await WatchedIdsAsync(user, seriesId)).Should().BeEmpty();
    }

    [Test]
    public async Task Undo_Should_LeaveEpisodesWatchedBeforeTheBatch()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var (earlier, first, second) = (NextId(), NextId(), NextId());

        await using (var db = NewContext())
        {
            await Repository(db).MarkWatchedAsync(user, seriesId, earlier);
        }

        // The batch includes the already-watched episode; only the two new rows belong to it.
        var batch = await MarkRangeAsync(user, seriesId, [earlier, first, second]);
        batch.InsertedCount.Should().Be(2);

        var removed = await UndoAsync(user, seriesId, batch.Id);

        removed.Should().Be(2);
        (await WatchedIdsAsync(user, seriesId)).Should().Equal(earlier);
    }

    [Test]
    public async Task Undo_Should_LeaveAWatchStampedAtTheSameMoment()
    {
        // The case that made the old timestamp match unreliable: another watch
        // of the same user and series with the batch's very WatchedUtc.
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var batch = await MarkRangeAsync(user, seriesId, [NextId(), NextId()]);
        var sameMoment = await StoredWatchedUtcAsync(user, seriesId);
        var neighbour = await InsertWatchAsync(user, seriesId, sameMoment, batchId: Guid.NewGuid());
        var legacy = await InsertWatchAsync(user, seriesId, sameMoment, batchId: null);

        var removed = await UndoAsync(user, seriesId, batch.Id);

        removed.Should().Be(2);
        (await WatchedIdsAsync(user, seriesId)).Should().BeEquivalentTo([neighbour, legacy]);
    }

    [Test]
    public async Task Undo_Should_OnlyTouchTheUserAndSeriesItWasIssuedFor()
    {
        var user = await SeedUserAsync();
        var otherUser = await SeedUserAsync();
        var (seriesId, otherSeriesId) = (NextId(), NextId());
        var batch = await MarkRangeAsync(user, seriesId, [NextId(), NextId()]);

        // Rows carrying the very same batch id (a forged or replayed form) that are not this user's in this series.
        await InsertWatchAsync(otherUser, seriesId, DateTime.UtcNow, batch.Id);
        await InsertWatchAsync(user, otherSeriesId, DateTime.UtcNow, batch.Id);

        var removed = await UndoAsync(user, seriesId, batch.Id);

        removed.Should().Be(2);
        (await WatchedIdsAsync(otherUser, seriesId)).Should().ContainSingle();
        (await WatchedIdsAsync(user, otherSeriesId)).Should().ContainSingle();
    }

    [Test]
    public async Task Undo_Should_RemoveNothing_WhenRepeated()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var batch = await MarkRangeAsync(user, seriesId, [NextId()]);
        await UndoAsync(user, seriesId, batch.Id);

        var removedAgain = await UndoAsync(user, seriesId, batch.Id);

        removedAgain.Should().Be(0);
    }

    [Test]
    public async Task Undo_Should_NeverRemoveRowsFromBeforeTheColumn()
    {
        // Rows written before batch_id existed keep it null; no id reaches them,
        // not even the empty one an unparsable form would bind to.
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var legacy = await InsertWatchAsync(user, seriesId, DateTime.UtcNow, batchId: null);

        var removed = await UndoAsync(user, seriesId, Guid.Empty);

        removed.Should().Be(0);
        (await WatchedIdsAsync(user, seriesId)).Should().Equal(legacy);
    }

    [Test]
    public async Task Undo_Should_FindTheBatchThroughTheUserSeriesIndex()
    {
        // Whether batch_id needs an index of its own: the delete also filters on
        // user and series, so the (user_id, series_tvdb_id) index narrows it to
        // one user's watches of one series before batch_id is looked at. 60
        // users with 40 series of 25 watched episodes each (60,000 rows).
        var user = await SeedUserAsync();
        for (var i = 0; i < 59; i++)
            await SeedUserAsync();

        await ExecuteAsync(
            """
            INSERT INTO episode_watch (id, user_id, series_tvdb_id, episode_tvdb_id, watched_utc, batch_id, created_utc, updated_utc)
            SELECT gen_random_uuid(), u.id, 800000 + s, 8000000 + s * 100 + e,
                   now() - (s * 25 + e) * interval '1 hour', gen_random_uuid(), now(), now()
            FROM app_user u, generate_series(1, 40) AS s, generate_series(1, 25) AS e;
            ANALYZE episode_watch;
            """);

        var seriesId = 800001;
        var capture = new CommandCapture();
        await using (var db = NewContext(capture))
        {
            await Repository(db).UndoWatchedBatchAsync(user, seriesId, Guid.NewGuid());
        }

        capture.NonQueries.Should().ContainSingle("the undo is one DELETE");
        var (sql, parameters) = capture.NonQueries.Single();
        sql.Should().StartWith("DELETE").And.Contain("batch_id");

        // The same statement with the same values, explained (not run).
        var numbered = parameters
            .Select((p, i) => (p.Name, Number: "$" + (i + 1)))
            .OrderByDescending(p => p.Name.Length) // @userId before a hypothetical @user
            .Aggregate(sql, (text, p) => text.Replace(p.Name, p.Number));
        var plan = await ScalarAsync<string>(
            "EXPLAIN (FORMAT JSON) " + numbered, parameters.Select(p => p.Value).ToArray());
        TestContext.Out.WriteLine(plan);

        plan.Should().Contain(UserSeriesIndex, "the (user_id, series_tvdb_id) index finds one user's watches of one series");
        plan.Should().NotContain("Seq Scan", "the undo must not read every user's watches");
    }

    private async Task<WatchedBatch> MarkRangeAsync(Guid user, int seriesId, int[] episodes)
    {
        await using var db = NewContext();
        return await Repository(db).MarkWatchedRangeAsync(user, seriesId, episodes, WatchSource.Bulk);
    }

    private async Task<int> UndoAsync(Guid user, int seriesId, Guid batchId)
    {
        await using var db = NewContext();
        return await Repository(db).UndoWatchedBatchAsync(user, seriesId, batchId);
    }

    private async Task<IReadOnlySet<int>> WatchedIdsAsync(Guid user, int seriesId)
    {
        await using var db = NewContext();
        return await Repository(db).GetWatchedEpisodeIdsAsync(user, seriesId);
    }

    private async Task<DateTime> StoredWatchedUtcAsync(Guid user, int seriesId)
    {
        await using var db = NewContext();
        return await db.EpisodeWatches
            .Where(x => x.UserId == user && x.SeriesTvdbId == seriesId)
            .Select(x => x.WatchedUtc)
            .FirstAsync();
    }

    /// <summary>A watch written straight to the table; returns its episode id.</summary>
    private async Task<int> InsertWatchAsync(Guid user, int seriesId, DateTime watchedUtc, Guid? batchId)
    {
        var entity = new EpisodeWatchEntity
        {
            Id = Guid.NewGuid(), UserId = user, SeriesTvdbId = seriesId, EpisodeTvdbId = NextId(),
            WatchedUtc = watchedUtc, BatchId = batchId
        };

        await using var db = NewContext();
        db.EpisodeWatches.Add(entity);
        await db.SaveChangesAsync();
        return entity.EpisodeTvdbId;
    }

    private static EpisodeWatchRepository Repository(AppDbContext db) =>
        new(db, NullLogger<EpisodeWatchRepository>.Instance);
}
