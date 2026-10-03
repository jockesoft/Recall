using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Admin;
using Recall.Web.Services;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Pages;

[TestFixture]
public class DigestPreviewModelTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly AppUserEntity Admin = new() { Id = Guid.NewGuid(), Username = "admin", Email = "admin@test.local" };
    private static readonly AppUserEntity Saga = new() { Id = Guid.NewGuid(), Username = "saga", Email = "saga@test.local" };

    private Mock<IAppUserRepository> _users = null!;
    private Mock<IDigestComposer> _composer = null!;
    private SiteOptions _site = null!;

    [SetUp]
    public void SetUp()
    {
        _users = new Mock<IAppUserRepository>();
        _users.Setup(x => x.GetByIdAsync(Admin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Admin);
        _users.Setup(x => x.GetByEmailAsync("saga@test.local", It.IsAny<CancellationToken>())).ReturnsAsync(Saga);

        _composer = new Mock<IDigestComposer>();
        _composer
            .Setup(x => x.ComposeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IDictionary<int, SeriesAggregate?>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string name, DateTime _, string baseUrl, IDictionary<int, SeriesAggregate?>? _, CancellationToken _) =>
                new ComposedDigest(
                    new DigestContent(DigestSection<DigestPremiere>.Empty, DigestSection<DigestEpisodeLine>.Empty, DigestSection<DigestEpisodeLine>.Empty),
                    new DigestEmail($"For {name}", "text", "<p>html</p>"),
                    $"{baseUrl}/Digest/OneClick?token=for-{name}"));

        _site = new SiteOptions { BaseUrl = "https://recall.example" };
    }

    private DigestPreviewModel CreateSut(string environment)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.UserId).Returns(Admin.Id);

        return new DigestPreviewModel(
            currentUser.Object, _users.Object, _composer.Object,
            Options.Create(_site), Options.Create(new DigestOptions()),
            new StubEnvironment(environment),
            new FixedTimeProvider(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))).WithTempData().WithHttpContext();
    }

    private void VerifyComposedFor(AppUserEntity user, Times times) =>
        _composer.Verify(
            x => x.ComposeAsync(user.Id, user.Username, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<IDictionary<int, SeriesAggregate?>?>(), It.IsAny<CancellationToken>()),
            times);

    // ---- Development: any user ----------------------------------------------------

    [Test]
    public async Task InDevelopment_AnyUsersDigest_Should_BePreviewable()
    {
        var sut = CreateSut("Development");
        sut.Email = "saga@test.local";

        await sut.OnGetAsync(CancellationToken.None);

        sut.CanChooseUser.Should().BeTrue();
        sut.Subject.Should().BeSameAs(Saga);
        sut.Digest!.Email!.Subject.Should().Be("For saga");
        sut.Refusal.Should().BeNull();
        VerifyComposedFor(Saga, Times.Once());
    }

    [Test]
    public async Task InDevelopment_AnUnknownAddress_Should_GetAnExplanation()
    {
        var sut = CreateSut("Development");
        sut.Email = "nobody@test.local";

        await sut.OnGetAsync(CancellationToken.None);

        sut.Digest.Should().BeNull();
        sut.Refusal.Should().Be("No user has that email address.");
    }

    // ---- everywhere else: only your own -------------------------------------------

    [TestCase("Production")]
    [TestCase("Staging")]
    [TestCase("Test")]
    public async Task OutsideDevelopment_AnAdmin_Should_OnlyPreviewTheirOwnDigest(string environment)
    {
        var sut = CreateSut(environment);
        sut.Email = "saga@test.local";

        var result = await sut.OnGetAsync(CancellationToken.None);

        result.Should().BeOfType<PageResult>();
        sut.CanChooseUser.Should().BeFalse();
        sut.Digest.Should().BeNull("another user's digest is their library and watch history");
        sut.Subject.Should().BeNull();
        sut.Refusal.Should().Be("Here you can only preview your own digest.");
        _users.Verify(x => x.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "the other account is not even looked up");
        _composer.VerifyNoOtherCalls();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ADMIN@test.local")]
    public async Task OutsideDevelopment_AnAdmin_Should_GetTheirOwnDigest(string? email)
    {
        var sut = CreateSut("Production");
        sut.Email = email;

        await sut.OnGetAsync(CancellationToken.None);

        sut.Subject.Should().BeSameAs(Admin);
        sut.Refusal.Should().BeNull();
        VerifyComposedFor(Admin, Times.Once());
    }

    // ---- what the preview is built with -------------------------------------------

    [Test]
    public async Task ThePreview_Should_UseTheChosenDate_AndTheConfiguredBaseUrl_AndOfferTheUnsubscribePage()
    {
        var sut = CreateSut("Production");
        sut.AsOf = new DateOnly(2026, 9, 18);

        await sut.OnGetAsync(CancellationToken.None);

        _composer.Verify(
            // The chosen date at the hour the digest goes out (Digest:HourUtc, 15 by default).
            x => x.ComposeAsync(Admin.Id, "admin", new DateTime(2026, 9, 18, 15, 0, 0, DateTimeKind.Utc), "https://recall.example", It.IsAny<IDictionary<int, SeriesAggregate?>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        sut.UnsubscribeUrl.Should().Be("https://recall.example/Digest/Unsubscribe?token=for-admin");
    }

    [Test]
    public async Task ThePreview_Should_DefaultToToday_AndFallBackToTheRequestsAddress_WithoutABaseUrl()
    {
        _site.BaseUrl = null;
        var sut = CreateSut("Production");
        sut.HttpContext.Request.Scheme = "https";
        sut.HttpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString("preview.test");

        await sut.OnGetAsync(CancellationToken.None);

        _composer.Verify(
            x => x.ComposeAsync(Admin.Id, "admin", Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), "https://preview.test", It.IsAny<IDictionary<int, SeriesAggregate?>?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void ThePage_Should_BeForAdminsOnly()
    {
        var authorize = typeof(DigestPreviewModel)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        authorize.Roles.Should().Be("Admin");
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Recall.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
