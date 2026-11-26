namespace Recall.Tests.TestSupport;

/// <summary>
/// The two cancellations a job tells apart: a request that ran out of time
/// (a failed item: logged, the next one tried) and the job's own
/// cancellation (the end of the run), which a test plays by cancelling the
/// token it passed to the job in a mock's callback and throwing
/// <c>new OperationCanceledException(job.Token)</c>.
/// </summary>
public static class Timeouts
{
    /// <summary>
    /// What a TheTVDB or OMDb request that ran out of time throws:
    /// <c>HttpClient.Timeout</c>'s shape, which TheTVDB's attempt timeout copies.
    /// It is an <see cref="OperationCanceledException"/>, though nobody cancelled the job.
    /// </summary>
    public static TaskCanceledException Request() =>
        new("The request was canceled due to the configured HttpClient.Timeout of 8 seconds elapsing.", new TimeoutException());
}
