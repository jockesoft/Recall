using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Recall.Web.Services;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Tests.Services;

[TestFixture]
public class TheTvDbOptionalCallExtensionsTests
{
    [Test]
    public async Task AsOptionalAsync_Should_ReturnResult_OnSuccess()
    {
        var result = await Task.FromResult<string?>("value")
            .AsOptionalAsync(NullLogger.Instance, LogLevel.Debug, "unused {Id}", 1);

        result.Should().Be("value");
    }

    [Test]
    public async Task AsOptionalAsync_Should_ReturnNull_WhenTheTvDbApiExceptionThrown()
    {
        async Task<string?> Throwing()
        {
            throw new TheTvDbApiException("boom");
        }

        var result = await Throwing().AsOptionalAsync(NullLogger.Instance, LogLevel.Debug, "unused {Id}", 1);

        result.Should().BeNull();
    }

    [Test]
    public void AsOptionalAsync_Should_Propagate_OtherExceptions()
    {
        async Task<string?> Throwing()
        {
            throw new InvalidOperationException("boom");
        }

        Func<Task> act = () => Throwing().AsOptionalAsync(NullLogger.Instance, LogLevel.Debug, "unused {Id}", 1);

        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
