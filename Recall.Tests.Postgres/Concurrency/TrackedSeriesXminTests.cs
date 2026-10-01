using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Concurrency;

/// <summary>
/// <c>TrackedSeriesEntity.Version</c> maps to PostgreSQL's <c>xmin</c> system
/// column. SQLite turns it into an ordinary NOT NULL column, so there a tracked
/// series cannot even be inserted through EF, and the concurrency check does
/// not exist.
/// </summary>
[TestFixture]
public sealed class TrackedSeriesXminTests : PostgresFixture
{
    [Test]
    public async Task Insert_Should_WorkThroughEf_AndReadBackAVersion()
    {
        var user = await SeedUserAsync();
        var entity = NewTrackedSeries(user);

        await using (var db = NewContext())
        {
            db.TrackedSeries.Add(entity);
            await db.SaveChangesAsync();
        }

        entity.Version.Should().NotBe(0u, "the database assigns xmin on insert and EF reads it back");

        await using var verify = NewContext();
        (await verify.TrackedSeries.SingleAsync(x => x.Id == entity.Id)).Version.Should().Be(entity.Version);
    }

    [Test]
    public async Task Version_Should_BeSystemColumn_NotARealOne()
    {
        var realColumns = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_name = 'tracked_series' AND column_name IN ('xmin', 'version');
            """);

        realColumns.Should().Be(0, "the migrations must not create a column for the concurrency token");
    }

    [Test]
    public async Task Update_Should_ChangeTheVersion()
    {
        var user = await SeedUserAsync();
        var entity = NewTrackedSeries(user);

        await using var db = NewContext();
        db.TrackedSeries.Add(entity);
        await db.SaveChangesAsync();
        var inserted = entity.Version;

        entity.Name = "Renamed";
        await db.SaveChangesAsync();

        entity.Version.Should().NotBe(inserted);
    }

    [Test]
    public async Task Update_Should_Throw_WhenTheRowChangedSinceItWasLoaded()
    {
        var user = await SeedUserAsync();
        var id = await InsertAsync(user);

        await using var first = NewContext();
        await using var second = NewContext();
        var loadedByFirst = await first.TrackedSeries.SingleAsync(x => x.Id == id);
        var loadedBySecond = await second.TrackedSeries.SingleAsync(x => x.Id == id);

        loadedByFirst.Name = "First writer";
        await first.SaveChangesAsync();

        loadedBySecond.Name = "Second writer";
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = NewContext();
        (await verify.TrackedSeries.SingleAsync(x => x.Id == id)).Name.Should().Be("First writer");
    }

    [Test]
    public async Task Delete_Should_Throw_WhenTheRowChangedSinceItWasLoaded()
    {
        var user = await SeedUserAsync();
        var id = await InsertAsync(user);

        await using var first = NewContext();
        await using var second = NewContext();
        var loadedByFirst = await first.TrackedSeries.SingleAsync(x => x.Id == id);
        var loadedBySecond = await second.TrackedSeries.SingleAsync(x => x.Id == id);

        loadedByFirst.Overview = "Changed in the meantime";
        await first.SaveChangesAsync();

        second.TrackedSeries.Remove(loadedBySecond);
        var act = () => second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = NewContext();
        (await verify.TrackedSeries.AnyAsync(x => x.Id == id)).Should().BeTrue();
    }

    [Test]
    public async Task Update_Should_Succeed_AfterReloadingTheCurrentVersion()
    {
        var user = await SeedUserAsync();
        var id = await InsertAsync(user);

        await using (var other = NewContext())
        {
            (await other.TrackedSeries.SingleAsync(x => x.Id == id)).Name = "Changed elsewhere";
            await other.SaveChangesAsync();
        }

        await using var db = NewContext();
        var current = await db.TrackedSeries.SingleAsync(x => x.Id == id);
        current.Name = "Changed here";

        var act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Repository_Should_AddReadAndRemoveATrackedSeries()
    {
        var user = await SeedUserAsync();
        var tvdbId = NextId();
        await using var db = NewContext();
        var repository = new TrackedSeriesRepository(db, NullLogger<TrackedSeriesRepository>.Instance);

        var added = await repository.AddAsync(new TrackedSeries
        {
            UserId = user,
            TvdbId = tvdbId,
            Name = "Severance",
            FirstAired = new DateOnly(2022, 2, 18)
        });

        added.Should().BeTrue();
        var stored = await repository.GetByUserAndTvdbIdAsync(user, tvdbId);
        stored.Should().NotBeNull();
        stored!.Version.Should().NotBe(0u);
        stored.FirstAired.Should().Be(new DateOnly(2022, 2, 18));
        (await repository.ExistsAsync(user, tvdbId)).Should().BeTrue();
        (await repository.GetUserIdsTrackingAsync(tvdbId)).Should().Equal(user);

        await repository.RemoveAsync(user, stored.Id);

        (await repository.ExistsAsync(user, tvdbId)).Should().BeFalse();
    }

    private static TrackedSeriesEntity NewTrackedSeries(Guid user) =>
        new() { Id = Guid.NewGuid(), UserId = user, TvdbId = NextId(), Name = "Original" };

    private async Task<Guid> InsertAsync(Guid user)
    {
        var entity = NewTrackedSeries(user);
        await using var db = NewContext();
        db.TrackedSeries.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }
}
