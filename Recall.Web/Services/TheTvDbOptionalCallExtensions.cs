using Recall.Web.Services.External.TheTvDb;

namespace Recall.Web.Services;

public static class TheTvDbOptionalCallExtensions
{
    /// <summary>
    /// Awaits a "nice to have" TheTVDB call — a translation, typically — that
    /// shouldn't fail the caller just because TheTVDB doesn't have it or briefly
    /// errors: on <see cref="TheTvDbApiException"/>, logs at
    /// <paramref name="logLevel"/> and returns <c>null</c> instead of propagating.
    /// A genuine failure of the caller's primary (non-optional) fetch should
    /// still be awaited separately and allowed to propagate.
    /// </summary>
    public static async Task<T?> AsOptionalAsync<T>(
        this Task<T?> task,
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
        catch (TheTvDbApiException ex)
        {
            logger.Log(logLevel, ex, message, args);
            return null;
        }
    }
}
