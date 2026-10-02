using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.WatchTracking;

/// <summary>
/// A bulk "mark watched" is undone by deleting the rows that carry the batch's
/// exact <c>WatchedUtc</c>. The timestamp leaves the server as ticks in the
/// toast's form and comes back in the Undo post, while the rows keep whatever
/// PostgreSQL stored. .NET counts in 100 ns ticks and <c>timestamptz</c> in
/// microseconds; the repository truncates the batch timestamp to the
/// millisecond so the value it hands out is the value in the rows. SQLite
/// stores the full ticks as text, so none of this can be seen there.
/// </summary>
[TestFixture]
public sealed class BulkWatchUndoTests : PostgresFixture
{
    [Test]
    public async Task BatchTimestamp_Should_BeStoredExactlyAsReturned()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        int[] episodes = [NextId(), NextId(), NextId()];

        var batch = await MarkRangeAsync(user, seriesId, episodes);

        batch.InsertedCount.Should().Be(3);
        (batch.WatchedUtc.Ticks % TimeSpan.TicksPerMillisecond).Should().Be(0);
        batch.WatchedUtc.Kind.Should().Be(DateTimeKind.Utc);

        await using var db = NewContext();
        var stored = await db.EpisodeWatches
            .Where(x => x.UserId == user && x.SeriesTvdbId == seriesId)
            .Select(x => x.WatchedUtc)
            .ToListAsync();
        stored.Should().HaveCount(3).And.OnlyContain(watchedUtc => watchedUtc.Ticks == batch.WatchedUtc.Ticks);
    }

    [Test]
    public async Task Undo_Should_RemoveTheBatch_WhenTheTimestampComesBackAsTicks()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        int[] episodes = [NextId(), NextId(), NextId()];
        var batch = await MarkRangeAsync(user, seriesId, episodes);

        // The round trip the toast makes: ticks into a form field, back into a DateTime.
        var posted = batch.WatchedUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        var stamp = new DateTime(long.Parse(posted, CultureInfo.InvariantCulture), DateTimeKind.Utc);

        var removed = await UndoAsync(user, seriesId, stamp);

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
            await new EpisodeWatchRepository(db, NullLogger<EpisodeWatchRepository>.Instance)
                .MarkWatchedAsync(user, seriesId, earlier);
        }

        // The batch includes the already-watched episode; only the two new rows belong to it.
        var batch = await MarkRangeAsync(user, seriesId, [earlier, first, second]);
        batch.InsertedCount.Should().Be(2);

        var removed = await UndoAsync(user, seriesId, batch.WatchedUtc);

        removed.Should().Be(2);
        (await WatchedIdsAsync(user, seriesId)).Should().Equal(earlier);
    }

    [Test]
    public async Task Undo_Should_OnlyTouchTheUserAndSeriesItWasIssuedFor()
    {
        var user = await SeedUserAsync();
        var otherUser = await SeedUserAsync();
        var (seriesId, otherSeriesId) = (NextId(), NextId());
        var batch = await MarkRangeAsync(user, seriesId, [NextId(), NextId()]);

        // Rows that happen to carry the very same timestamp but are not this batch.
        await InsertWatchAsync(otherUser, seriesId, batch.WatchedUtc);
        await InsertWatchAsync(user, otherSeriesId, batch.WatchedUtc);

        var removed = await UndoAsync(user, seriesId, batch.WatchedUtc);

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
        await UndoAsync(user, seriesId, batch.WatchedUtc);

        var removedAgain = await UndoAsync(user, seriesId, batch.WatchedUtc);

        removedAgain.Should().Be(0);
    }

    [Test]
    public async Task Undo_Should_NotMatchANeighbouringMillisecond()
    {
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var batch = await MarkRangeAsync(user, seriesId, [NextId()]);

        var removed = await UndoAsync(user, seriesId, batch.WatchedUtc.AddMilliseconds(1));

        removed.Should().Be(0);
        (await WatchedIdsAsync(user, seriesId)).Should().ContainSingle();
    }

    [Test]
    public async Task Postgres_Should_NotPreserveTicksBelowAMicrosecond()
    {
        // What the truncation in MarkWatchedRangeAsync guards against: a
        // timestamp with 100 ns detail comes back from timestamptz without it,
        // so an untruncated batch timestamp would differ from the stored one.
        var user = await SeedUserAsync();
        var seriesId = NextId();
        var precise = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        var id = await InsertWatchAsync(user, seriesId, precise);

        await using var db = NewContext();
        var stored = await db.EpisodeWatches.Where(x => x.Id == id).Select(x => x.WatchedUtc).SingleAsync();

        stored.Should().NotBe(precise);
        (stored.Ticks % 10).Should().Be(0, "timestamptz keeps microseconds, which is 10 ticks");
        (precise - stored).Duration().Should().BeLessThan(TimeSpan.FromMicroseconds(1));
    }

    private async Task<WatchedBatch> MarkRangeAsync(Guid user, int seriesId, int[] episodes)
    {
        await using var db = NewContext();
        return await Repository(db).MarkWatchedRangeAsync(user, seriesId, episodes, WatchSource.Bulk);
    }

    private async Task<int> UndoAsync(Guid user, int seriesId, DateTime stamp)
    {
        await using var db = NewContext();
        return await Repository(db).UndoWatchedBatchAsync(user, seriesId, stamp);
    }

    private async Task<IReadOnlySet<int>> WatchedIdsAsync(Guid user, int seriesId)
    {
        await using var db = NewContext();
        return await Repository(db).GetWatchedEpisodeIdsAsync(user, seriesId);
    }

    private async Task<Guid> InsertWatchAsync(Guid user, int seriesId, DateTime watchedUtc)
    {
        var entity = new EpisodeWatchEntity
        {
            Id = Guid.NewGuid(), UserId = user, SeriesTvdbId = seriesId, EpisodeTvdbId = NextId(), WatchedUtc = watchedUtc
        };

        await using var db = NewContext();
        db.EpisodeWatches.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private static EpisodeWatchRepository Repository(AppDbContext db) =>
        new(db, NullLogger<EpisodeWatchRepository>.Instance);
}
