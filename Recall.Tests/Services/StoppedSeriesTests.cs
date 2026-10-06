using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Services;

/// <summary>
/// "Stopped watching" in <see cref="WatchProgressService"/>: stopping and
/// resuming, which series may be stopped, and the rule that marking anything
/// watched resumes a stopped series while unmarking never changes the state.
/// </summary>
[TestFixture]
public sealed class StoppedSeriesTests
{
    private const int SeriesId = 403245;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 10, 2);
    private static readonly DateTime Now = Today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
    private static readonly DateTime StoppedEarlier = new(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc);

    private Mock<ITheTvDbService> _tvDb = null!;
    private Mock<IEpisodeWatchRepository> _watches = null!;
    private Mock<ITrackedSeriesRepository> _library = null!;
    private HashSet<int> _watched = null!;
    private string _status = null!;

    /// <summary>The library row: null when the series is not in the library.</summary>
    private TrackedSeries? _tracked;
    private WatchProgressService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tvDb = new Mock<ITheTvDbService>();
        _watches = new Mock<IEpisodeWatchRepository>();
        _library = new Mock<ITrackedSeriesRepository>();
        _watched = [];
        _status = "Continuing";
        _tracked = Tracked(stoppedUtc: null);

        // Three aired regular episodes, one to come, and an aired special.
        EpisodeSummary[] episodes =
        [
            new() { Id = 9, SeasonNumber = 0, EpisodeNumber = 1, Name = "Special", Aired = Today.AddDays(-25) },
            new() { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Name = "One", Aired = Today.AddDays(-30) },
            new() { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, Name = "Two", Aired = Today.AddDays(-20) },
            new() { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, Name = "Three", Aired = Today.AddDays(-10) },
            new() { Id = 4, SeasonNumber = 1, EpisodeNumber = 4, Name = "Four", Aired = Today.AddDays(7) }
        ];
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeriesAggregate
            {
                TvdbId = SeriesId, Name = "Silo", Status = new SeriesStatus { Name = _status }, Episodes = episodes
            });
        _tvDb.Setup(x => x.GetSeriesByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TvSeriesDetails(SeriesId, "Silo", "silo", "Overview", null, "2023-05-05", null, "Continuing"));

        // A small stand-in for the library row, with the repository's own rules.
        _library.Setup(x => x.ExistsAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _tracked is not null);
        _library.Setup(x => x.GetByUserAndTvdbIdAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _tracked);
        _library.Setup(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrackedSeries series, CancellationToken _) =>
            {
                if (_tracked is not null) return false;
                _tracked = series;
                return true;
            });
        _library.Setup(x => x.StopAsync(UserId, SeriesId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int _, DateTime stoppedUtc, CancellationToken _) =>
            {
                if (_tracked is null || _tracked.StoppedUtc is not null) return false;
                _tracked = Tracked(stoppedUtc);
                return true;
            });
        _library.Setup(x => x.ResumeAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ResumedSeries? () =>
            {
                if (_tracked?.StoppedUtc is not { } stoppedUtc) return null;
                _tracked = Tracked(stoppedUtc: null);
                return new ResumedSeries("Silo", stoppedUtc);
            });

        _watches.Setup(x => x.GetWatchedEpisodeIdsAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _watched.ToHashSet());
        _watches.Setup(x => x.IsWatchedAsync(UserId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int id, CancellationToken _) => _watched.Contains(id));
        _watches.Setup(x => x.MarkWatchedAsync(UserId, SeriesId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, int _, int id, CancellationToken _) => _watched.Add(id))
            .Returns(Task.CompletedTask);
        _watches.Setup(x => x.MarkUnwatchedAsync(UserId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, int id, CancellationToken _) => _watched.Remove(id))
            .Returns(Task.CompletedTask);
        _watches.Setup(x => x.MarkUnwatchedRangeAsync(UserId, It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IEnumerable<int> ids, CancellationToken _) => ids.Count(id => _watched.Remove(id)));
        _watches.Setup(x => x.UndoWatchedBatchAsync(UserId, SeriesId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _watches.Setup(x => x.MarkWatchedRangeAsync(
                UserId, SeriesId, It.IsAny<IEnumerable<int>>(), It.IsAny<WatchSource>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int _, IEnumerable<int> ids, WatchSource _, int? _, CancellationToken _) =>
            {
                var added = ids.Count(id => _watched.Add(id));
                return added == 0 ? WatchedBatch.Empty : new WatchedBatch(added, Now);
            });

        _sut = new WatchProgressService(
            _tvDb.Object, _watches.Object, _library.Object, Mock.Of<IRatingRepository>(),
            new FixedTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero)),
            NullLogger<WatchProgressService>.Instance);
    }

    private static TrackedSeries Tracked(DateTime? stoppedUtc) =>
        new() { Id = Guid.NewGuid(), UserId = UserId, TvdbId = SeriesId, Name = "Silo", StoppedUtc = stoppedUtc };

    private void Stopped() => _tracked = Tracked(StoppedEarlier);

    private void VerifyResumed(Times times) =>
        _library.Verify(x => x.ResumeAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), times);

    private void VerifyStopped(Times times) =>
        _library.Verify(x => x.StopAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), times);

    // ---- stopping ----------------------------------------------------------------

    [Test]
    public async Task Stop_Should_RecordNow_ForASeriesBeingWatched()
    {
        _watched.Add(1);

        var result = await _sut.StopWatchingAsync(UserId, SeriesId);

        result.Should().Be(new StopWatchingResult(StopWatchingOutcome.Stopped, "Silo"));
        _tracked!.StoppedUtc.Should().Be(Now, "the date comes from the injected clock");
    }

    [Test]
    public async Task Stop_Should_BeAllowed_ForAnUpToDateSeriesThatContinues()
    {
        _watched.UnionWith([1, 2, 3]);

        var result = await _sut.StopWatchingAsync(UserId, SeriesId);

        result.Outcome.Should().Be(StopWatchingOutcome.Stopped, "more is coming, and the user may not want to hear of it");
        _tracked!.StoppedUtc.Should().NotBeNull();
    }

    [Test]
    public async Task Stop_Should_BeRefused_ForAFinishedSeries()
    {
        _status = "Ended";
        _watched.UnionWith([1, 2, 3]);

        var result = await _sut.StopWatchingAsync(UserId, SeriesId);

        result.Should().Be(new StopWatchingResult(StopWatchingOutcome.Finished, "Silo"));
        _tracked!.StoppedUtc.Should().BeNull();
        VerifyStopped(Times.Never());
    }

    [Test]
    public async Task Stop_Should_BeAllowed_ForAnEndedSeriesThatIsNotFullyWatched()
    {
        _status = "Ended";
        _watched.Add(1);

        (await _sut.StopWatchingAsync(UserId, SeriesId)).Outcome.Should().Be(StopWatchingOutcome.Stopped);
    }

    [Test]
    public async Task Stop_Should_KeepTheEarlierDate_WhenAlreadyStopped()
    {
        Stopped();

        var result = await _sut.StopWatchingAsync(UserId, SeriesId);

        result.Should().Be(new StopWatchingResult(StopWatchingOutcome.AlreadyStopped, "Silo"));
        _tracked!.StoppedUtc.Should().Be(StoppedEarlier);
        VerifyStopped(Times.Never());
    }

    [Test]
    public async Task Stop_Should_SayNotInLibrary_AndWriteNothing_ForASeriesThatIsNotTracked()
    {
        _tracked = null;

        var result = await _sut.StopWatchingAsync(UserId, SeriesId);

        result.Should().Be(new StopWatchingResult(StopWatchingOutcome.NotInLibrary));
        VerifyStopped(Times.Never());
        _library.Verify(x => x.AddAsync(It.IsAny<TrackedSeries>(), It.IsAny<CancellationToken>()), Times.Never, "stopping never adds");
    }

    [Test]
    public async Task Stop_Should_StillStop_WhenTheSeriesStateCannotBeRead()
    {
        _tvDb.Setup(x => x.GetSeriesAggregateByIdAsync(SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache down"));

        (await _sut.StopWatchingAsync(UserId, SeriesId)).Outcome.Should().Be(StopWatchingOutcome.Stopped,
            "the user asked to stop, and stopping loses nothing");
    }

    // ---- resuming ----------------------------------------------------------------

    [Test]
    public async Task Resume_Should_ClearTheDate_AndNameTheSeries()
    {
        Stopped();

        (await _sut.ResumeWatchingAsync(UserId, SeriesId)).Should().Be("Silo");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task Resume_Should_ReturnNull_WhenThereIsNothingToResume()
    {
        (await _sut.ResumeWatchingAsync(UserId, SeriesId)).Should().BeNull("it was never stopped");

        _tracked = null;
        (await _sut.ResumeWatchingAsync(UserId, SeriesId)).Should().BeNull("it is not in the library");
    }

    [Test]
    public async Task StopThenResume_Should_BeBackWhereItStarted()
    {
        (await _sut.StopWatchingAsync(UserId, SeriesId)).Outcome.Should().Be(StopWatchingOutcome.Stopped);

        // The Undo in the toast is a resume.
        (await _sut.ResumeWatchingAsync(UserId, SeriesId)).Should().Be("Silo");

        _tracked!.StoppedUtc.Should().BeNull();
        (await _sut.StopWatchingAsync(UserId, SeriesId)).Outcome.Should().Be(StopWatchingOutcome.Stopped, "it can be stopped again");
    }

    // ---- marking resumes -----------------------------------------------------------

    [Test]
    public async Task MarkEpisodeWatched_Should_ResumeAStoppedSeries()
    {
        Stopped();

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        result.ResumedWatching.Should().Be("Silo");
        result.AddedToLibrary.Should().BeNull("it was in the library all along");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task ToggleEpisodeWatched_Should_ResumeAStoppedSeries_WhenItMarks()
    {
        Stopped();

        var result = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 2);

        result.ResumedWatching.Should().Be("Silo");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task MarkEpisodeWatchedUndoably_Should_ResumeAStoppedSeries()
    {
        Stopped();

        var result = await _sut.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 1);

        result.ResumedWatching.Should().Be("Silo");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task MarkWatchedThrough_Should_ResumeAStoppedSeries()
    {
        Stopped();

        var result = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 3);

        result.MarkedCount.Should().Be(3);
        result.ResumedWatching.Should().Be("Silo");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task MarkSeasonWatched_Should_ResumeAStoppedSeries()
    {
        Stopped();

        var result = await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1);

        result.Batch.InsertedCount.Should().Be(3);
        result.ResumedWatching.Should().Be("Silo");
        _tracked!.StoppedUtc.Should().BeNull();
    }

    [Test]
    public async Task MarkingASpecial_Should_ResumeAStoppedSeries_Too()
    {
        Stopped();

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 9);

        result.ResumedWatching.Should().Be("Silo", "any episode counts: watching a special is watching the series");
    }

    [Test]
    public async Task AMarkThatResumes_Should_StillReportCatchingUp()
    {
        Stopped();
        _watched.UnionWith([1, 2]);

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 3);

        result.ResumedWatching.Should().Be("Silo");
        result.CaughtUp.Should().NotBeNull("the two are independent: the toast says both");
        result.CaughtUp!.Finished.Should().BeFalse();
    }

    [Test]
    public async Task AMark_Should_ResumeNothing_ForASeriesThatIsNotStopped()
    {
        var single = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);
        var through = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 2);
        var season = await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1);

        single.ResumedWatching.Should().BeNull();
        through.ResumedWatching.Should().BeNull();
        season.ResumedWatching.Should().BeNull();
    }

    [Test]
    public async Task AMarkOfAnUntrackedSeries_Should_AddIt_NotResumeIt()
    {
        _tracked = null;

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);

        result.AddedToLibrary.Should().Be("Silo");
        result.ResumedWatching.Should().BeNull();
        VerifyResumed(Times.Never());
    }

    [Test]
    public async Task ARefusedMark_Should_NotResume()
    {
        Stopped();

        var notAired = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 4);
        var notInSeries = await _sut.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 777);
        var through = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 4);

        notAired.Outcome.Should().Be(EpisodeWatchOutcome.NotAired);
        notInSeries.Outcome.Should().Be(EpisodeWatchOutcome.EpisodeNotInSeries);
        through.HasAired.Should().BeFalse();
        _tracked!.StoppedUtc.Should().Be(StoppedEarlier, "nothing was marked");
        VerifyResumed(Times.Never());
    }

    [Test]
    public async Task ASeasonWithNothingLeftToMark_Should_NotResume()
    {
        _watched.UnionWith([1, 2, 3]);
        Stopped();

        var result = await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1);

        result.Batch.InsertedCount.Should().Be(0);
        result.ResumedWatching.Should().BeNull();
        _tracked!.StoppedUtc.Should().Be(StoppedEarlier);
    }

    [Test]
    public async Task AResumeThatFails_Should_NotLoseTheMark()
    {
        Stopped();
        _library.Setup(x => x.ResumeAsync(UserId, SeriesId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database down"));

        var result = await _sut.MarkEpisodeWatchedAsync(UserId, SeriesId, 1);

        result.Outcome.Should().Be(EpisodeWatchOutcome.MarkedWatched);
        result.ResumedWatching.Should().BeNull();
        _watched.Should().Contain(1);
    }

    // ---- unmarking leaves the state alone --------------------------------------------

    [Test]
    public async Task Unmarking_Should_NeitherResumeNorStop_AStoppedSeries()
    {
        _watched.UnionWith([1, 2, 3]);
        Stopped();

        var toggled = await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 3);
        var season = await _sut.MarkSeasonUnwatchedAsync(UserId, SeriesId, 1);
        var undone = await _sut.UndoWatchedBatchAsync(UserId, SeriesId, Now);

        toggled.Outcome.Should().Be(EpisodeWatchOutcome.MarkedUnwatched);
        toggled.ResumedWatching.Should().BeNull();
        season.Should().Be(2);
        undone.Should().Be(1);
        _tracked!.StoppedUtc.Should().Be(StoppedEarlier);
        VerifyResumed(Times.Never());
        VerifyStopped(Times.Never());
    }

    [Test]
    public async Task Unmarking_Should_NotStop_ASeriesBeingWatched()
    {
        _watched.UnionWith([1, 2]);

        await _sut.ToggleEpisodeWatchedAsync(UserId, SeriesId, 2);
        await _sut.MarkSeasonUnwatchedAsync(UserId, SeriesId, 1);
        await _sut.UndoWatchedBatchAsync(UserId, SeriesId, Now);

        _tracked!.StoppedUtc.Should().BeNull();
        VerifyStopped(Times.Never());
        VerifyResumed(Times.Never());
    }

    // ---- the wording every page shares ------------------------------------------------

    [Test]
    public void Clause_Should_SayWhatTheMarkDidToTheLibrary()
    {
        MarkLibraryEffect.Clause(null, null).Should().BeNull();
        MarkLibraryEffect.Clause("Silo", null).Should().Be(" and added Silo to your library");
        MarkLibraryEffect.Clause(null, "Silo").Should().Be(" and resumed watching Silo");
    }

    // ---- undoing the mark that resumed -------------------------------------------------

    [Test]
    public async Task EveryUndoableMark_Should_ReportTheStoppedDateItCleared()
    {
        Stopped();
        var oneTap = await _sut.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 1);

        Stopped();
        var through = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 2);

        Stopped();
        var season = await _sut.MarkSeasonWatchedAsync(UserId, SeriesId, 1);

        oneTap.ResumedFromStoppedUtc.Should().Be(StoppedEarlier);
        through.ResumedFromStoppedUtc.Should().Be(StoppedEarlier);
        season.ResumedFromStoppedUtc.Should().Be(StoppedEarlier);
    }

    [Test]
    public async Task AMarkThatResumedNothing_Should_ReportNoStoppedDate()
    {
        var through = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 2);

        through.ResumedWatching.Should().BeNull();
        through.ResumedFromStoppedUtc.Should().BeNull("the Undo of this mark must not stop anything");
    }

    [Test]
    public async Task UndoingTheMarkThatResumed_Should_StopTheSeriesAgain_WithItsOriginalDate()
    {
        Stopped();
        var marked = await _sut.MarkWatchedThroughAsync(UserId, SeriesId, 3);
        _tracked!.StoppedUtc.Should().BeNull("the mark resumed it");

        // What the Undo in the toast does: remove the batch, then put the date back.
        await _sut.UndoWatchedBatchAsync(UserId, SeriesId, marked.Batch!.WatchedUtc);
        var restored = await _sut.RestoreStoppedAsync(UserId, SeriesId, marked.ResumedFromStoppedUtc!.Value);

        restored.Should().BeTrue();
        _tracked!.StoppedUtc.Should().Be(StoppedEarlier, "the original date, not the time of the undo");
    }

    [Test]
    public async Task RestoreStopped_Should_KeepANewerStop_AndDoNothingForASeriesNoLongerInTheLibrary()
    {
        Stopped();
        var marked = await _sut.MarkEpisodeWatchedUndoablyAsync(UserId, SeriesId, 1);

        // Stopped again before the Undo was pressed: the newer date stands.
        await _sut.StopWatchingAsync(UserId, SeriesId);
        (await _sut.RestoreStoppedAsync(UserId, SeriesId, marked.ResumedFromStoppedUtc!.Value)).Should().BeFalse();
        _tracked!.StoppedUtc.Should().Be(Now);

        _tracked = null;
        (await _sut.RestoreStoppedAsync(UserId, SeriesId, StoppedEarlier)).Should().BeFalse();
        _tracked.Should().BeNull("restoring never adds the series back");
    }

    [Test]
    public async Task UndoingABatch_Should_NotStopByItself()
    {
        _watched.UnionWith([1, 2]);

        await _sut.UndoWatchedBatchAsync(UserId, SeriesId, Now);

        _tracked!.StoppedUtc.Should().BeNull("only the page's Undo of a resuming mark restores a stop, and it says so explicitly");
        VerifyStopped(Times.Never());
    }
}
