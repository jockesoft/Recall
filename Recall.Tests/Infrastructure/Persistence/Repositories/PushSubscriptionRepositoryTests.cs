using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Persistence.Repositories;

/// <summary>
/// Push subscriptions: one row per browser, owned by one user, plus the
/// quiet-hours hold and the user's quiet-hours setting.
/// </summary>
[TestFixture]
public sealed class PushSubscriptionRepositoryTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private Guid _alice;
    private Guid _bob;

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = new AppDbContext(_dbOptions);
        await db.Database.EnsureCreatedAsync();

        _alice = Guid.NewGuid();
        _bob = Guid.NewGuid();
        db.AppUsers.AddRange(
            new AppUserEntity { Id = _alice, Username = "alice", Email = "alice@test.local" },
            new AppUserEntity { Id = _bob, Username = "bob", Email = "bob@test.local" });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDownAsync() => await _connection.DisposeAsync();

    private PushSubscriptionRepository NewRepository(AppDbContext db) =>
        new(db, NullLogger<PushSubscriptionRepository>.Instance);

    private static NewPushSubscription Browser(string name, string zone = "Europe/Stockholm", string key = "key") =>
        new($"https://fcm.googleapis.com/fcm/send/{name}", key, "auth", "Chrome on Android", zone);

    [Test]
    public async Task Save_Should_AddADevice_AndSavingTheSameBrowserAgain_Should_UpdateIt()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);

        (await repository.SaveAsync(_alice, Browser("phone"))).Should().Be(PushSubscribeResult.Added);
        (await repository.SaveAsync(_alice, Browser("phone", zone: "America/New_York", key: "rotated")))
            .Should().Be(PushSubscribeResult.Updated);

        var devices = await repository.GetForUserAsync(_alice);
        devices.Should().ContainSingle();
        devices[0].TimeZone.Should().Be("America/New_York");
        devices[0].P256dh.Should().Be("rotated");
    }

    [Test]
    public async Task Save_Should_MoveABrowser_ThatWasSubscribedForAnotherAccount()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);
        await repository.SaveAsync(_bob, Browser("shared-laptop"));

        var result = await repository.SaveAsync(_alice, Browser("shared-laptop"));

        result.Should().Be(PushSubscribeResult.Added, "it is a new device for Alice");
        (await repository.GetForUserAsync(_bob)).Should().BeEmpty("Bob's pushes must not reach the browser Alice now uses");
        (await repository.GetForUserAsync(_alice)).Should().ContainSingle();
    }

    [Test]
    public async Task Remove_Should_OnlyTouchTheUsersOwnDevice()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);
        await repository.SaveAsync(_alice, Browser("alice-phone"));
        await repository.SaveAsync(_bob, Browser("bob-phone"));
        var bobsDevice = (await repository.GetForUserAsync(_bob)).Single();

        (await repository.RemoveAsync(_alice, bobsDevice.Id)).Should().BeFalse("it is not Alice's");
        (await repository.RemoveByEndpointAsync(_alice, bobsDevice.Endpoint)).Should().BeFalse();
        (await repository.GetForUserAsync(_bob)).Should().ContainSingle();

        (await repository.RemoveAsync(_bob, bobsDevice.Id)).Should().BeTrue();
        (await repository.GetForUserAsync(_bob)).Should().BeEmpty();

        (await repository.RemoveByEndpointAsync(_alice, Browser("alice-phone").Endpoint)).Should().BeTrue();
        (await repository.GetForUserAsync(_alice)).Should().BeEmpty();
    }

    [Test]
    public async Task Hold_Should_KeepTheEarliestStart_AndDelivery_Should_ClearIt()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);
        await repository.SaveAsync(_alice, Browser("phone"));
        var device = (await repository.GetForUserAsync(_alice)).Single();
        var release = Now.AddHours(8);

        await repository.HoldAsync(device.Id, Now, release);
        await repository.HoldAsync(device.Id, Now.AddHours(1), release);

        var held = (await repository.GetForUserAsync(_alice)).Single();
        held.HeldSinceUtc.Should().Be(Now, "the second hold adds to the first, which started earlier");
        held.HeldUntilUtc.Should().Be(release);

        await repository.MarkDeliveredAsync(device.Id, release);

        var delivered = (await repository.GetForUserAsync(_alice)).Single();
        delivered.LastSuccessUtc.Should().Be(release);
        delivered.HeldSinceUtc.Should().BeNull();
        delivered.HeldUntilUtc.Should().BeNull();
    }

    [Test]
    public async Task GetDueHeld_Should_ReturnHoldsWhoseTimeHasCome_OrWhoseUserTurnedQuietHoursOff()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);
        await repository.SaveAsync(_alice, Browser("due"));
        await repository.SaveAsync(_alice, Browser("not-yet"));
        await repository.SaveAsync(_alice, Browser("nothing-held"));
        await repository.SaveAsync(_bob, Browser("bob-not-yet"));
        var all = (await repository.GetForUserAsync(_alice)).Concat(await repository.GetForUserAsync(_bob)).ToDictionary(d => d.Endpoint.Split('/')[^1]);

        await repository.HoldAsync(all["due"].Id, Now.AddHours(-9), Now.AddMinutes(-5));
        await repository.HoldAsync(all["not-yet"].Id, Now.AddHours(-1), Now.AddHours(3));
        await repository.HoldAsync(all["bob-not-yet"].Id, Now.AddHours(-1), Now.AddHours(3));

        (await repository.GetDueHeldAsync(Now)).Select(d => d.Endpoint.Split('/')[^1]).Should().Equal("due");

        await new AppUserRepository(db).SetPushQuietHoursAsync(_bob, on: false);

        (await repository.GetDueHeldAsync(Now)).Select(d => d.Endpoint.Split('/')[^1])
            .Should().BeEquivalentTo(["due", "bob-not-yet"], "a user who turned quiet hours off gets what was held at the next run");

        await repository.ClearHoldAsync(all["due"].Id);
        (await repository.GetDueHeldAsync(Now)).Select(d => d.Endpoint.Split('/')[^1]).Should().Equal("bob-not-yet");
    }

    [Test]
    public async Task DeleteGone_Should_RemoveTheSubscription()
    {
        await using var db = new AppDbContext(_dbOptions);
        var repository = NewRepository(db);
        await repository.SaveAsync(_alice, Browser("gone"));

        await repository.DeleteAsync((await repository.GetForUserAsync(_alice)).Single().Id);

        (await db.PushSubscriptions.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task QuietHours_Should_BeOnForANewUser_AndStayOffOnceTurnedOff()
    {
        var carol = Guid.NewGuid();
        await using (var seed = new AppDbContext(_dbOptions))
        {
            seed.AppUsers.Add(new AppUserEntity { Id = carol, Username = "carol", Email = "carol@test.local", PushQuietHours = false });
            await seed.SaveChangesAsync();
        }

        await using var db = new AppDbContext(_dbOptions);
        (await db.AppUsers.SingleAsync(x => x.Id == _alice)).PushQuietHours.Should().BeTrue("quiet hours are on by default");
        (await db.AppUsers.SingleAsync(x => x.Id == carol)).PushQuietHours
            .Should().BeFalse("false must be stored as false, not replaced by the column's default");

        await new AppUserRepository(db).SetPushQuietHoursAsync(_alice, on: false);
        db.ChangeTracker.Clear();
        (await db.AppUsers.SingleAsync(x => x.Id == _alice)).PushQuietHours.Should().BeFalse();
    }
}
