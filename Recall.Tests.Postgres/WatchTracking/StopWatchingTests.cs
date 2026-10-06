using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.WatchTracking;

/// <summary>
/// Stopping and resuming a series are atomic updates of the library row
/// (<c>tracked_series.stopped_utc</c>). They are tested here because a tracked
/// series cannot be inserted through EF on SQLite (<c>xmin</c>), and because
/// the update must leave the row's concurrency token usable.
/// </summary>
[TestFixture]
public sealed class StopWatchingTests : PostgresFixture
{
    private static readonly DateTime StoppedAt = new(2026, 9, 4, 18, 30, 0, DateTimeKind.Utc);

    [Test]
    public async Task Stop_Should_RecordTheDate_AndKeepTheFirstOne_WhenStoppedAgain()
    {
        var user = await SeedUserAsync();
        var seriesId = await SeedTrackedAsync(user, "Silo");

        await using var db = NewContext();
        var repository = NewRepository(db);

        (await repository.StopAsync(user, seriesId, StoppedAt)).Should().BeTrue();
        (await repository.StopAsync(user, seriesId, StoppedAt.AddDays(3))).Should().BeFalse("it is already stopped");

        var tracked = await repository.GetByUserAndTvdbIdAsync(user, seriesId);
        tracked!.StoppedUtc.Should().Be(StoppedAt);
        (await repository.GetByUserAsync(user)).Should().ContainSingle().Which.StoppedUtc.Should().Be(StoppedAt);
    }

    [Test]
    public async Task Stop_Should_ChangeNothing_ForASeriesThatIsNotInTheLibrary()
    {
        var user = await SeedUserAsync();

        await using var db = NewContext();

        (await NewRepository(db).StopAsync(user, NextId(), StoppedAt)).Should().BeFalse();
    }

    [Test]
    public async Task Resume_Should_ClearTheDate_AndNameTheSeries_Once()
    {
        var user = await SeedUserAsync();
        var seriesId = await SeedTrackedAsync(user, "Silo", StoppedAt);

        await using var db = NewContext();
        var repository = NewRepository(db);

        (await repository.ResumeAsync(user, seriesId)).Should().Be(
            new ResumedSeries("Silo", StoppedAt), "the caller gets the date back, to restore it if the resume is undone");
        (await repository.ResumeAsync(user, seriesId)).Should().BeNull("there is nothing left to resume");
        (await repository.GetByUserAndTvdbIdAsync(user, seriesId))!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task Resume_Should_ReturnNull_ForASeriesThatWasNeverStopped_OrIsNotInTheLibrary()
    {
        var user = await SeedUserAsync();
        var watching = await SeedTrackedAsync(user, "Watching");

        await using var db = NewContext();
        var repository = NewRepository(db);

        (await repository.ResumeAsync(user, watching)).Should().BeNull();
        (await repository.ResumeAsync(user, NextId())).Should().BeNull();
    }

    [Test]
    public async Task StopAndResume_Should_TouchOnlyTheCallersOwnRow()
    {
        var user = await SeedUserAsync();
        var other = await SeedUserAsync();
        var seriesId = NextId();
        await SeedTrackedAsync(user, "Shared", tvdbId: seriesId);
        await SeedTrackedAsync(other, "Shared", StoppedAt, seriesId);

        await using var db = NewContext();
        var repository = NewRepository(db);

        (await repository.StopAsync(user, seriesId, StoppedAt.AddDays(1))).Should().BeTrue();
        (await repository.GetByUserAndTvdbIdAsync(other, seriesId))!.StoppedUtc.Should().Be(StoppedAt);

        (await repository.ResumeAsync(user, seriesId))!.Name.Should().Be("Shared");
        (await repository.GetByUserAndTvdbIdAsync(other, seriesId))!.StoppedUtc.Should().Be(StoppedAt, "the other user is still stopped");
    }

    [Test]
    public async Task Stop_Should_StampUpdatedUtc_AndLeaveTheRowEditableThroughEf()
    {
        var user = await SeedUserAsync();
        var seriesId = await SeedTrackedAsync(user, "Silo");

        await using (var seed = NewContext())
        {
            await seed.TrackedSeries
                .Where(x => x.UserId == user)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.UpdatedUtc, StoppedAt.AddYears(-1)));
        }

        await using (var db = NewContext())
            (await NewRepository(db).StopAsync(user, seriesId, StoppedAt)).Should().BeTrue();

        await using var verify = NewContext();
        var row = await verify.TrackedSeries.SingleAsync(x => x.UserId == user);
        row.UpdatedUtc.Should().BeAfter(StoppedAt.AddYears(-1).AddDays(1), "ExecuteUpdate bypasses the audit stamp, so the repository sets it");

        // The update moved xmin; a row read afterwards still saves.
        row.Name = "Renamed";
        await verify.SaveChangesAsync();
    }

    [Test]
    public async Task UsersTracking_Should_LeaveOutThoseWhoStoppedWatching()
    {
        var watching = await SeedUserAsync();
        var stopped = await SeedUserAsync();
        var seriesId = NextId();
        await SeedTrackedAsync(watching, "Shared", tvdbId: seriesId);
        await SeedTrackedAsync(stopped, "Shared", StoppedAt, seriesId);

        await using var db = NewContext();
        var repository = NewRepository(db);

        (await repository.GetUserIdsTrackingAsync(seriesId)).Should().Equal(watching);

        (await repository.ResumeAsync(stopped, seriesId)).Should().NotBeNull();
        (await repository.GetUserIdsTrackingAsync(seriesId)).Should().BeEquivalentTo([watching, stopped]);
    }

    private static TrackedSeriesRepository NewRepository(Recall.Web.Infrastructure.Persistence.AppDbContext db) =>
        new(db, NullLogger<TrackedSeriesRepository>.Instance);

    private async Task<int> SeedTrackedAsync(Guid user, string name, DateTime? stoppedUtc = null, int? tvdbId = null)
    {
        var id = tvdbId ?? NextId();

        await using var db = NewContext();
        db.TrackedSeries.Add(new TrackedSeriesEntity
        {
            Id = Guid.NewGuid(), UserId = user, TvdbId = id, Name = name, StoppedUtc = stoppedUtc
        });
        await db.SaveChangesAsync();

        return id;
    }

    [Test]
    public async Task TheDateAResumeHandsBack_Should_RestoreTheStopExactly()
    {
        // The Undo of a mark that resumed a series sends this date round as ticks and stops the series again with it.
        var user = await SeedUserAsync();
        var stoppedAt = new DateTime(2026, 9, 4, 18, 30, 12, DateTimeKind.Utc).AddTicks(3_456_780);   // microseconds, as PostgreSQL keeps them
        var seriesId = await SeedTrackedAsync(user, "Silo", stoppedAt);

        await using var db = NewContext();
        var repository = NewRepository(db);

        var resumed = await repository.ResumeAsync(user, seriesId);
        var roundTripped = new DateTime(long.Parse(resumed!.StoppedUtc.Ticks.ToString()), DateTimeKind.Utc);

        (await repository.StopAsync(user, seriesId, roundTripped)).Should().BeTrue();
        (await repository.GetByUserAndTvdbIdAsync(user, seriesId))!.StoppedUtc.Should().Be(stoppedAt);
    }
}
