using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

[TestFixture]
public sealed class WatchlistImportRepositoryTests
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

        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    [Test]
    public async Task CreateJobAsync_Should_MarkUnsupportedItems_ProcessedImmediately()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var db = new AppDbContext(_dbOptions);
        var sut = new WatchlistImportRepository(db);

        var items = new[]
        {
            new NewWatchlistImportItem(1, "tt0000001", "A Movie", "Movie", 5, IsSupported: true),
            new NewWatchlistImportItem(2, "tt0000002", "A Video Game", "Video Game", null, IsSupported: false)
        };

        var job = await sut.CreateJobAsync(userId, "export.csv", items);

        job.TotalCount.Should().Be(2);
        job.ProcessedCount.Should().Be(1, "the unsupported row resolves immediately; the movie row is still pending");
        job.SkippedCount.Should().Be(1);
        job.Status.Should().Be(WatchlistImportJobStatus.Processing);

        job.Items.Should().ContainSingle(i => i.TitleType == "Video Game" && i.Status == WatchlistImportItemStatus.Unsupported);
        job.Items.Should().ContainSingle(i => i.TitleType == "Movie" && i.Status == WatchlistImportItemStatus.Pending);
    }

    [Test]
    public async Task GetActiveJobForUserAsync_Should_ReturnProcessingJob_AndNullAfterCompletion()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        Guid jobId;
        await using (var db = new AppDbContext(_dbOptions))
        {
            var sut = new WatchlistImportRepository(db);
            var job = await sut.CreateJobAsync(userId, "export.csv",
                [new NewWatchlistImportItem(1, "tt0000001", "A Movie", "Movie", null, IsSupported: true)]);
            jobId = job.Id;
        }

        await using (var db = new AppDbContext(_dbOptions))
        {
            var sut = new WatchlistImportRepository(db);
            (await sut.GetActiveJobForUserAsync(userId)).Should().NotBeNull();

            var itemId = db.WatchlistImportItems.Single(x => x.JobId == jobId).Id;
            await sut.MarkItemResultAsync(itemId, WatchlistImportItemStatus.Imported, 123, "Added.", CancellationToken.None);
            await sut.RecalculateJobProgressAsync(jobId);
        }

        await using (var db = new AppDbContext(_dbOptions))
        {
            var sut = new WatchlistImportRepository(db);
            (await sut.GetActiveJobForUserAsync(userId)).Should().BeNull("the job is now completed");
        }
    }

    [Test]
    public async Task ClaimNextPendingBatchAsync_Should_ReturnOldestJobsFirst_AcrossUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await SeedUserAsync(userA);
        await SeedUserAsync(userB);

        await using var db = new AppDbContext(_dbOptions);

        var olderJob = new WatchlistImportJobEntity
        {
            Id = Guid.NewGuid(),
            UserId = userA,
            FileName = "a.csv",
            Status = WatchlistImportJobStatus.Processing,
            TotalCount = 1,
            CreatedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        olderJob.Items.Add(new WatchlistImportItemEntity
        {
            Id = Guid.NewGuid(),
            JobId = olderJob.Id,
            RowNumber = 1,
            ImdbId = "tt0000001",
            Title = "Older",
            TitleType = "Movie",
            Status = WatchlistImportItemStatus.Pending,
            CreatedUtc = olderJob.CreatedUtc
        });

        var newerJob = new WatchlistImportJobEntity
        {
            Id = Guid.NewGuid(),
            UserId = userB,
            FileName = "b.csv",
            Status = WatchlistImportJobStatus.Processing,
            TotalCount = 1,
            CreatedUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        newerJob.Items.Add(new WatchlistImportItemEntity
        {
            Id = Guid.NewGuid(),
            JobId = newerJob.Id,
            RowNumber = 1,
            ImdbId = "tt0000002",
            Title = "Newer",
            TitleType = "Movie",
            Status = WatchlistImportItemStatus.Pending,
            CreatedUtc = newerJob.CreatedUtc
        });

        db.WatchlistImportJobs.AddRange(newerJob, olderJob);
        await db.SaveChangesAsync();

        var sut = new WatchlistImportRepository(db);
        var batch = await sut.ClaimNextPendingBatchAsync(1);

        batch.Should().ContainSingle();
        batch[0].Title.Should().Be("Older", "the oldest queued row across every user's job should come first");
        batch[0].UserId.Should().Be(userA);
    }

    [Test]
    public async Task RecalculateJobProgressAsync_Should_CompleteJob_OnceEveryItemIsProcessed()
    {
        var userId = Guid.NewGuid();
        await SeedUserAsync(userId);

        await using var db = new AppDbContext(_dbOptions);
        var sut = new WatchlistImportRepository(db);

        var job = await sut.CreateJobAsync(userId, "export.csv",
        [
            new NewWatchlistImportItem(1, "tt0000001", "Movie One", "Movie", 8, IsSupported: true),
            new NewWatchlistImportItem(2, "tt0000002", "Movie Two", "Movie", null, IsSupported: true)
        ]);

        var itemIds = job.Items.Select(i => i.Id).ToArray();

        await sut.MarkItemResultAsync(itemIds[0], WatchlistImportItemStatus.Imported, 1, "ok", CancellationToken.None);
        await sut.RecalculateJobProgressAsync(job.Id);

        var midway = await sut.GetLatestJobForUserAsync(userId, includeItems: false);
        midway!.Status.Should().Be(WatchlistImportJobStatus.Processing);
        midway.ProcessedCount.Should().Be(1);

        await sut.MarkItemResultAsync(itemIds[1], WatchlistImportItemStatus.NotFound, null, "no match", CancellationToken.None);
        await sut.RecalculateJobProgressAsync(job.Id);

        var final = await sut.GetLatestJobForUserAsync(userId, includeItems: false);
        final!.Status.Should().Be(WatchlistImportJobStatus.Completed);
        final.ImportedCount.Should().Be(1);
        final.NotFoundCount.Should().Be(1);
        final.CompletedUtc.Should().NotBeNull();
    }

    private async Task SeedUserAsync(Guid userId)
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = userId,
            Username = $"user-{userId:N}",
            Email = $"{userId:N}@test.local"
        });

        await dbContext.SaveChangesAsync();
    }
}
