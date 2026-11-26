using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Tests.TestSupport;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services;

/// <summary>
/// The best-effort TheTVDB call (a translation): TheTVDB failing or not
/// answering in time gives null and one log line; anything else, and the
/// caller's own cancellation, propagates.
/// </summary>
[TestFixture]
public class TheTvDbOptionalCallExtensionsTests
{
    [Test]
    public async Task AsOptionalAsync_Should_ReturnResult_OnSuccess()
    {
        var result = await Task.FromResult<string?>("value")
            .AsOptionalAsync(CancellationToken.None, NullLogger.Instance, LogLevel.Debug, "unused {Id}", 1);

        result.Should().Be("value");
    }

    [Test]
    public async Task AsOptionalAsync_Should_ReturnNull_WhenTheTvDbApiExceptionThrown()
    {
        var log = new RecordingLogger<TheTvDbOptionalCallExtensionsTests>();

        var result = await Throwing(new TheTvDbApiException("boom"))
            .AsOptionalAsync(CancellationToken.None, log, LogLevel.Warning, "Translation failed for {Id}", 1);

        result.Should().BeNull();
        log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, "Translation failed for 1"));
    }

    [Test]
    public async Task AsOptionalAsync_Should_ReturnNull_WhenTheRequestTimesOut()
    {
        var log = new RecordingLogger<TheTvDbOptionalCallExtensionsTests>();

        var result = await Throwing(Timeouts.Request())
            .AsOptionalAsync(CancellationToken.None, log, LogLevel.Warning, "Translation failed for {Id}", 1);

        result.Should().BeNull("a timed-out translation is as good as a failed one");
        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Test]
    public async Task AsOptionalAsync_Should_Propagate_TheCallersOwnCancellation()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var log = new RecordingLogger<TheTvDbOptionalCallExtensionsTests>();

        var act = () => Throwing(new OperationCanceledException(caller.Token))
            .AsOptionalAsync(caller.Token, log, LogLevel.Warning, "Translation failed for {Id}", 1);

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task AsOptionalAsync_Should_Propagate_OtherExceptions()
    {
        var act = () => Throwing(new InvalidOperationException("boom"))
            .AsOptionalAsync(CancellationToken.None, NullLogger.Instance, LogLevel.Debug, "unused {Id}", 1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<string?> Throwing(Exception exception)
    {
        await Task.Yield();
        throw exception;
    }
}
