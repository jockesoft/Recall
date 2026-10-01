using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Web.Infrastructure.Authentication;
using Recall.Web.Infrastructure.Persistence;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Tests.Infrastructure.Authentication;

/// <summary>
/// Drives <see cref="RecallCookieEvents.ValidatePrincipal"/> with a real
/// <see cref="AppUserRepository"/> on in-memory SQLite and a hand-built
/// <see cref="CookieValidatePrincipalContext"/>.
/// </summary>
[TestFixture]
public sealed class RecallCookieEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private AppDbContext _dbContext = null!;
    private Mock<IAuthenticationService> _authenticationService = null!;
    private FixedTimeProvider _time = null!;
    private Guid _userId;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(_dbOptions);
        await _dbContext.Database.EnsureCreatedAsync();

        _userId = Guid.NewGuid();
        _dbContext.AppUsers.Add(new AppUserEntity
        {
            Id = _userId,
            Username = "alice",
            Email = "alice@test.local",
            Role = UserRole.User
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _authenticationService = new Mock<IAuthenticationService>();
        _time = new FixedTimeProvider(Now);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task Should_KeepThePrincipal_AndStampTheCookie_WhenTheUserStillMatches()
    {
        var context = CreateContext(RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User));
        var original = context.Principal;

        await CreateSut().ValidatePrincipal(context);

        context.Principal.Should().BeSameAs(original);
        context.ShouldRenew.Should().BeTrue("the check time has to be written back into the cookie");
        context.Properties.Items.Should().ContainKey(RecallCookieEvents.LastValidatedKey);
    }

    [Test]
    public async Task Should_RejectAndSignOut_WhenTheUserNoLongerExists()
    {
        await DeleteUserAsync();
        var context = CreateContext(RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User));

        await CreateSut().ValidatePrincipal(context);

        context.Principal.Should().BeNull();
        VerifySignedOut(Times.Once());
    }

    [Test]
    public async Task Should_RejectAndSignOut_WhenTheCookieHasNoUsableUserId()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")], CookieAuthenticationDefaults.AuthenticationScheme);
        var context = CreateContext(new ClaimsPrincipal(identity));

        await CreateSut().ValidatePrincipal(context);

        context.Principal.Should().BeNull();
        VerifySignedOut(Times.Once());
    }

    [Test]
    public async Task Should_ReplaceThePrincipal_WhenTheRoleChanged()
    {
        // The cookie still says Admin; the row has since been demoted to User.
        var context = CreateContext(RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.Admin));

        await CreateSut().ValidatePrincipal(context);

        context.Principal!.IsInRole(Roles.Admin).Should().BeFalse();
        context.Principal.IsInRole(Roles.User).Should().BeTrue();
        context.Principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(_userId.ToString());
        context.ShouldRenew.Should().BeTrue();
    }

    [Test]
    public async Task Should_ReplaceThePrincipal_WhenTheUsernameChanged()
    {
        var context = CreateContext(RecallPrincipal.Create(_userId, "old-name", "alice@test.local", UserRole.User));

        await CreateSut().ValidatePrincipal(context);

        context.Principal!.Identity!.Name.Should().Be("alice");
    }

    [Test]
    public async Task Should_NotTouchTheDatabase_WhileTheLastCheckIsRecent()
    {
        await DeleteUserAsync();
        var context = CreateContext(
            RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User),
            lastValidated: Now - RecallCookieEvents.RevalidationInterval + TimeSpan.FromSeconds(1));

        await CreateSut().ValidatePrincipal(context);

        context.Principal.Should().NotBeNull("the user is gone, but the cookie was checked less than the interval ago");
        context.ShouldRenew.Should().BeFalse();
        VerifySignedOut(Times.Never());
    }

    [Test]
    public async Task Should_CheckAgain_OnceTheIntervalHasPassed()
    {
        await DeleteUserAsync();
        var context = CreateContext(
            RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User),
            lastValidated: Now - RecallCookieEvents.RevalidationInterval);

        await CreateSut().ValidatePrincipal(context);

        context.Principal.Should().BeNull();
    }

    [Test]
    public async Task Should_CheckAgain_WhenTheStampIsUnreadableOrInTheFuture()
    {
        await DeleteUserAsync();

        var garbled = CreateContext(RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User));
        garbled.Properties.Items[RecallCookieEvents.LastValidatedKey] = "yesterday-ish";
        var future = CreateContext(
            RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User),
            lastValidated: Now.AddDays(1));

        await CreateSut().ValidatePrincipal(garbled);
        await CreateSut().ValidatePrincipal(future);

        garbled.Principal.Should().BeNull();
        future.Principal.Should().BeNull();
    }

    [Test]
    public async Task Should_KeepTheSession_AndRetryNextRequest_WhenTheDatabaseIsUnavailable()
    {
        var failingRepository = new Mock<IAppUserRepository>();
        failingRepository
            .Setup(x => x.GetByIdAsync(_userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        var sut = new RecallCookieEvents(failingRepository.Object, _time, NullLogger<RecallCookieEvents>.Instance);
        var context = CreateContext(RecallPrincipal.Create(_userId, "alice", "alice@test.local", UserRole.User));

        await sut.ValidatePrincipal(context);

        context.Principal.Should().NotBeNull();
        context.ShouldRenew.Should().BeFalse();
        context.Properties.Items.Should().NotContainKey(RecallCookieEvents.LastValidatedKey);
        VerifySignedOut(Times.Never());
    }

    private RecallCookieEvents CreateSut() =>
        new(new AppUserRepository(_dbContext), _time, NullLogger<RecallCookieEvents>.Instance);

    private CookieValidatePrincipalContext CreateContext(ClaimsPrincipal principal, DateTimeOffset? lastValidated = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(_authenticationService.Object)
                .BuildServiceProvider()
        };

        var properties = new AuthenticationProperties { IsPersistent = true };
        if (lastValidated is { } stamp)
            properties.Items[RecallCookieEvents.LastValidatedKey] = stamp.UtcDateTime.ToString("O");

        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(principal, properties, scheme.Name);

        return new CookieValidatePrincipalContext(httpContext, scheme, new CookieAuthenticationOptions(), ticket);
    }

    private async Task DeleteUserAsync()
    {
        await using var dbContext = new AppDbContext(_dbOptions);
        await dbContext.AppUsers.Where(x => x.Id == _userId).ExecuteDeleteAsync();
    }

    private void VerifySignedOut(Times times) =>
        _authenticationService.Verify(
            x => x.SignOutAsync(
                It.IsAny<HttpContext>(),
                CookieAuthenticationDefaults.AuthenticationScheme,
                It.IsAny<AuthenticationProperties?>()),
            times);
}
