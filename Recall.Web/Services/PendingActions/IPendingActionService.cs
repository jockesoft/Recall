using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.PendingActions;

/// <summary>
/// What a pending action says on the sign-in page, and doing it once the
/// visitor has signed in.
/// </summary>
public interface IPendingActionService
{
    /// <summary>
    /// The sign-in page's sentence for the action ("Sign in to add Breaking Bad
    /// to your library."). Reads the caches only: the sign-in page is public
    /// and spends no TheTVDB quota. A title that is not cached is "this series".
    /// </summary>
    Task<PendingActionPrompt> DescribeAsync(PendingAction action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Does the action for <paramref name="userId"/> through the same services
    /// the Details pages use, with their checks, and says what happened for the
    /// toast. Never throws for a failure of the action itself (the sign-in has
    /// already happened); the caller's own cancellation still propagates.
    /// </summary>
    Task<PendingActionOutcome> CompleteAsync(Guid userId, PendingAction action, CancellationToken cancellationToken = default);
}

/// <summary>The sign-in page's context: the sentence, the one after it, and the title's poster when it is cached.</summary>
public sealed record PendingActionPrompt(string Lead, string Detail, string? PosterUrl);

public enum PendingOutcomeTone
{
    Success,
    Info,
    Error
}

/// <summary>
/// What a pending action did, for the toast after sign-in. A success may carry
/// an Undo: of an add (<see cref="UndoAdd"/>), or of a mark (<see cref="Batch"/>
/// with the series, as the Details pages' marks do).
/// </summary>
public sealed record PendingActionOutcome(PendingOutcomeTone Tone, string Message)
{
    /// <summary>The series added to the library or the movie put on the watchlist, which the toast's Undo takes back.</summary>
    public PendingUndo? UndoAdd { get; init; }

    /// <summary>The series of a marked episode, for the Undo of the mark.</summary>
    public int? WatchedSeriesId { get; init; }

    public WatchedBatch? Batch { get; init; }

    public SeriesCaughtUp? CaughtUp { get; init; }

    public DateTime? ResumedFromStoppedUtc { get; init; }
}

/// <summary>The Undo of an add: the series (out of the library again) or the movie (off the watchlist again).</summary>
public sealed record PendingUndo(PendingTitle Title, int TvdbId);
