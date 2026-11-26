using Recall.Web.Extensions;
using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Services;

public static class TheTvDbOptionalCallExtensions
{
    /// <summary>
    /// Awaits a "nice to have" TheTVDB call — a translation, typically — that
    /// shouldn't fail the caller just because TheTVDB doesn't have it, briefly
    /// errors or doesn't answer in time: on <see cref="TheTvDbApiException"/>
    /// or a request timeout (<see cref="CancellationExceptionExtensions.IsTimeoutFor"/>),
    /// logs once at <paramref name="logLevel"/> and returns <c>null</c>, so the
    /// caller falls back to what it has. <paramref name="cancellationToken"/> is
    /// the caller's own: its cancellation still propagates. A genuine failure of
    /// the caller's primary (non-optional) fetch should still be awaited
    /// separately and allowed to propagate.
    /// </summary>
    public static async Task<T?> AsOptionalAsync<T>(
        this Task<T?> task,
        CancellationToken cancellationToken,
        ILogger logger,
        LogLevel logLevel,
        string message,
        params object?[] args)
        where T : class
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is TheTvDbApiException || ex.IsTimeoutFor(cancellationToken))
        {
            logger.Log(logLevel, ex, message, args);
            return null;
        }
    }
}
