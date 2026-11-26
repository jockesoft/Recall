namespace Recall.Web.Extensions;

public static class CancellationExceptionExtensions
{
    /// <summary>
    /// The catch filter for one item of a batch (a job's series, email, user,
    /// list), or for a page handler's error handling: true for every way the
    /// work can fail, and false only when <paramref name="cancellationToken"/>
    /// (the caller's own: the job's, or the request's <c>RequestAborted</c>)
    /// has been cancelled, which must end the run (or the request, which
    /// ASP.NET Core then logs at Debug, not as an error).
    /// <para>
    /// <c>ex is not OperationCanceledException</c> is not enough: a request
    /// that times out throws one too. <c>HttpClient.Timeout</c>, TheTVDB's
    /// attempt timeout and OMDb's client timeout all end in a
    /// <see cref="TaskCanceledException"/>, so that filter let one slow
    /// TheTVDB or OMDb request end a whole job run as if the job had been
    /// stopped. A timeout is a failed item: logged, the stored data kept, and
    /// the next item tried.
    /// </para>
    /// </summary>
    public static bool IsNotCancellationBy(this Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// A cancellation nobody asked for: a request that ran out of time
    /// (<c>HttpClient.Timeout</c>, TheTVDB's attempt timeout), while
    /// <paramref name="cancellationToken"/> (the caller's own) is not cancelled.
    /// A page treats it like the upstream failing (the Details pages' 503 state);
    /// a visitor who went away (<c>HttpContext.RequestAborted</c>) is not one.
    /// </summary>
    public static bool IsTimeoutFor(this Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
