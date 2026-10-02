using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Postgres.Account;

/// <summary>
/// "Delete my account" against real PostgreSQL: one user with rows in every
/// table that belongs to a user is deleted, and nothing of theirs is left
/// while another user's data is untouched.
///
/// The check for leftovers does not list tables. It asks the database for
/// every table that has a <c>user_id</c> column, so a table added later
/// without being added to <c>AppUserRepository.DeleteAccountAsync</c> (or
/// without a cascading foreign key) fails here.
/// </summary>
[TestFixture]
public sealed class AccountDeletionTests : PostgresFixture
{
    [SetUp]
    public async Task EmptyTheUserTablesAsync()
    {
        // The only-admin rule counts every admin in the database, so each test starts without users.
        await ExecuteAsync("TRUNCATE app_user CASCADE; TRUNCATE email;");
    }

    [Test]
    public async Task DeleteAccount_Should_RemoveEverythingOfTheUser_AndNothingOfAnyoneElse()
    {
        var leaving = await SeedUserAsync("leaving");
        var staying = await SeedUserAsync("staying");
        var sharedSeries = NextId();
        await SeedEverythingAsync(leaving, sharedSeries, rating: 9);
        await SeedEverythingAsync(staying, sharedSeries, rating: 5);

        var tables = await UserOwnedTablesAsync();
        tables.Should().Contain(
        [
            "digest_send", "tracked_series", "tracked_movie", "episode_watch", "user_movie_watch", "user_like", "user_rating",
            "notification", "notified_episode", "login_token", "watchlist_import_job"
        ], "these are the tables the test seeds; the list itself comes from the database");

        foreach (var table in tables)
            (await CountAsync(table, leaving)).Should().BeGreaterThan(0, "the test must seed {0}, or it proves nothing about it", table);

        AccountDeletionResult result;
        await using (var db = NewContext())
            result = await new AppUserRepository(db).DeleteAccountAsync(leaving);

        result.Should().Be(AccountDeletionResult.Deleted);

        foreach (var table in tables)
        {
            (await CountAsync(table, leaving)).Should().Be(0, "{0} still has rows of the deleted user", table);
            (await CountAsync(table, staying)).Should().BeGreaterThan(0, "{0} lost rows of another user", table);
        }

        await using var check = NewContext();
        (await check.AppUsers.AnyAsync(x => x.Id == leaving)).Should().BeFalse();
        (await check.AppUsers.AnyAsync(x => x.Id == staying)).Should().BeTrue();

        // Rows reached through another row, or by address, not by a user id.
        (await check.WatchlistImportItems.CountAsync()).Should().Be(1, "the deleted user's import rows go with the import");
        (await check.WatchlistImportItems.AnyAsync(x => x.Job.UserId == staying)).Should().BeTrue();
        var remainingMail = await check.Emails.Select(x => x.ToAddress).ToListAsync();
        remainingMail.Should().HaveCount(2, "queued and sent mail to the deleted address is removed, whatever its case; other mail is kept");
        remainingMail.Should().OnlyContain(address => address.Equals(EmailOf(staying), StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task DeleteAccount_Should_TakeTheUsersRatingsOutOfTheRecallRating()
    {
        var leaving = await SeedUserAsync();
        var staying = await SeedUserAsync();
        var series = NextId();
        await SeedEverythingAsync(leaving, series, rating: 9);
        await SeedEverythingAsync(staying, series, rating: 5);

        await using (var db = NewContext())
        {
            var before = await new RatingRepository(db, NullLogger<RatingRepository>.Instance).GetSummaryAsync(RatingTargetType.Series, series);
            before.Count.Should().Be(2);
            before.Average.Should().Be(7);

            await new AppUserRepository(db).DeleteAccountAsync(leaving);
        }

        await using var check = NewContext();
        var after = await new RatingRepository(check, NullLogger<RatingRepository>.Instance).GetSummaryAsync(RatingTargetType.Series, series);
        after.Count.Should().Be(1);
        after.Average.Should().Be(5);
    }

    [Test]
    public async Task DeleteAccount_Should_LeaveTheSharedMetadataCachesAlone()
    {
        var leaving = await SeedUserAsync();
        var series = NextId();
        await SeedEverythingAsync(leaving, series, rating: 8);
        await using (var seed = NewContext())
        {
            seed.CachedSeriesOmdb.Add(new CachedSeriesOmdbEntity { TvdbId = series, RetrievedUtc = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
            await new AppUserRepository(db).DeleteAccountAsync(leaving);

        await using var check = NewContext();
        (await check.CachedSeriesOmdb.AnyAsync(x => x.TvdbId == series))
            .Should().BeTrue("snapshots of TheTVDB and OMDb data belong to nobody");
    }

    [Test]
    public async Task DeleteAccount_Should_RefuseTheOnlyAdmin_AndDeleteNothing()
    {
        var admin = await SeedUserAsync("the-admin");
        await MakeAdminAsync(admin);
        var member = await SeedUserAsync("a-member");
        await SeedEverythingAsync(admin, NextId(), rating: 7);

        await using (var db = NewContext())
        {
            var repository = new AppUserRepository(db);
            (await repository.IsOnlyAdminAsync(admin)).Should().BeTrue();
            (await repository.IsOnlyAdminAsync(member)).Should().BeFalse("a member is not an admin at all");
            (await repository.DeleteAccountAsync(admin)).Should().Be(AccountDeletionResult.OnlyAdmin);
        }

        await using var check = NewContext();
        (await check.AppUsers.AnyAsync(x => x.Id == admin)).Should().BeTrue();
        (await check.TrackedSeries.AnyAsync(x => x.UserId == admin)).Should().BeTrue("a refusal deletes nothing");
        (await check.Emails.AnyAsync()).Should().BeTrue();
    }

    [Test]
    public async Task DeleteAccount_Should_AllowAnAdmin_WhenAnotherAdminRemains()
    {
        var leaving = await SeedUserAsync();
        var remaining = await SeedUserAsync();
        await MakeAdminAsync(leaving);
        await MakeAdminAsync(remaining);

        await using (var db = NewContext())
        {
            var repository = new AppUserRepository(db);
            (await repository.IsOnlyAdminAsync(leaving)).Should().BeFalse();
            (await repository.DeleteAccountAsync(leaving)).Should().Be(AccountDeletionResult.Deleted);
            (await repository.IsOnlyAdminAsync(remaining)).Should().BeTrue("now there is one");
        }
    }

    [Test]
    public async Task DeleteAccount_Should_ReportAMissingUser_AndTheAccountShouldBeGoneForTheCookieCheck()
    {
        var user = await SeedUserAsync();

        await using var db = NewContext();
        var repository = new AppUserRepository(db);

        (await repository.DeleteAccountAsync(user)).Should().Be(AccountDeletionResult.Deleted);
        (await repository.DeleteAccountAsync(user)).Should().Be(AccountDeletionResult.UserNotFound, "a second session deleting again finds nothing");

        // What RecallCookieEvents reads when it revalidates another session's cookie.
        (await repository.GetByIdAsync(user)).Should().BeNull();
    }

    [Test]
    public async Task SigningInAgain_Should_CreateANewEmptyAccount()
    {
        var old = await SeedUserAsync();
        var email = EmailOf(old);
        await SeedEverythingAsync(old, NextId(), rating: 6);

        await using var db = NewContext();
        var repository = new AppUserRepository(db);
        await repository.DeleteAccountAsync(old);

        var fresh = await repository.GetOrCreateByEmailAsync(email);

        fresh.Id.Should().NotBe(old);
        fresh.Role.Should().Be(UserRole.User);
        (await db.TrackedSeries.AnyAsync(x => x.UserId == fresh.Id)).Should().BeFalse();
        (await db.UserRatings.AnyAsync(x => x.UserId == fresh.Id)).Should().BeFalse();
    }

    // ---- helpers ---------------------------------------------------------------

    private static string EmailOf(Guid userId) => $"{userId:N}@example.com";

    private Task MakeAdminAsync(Guid userId) =>
        ExecuteAsync($"UPDATE app_user SET role = 'Admin' WHERE id = '{userId}'");

    /// <summary>Every table with a <c>user_id</c> column, straight from the database.</summary>
    private async Task<IReadOnlyList<string>> UserOwnedTablesAsync()
    {
        var names = await ScalarAsync<string>(
            """
            SELECT string_agg(table_name, ',' ORDER BY table_name)
            FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'user_id'
            """);

        return (names ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<long> CountAsync(string table, Guid userId) =>
        await ScalarAsync<long>($"SELECT count(*) FROM \"{table}\" WHERE user_id = '{userId}'");

    /// <summary>One row (or more) for the user in every table that can hold a user's data.</summary>
    private async Task SeedEverythingAsync(Guid userId, int seriesId, int rating)
    {
        var now = DateTime.UtcNow;
        var movieId = NextId();
        var episodeId = NextId();

        await using var db = NewContext();

        db.TrackedSeries.Add(new TrackedSeriesEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = seriesId, Name = "Series" });
        db.TrackedMovies.Add(new TrackedMovieEntity { Id = Guid.NewGuid(), UserId = userId, TvdbId = movieId, Name = "Movie" });
        db.EpisodeWatches.Add(new EpisodeWatchEntity
        {
            Id = Guid.NewGuid(), UserId = userId, SeriesTvdbId = seriesId, EpisodeTvdbId = episodeId, WatchedUtc = now
        });
        db.UserMovieWatches.Add(new UserMovieWatchEntity { Id = Guid.NewGuid(), UserId = userId, MovieTvdbId = NextId(), WatchedUtc = now });
        db.UserLikes.Add(new UserLikeEntity
        {
            Id = Guid.NewGuid(), UserId = userId, TargetType = LikeTargetType.Series, TargetTvdbId = seriesId, SeriesTvdbId = seriesId
        });
        db.UserRatings.Add(new UserRatingEntity
        {
            Id = Guid.NewGuid(), UserId = userId, TargetType = RatingTargetType.Series, TargetTvdbId = seriesId,
            SeriesTvdbId = seriesId, Value = rating
        });
        db.Notifications.Add(new NotificationEntity
        {
            Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.NewEpisode, Title = "New episode",
            SeriesTvdbId = seriesId, EpisodeTvdbId = episodeId, EpisodeCount = 1
        });
        db.NotifiedEpisodes.Add(new NotifiedEpisodeEntity
        {
            Id = Guid.NewGuid(), UserId = userId, SeriesTvdbId = seriesId, EpisodeTvdbId = episodeId, CreatedUtc = now
        });
        db.LoginTokens.Add(new LoginTokenEntity
        {
            Id = Guid.NewGuid(), UserId = userId, TokenHash = Guid.NewGuid().ToString("N"), ExpiresUtc = now.AddMinutes(15)
        });

        db.DigestSends.Add(new DigestSendEntity
        {
            Id = Guid.NewGuid(), UserId = userId, PeriodStart = DateOnly.FromDateTime(now), Status = DigestSendStatus.Queued, CreatedUtc = now
        });

        var jobId = Guid.NewGuid();
        db.WatchlistImportJobs.Add(new WatchlistImportJobEntity
        {
            Id = jobId, UserId = userId, FileName = "ratings.csv", Status = WatchlistImportJobStatus.Completed,
            TotalCount = 1, ProcessedCount = 1, ImportedCount = 1, CreatedUtc = now, CompletedUtc = now
        });
        db.WatchlistImportItems.Add(new WatchlistImportItemEntity
        {
            Id = Guid.NewGuid(), JobId = jobId, RowNumber = 1, ImdbId = "tt0000001", Title = "Title", TitleType = "Movie",
            Status = WatchlistImportItemStatus.Imported, CreatedUtc = now, ProcessedUtc = now
        });

        // One delivered and one still queued, to the user's address in mixed case.
        var address = EmailOf(userId);
        db.Emails.Add(new EmailEntity { Id = Guid.NewGuid(), ToAddress = address, Subject = "Sign in", Body = string.Empty, SentUtc = now });
        db.Emails.Add(new EmailEntity { Id = Guid.NewGuid(), ToAddress = address.ToUpperInvariant(), Subject = "Sign in", Body = "link" });

        await db.SaveChangesAsync();
    }
}
