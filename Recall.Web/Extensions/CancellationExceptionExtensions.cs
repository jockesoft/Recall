namespace Recall.Web.Extensions;

public static class CancellationExceptionExtensions
{
    /// <summary>
    /// The catch filter for one item of a batch (a job's series, email, user,
    /// list): true for every way the item can fail, and false only when
    /// <paramref name="cancellationToken"/> (the caller's own: the job's, or
    /// the request's) has been cancelled, which must end the run.
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
}
