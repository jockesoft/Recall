using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class DigestComposerTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);
    // "Now" for the release-moment rules: noon UTC on Today.
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();
    private const string BaseUrl = "https://recall.example";

    private Mock<ITrackedSeriesRepository> _tracked = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<ITheTvDbService> _tvDb = null!;
    private DigestUnsubscribeTokens _tokens = null!;
    private DigestComposer _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tracked = new Mock<ITrackedSeriesRepository>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _tvDb = new Mock<ITheTvDbService>(MockBehavior.Strict);
        _tokens = new DigestUnsubscribeTokens(new EphemeralDataProtectionProvider());

        _watches.Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<int> { 10 });
        _watches.Setup(x => x.GetLastWatchedUtcBySeriesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [1] = Today.AddDays(-2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) });

        _sut = new DigestComposer(
            _tracked.Object, _watches.Object, _tvDb.Object, _tokens,
            Options.Create(new LibraryOptions()), NullLogger<DigestComposer>.Instance);
    }

    private void Tracks(params int[] seriesIds) =>
        _tracked.Setup(x => x.GetByUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seriesIds.Select(id => new TrackedSeries { Id = Guid.NewGuid(), UserId = UserId, TvdbId = id, Name = $"Series {id}" }).ToList());

    private void Cached(int seriesId, params EpisodeSummary[] episodes) =>
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesAggregate { TvdbId = seriesId, Name = $"Series {seriesId}", Episodes = episodes });

    private static EpisodeSummary Ep(int id, int season, int number, int daysFromToday) =>
        new() { Id = id, SeasonNumber = season, EpisodeNumber = number, Name = $"S{season}E{number}", Aired = Today.AddDays(daysFromToday) };

    [Test]
    public async Task Compose_Should_ReadSeriesFromTheCachesOnly_NeverFromTheApi()
    {
        Tracks(1);
        Cached(1, Ep(10, 1, 1, -30), Ep(11, 1, 2, -1));

        var digest = await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl);

        digest.Email.Should().NotBeNull();
        digest.Content.ReadyToWatch.Items.Should().ContainSingle();
        // The mock is strict: any call other than the cache-only read (the layered read that can reach TheTVDB,
        // an episode lookup, a search) would have thrown.
        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _tvDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Compose_Should_LeaveOutASeriesThatIsNotCached_OrCannotBeRead()
    {
        Tracks(1, 2, 3);
        Cached(1, Ep(10, 1, 1, -30), Ep(11, 1, 2, -1));
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync((SeriesAggregate?)null);
        _tvDb.Setup(x => x.GetCachedSeriesAggregateAsync(3, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("redis down"));

        var digest = await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl);

        digest.Content.ReadyToWatch.Items.Select(l => l.SeriesId).Should().Equal(1);
    }

    [Test]
    public async Task Compose_Should_ReuseSeriesAlreadyReadInThisRun()
    {
        Tracks(1);
        Cached(1, Ep(10, 1, 1, -30), Ep(11, 1, 2, -1));
        var shared = new Dictionary<int, SeriesAggregate?>();

        await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl, shared);
        await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl, shared);

        _tvDb.Verify(x => x.GetCachedSeriesAggregateAsync(1, It.IsAny<CancellationToken>()), Times.Once,
            "many users track the same series; one run reads each once");
    }

    [Test]
    public async Task Compose_Should_ProduceNoEmail_WhenThereIsNothingToSay()
    {
        Tracks(1);
        Cached(1, Ep(10, 1, 1, -30));

        var digest = await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl);

        digest.Content.IsEmpty.Should().BeTrue();
        digest.Email.Should().BeNull();
    }

    [Test]
    public async Task Compose_Should_GiveBothUnsubscribeAddresses_ATokenForThisUser()
    {
        Tracks(1);
        Cached(1, Ep(10, 1, 1, -30), Ep(11, 1, 2, -1));

        var digest = await _sut.ComposeAsync(UserId, "saga", Now, BaseUrl);

        digest.OneClickUnsubscribeUrl.Should().StartWith("https://recall.example/Digest/OneClick?token=");
        var token = digest.OneClickUnsubscribeUrl.Split("token=")[1];
        _tokens.TryRead(token, out var userId).Should().BeTrue();
        userId.Should().Be(UserId);

        digest.Email!.TextBody.Should().Contain($"https://recall.example/Digest/Unsubscribe?token={token}",
            "the link in the body goes to the page with the button; the header address is for mail clients");
        digest.Email.HtmlBody.Should().Contain("/Digest/Unsubscribe?token=");
    }
}
