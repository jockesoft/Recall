using System.Globalization;

namespace Recall.Web.Services.PendingActions;

/// <summary>
/// Something a signed-out visitor pressed on a Details page, kept (in the
/// <see cref="IPendingActions"/> cookie) until they have signed in, and then
/// done for them. Only these kinds exist: the allowlist is the enum.
/// </summary>
public enum PendingActionKind
{
    AddSeries,
    LikeSeries,
    RateSeries,
    MarkMovieWatched,
    AddMovieToWatchlist,
    LikeMovie,
    RateMovie,
    MarkEpisodeWatched,
    LikeEpisode,
    RateEpisode
}

/// <summary>
/// One pending action: its kind, the TheTVDB id it is about (the series', the
/// movie's or the episode's, by <see cref="Kind"/>), and for a rating its
/// value. Nothing else: the page it came from follows from these
/// (<see cref="ReturnUrl"/>).
/// </summary>
public sealed record PendingAction(PendingActionKind Kind, int TvdbId, int? Rating = null)
{
    /// <summary>A kind that is defined, a positive id, and a rating from 1 to 10 exactly when the kind is a rating.</summary>
    public bool IsValid =>
        Enum.IsDefined(Kind)
        && TvdbId > 0
        && (IsRating ? Rating is >= 1 and <= 10 : Rating is null);

    public bool IsRating => Kind is PendingActionKind.RateSeries or PendingActionKind.RateMovie or PendingActionKind.RateEpisode;

    /// <summary>What the id is of.</summary>
    public PendingTitle Title => Kind switch
    {
        PendingActionKind.AddSeries or PendingActionKind.LikeSeries or PendingActionKind.RateSeries => PendingTitle.Series,
        PendingActionKind.MarkEpisodeWatched or PendingActionKind.LikeEpisode or PendingActionKind.RateEpisode => PendingTitle.Episode,
        _ => PendingTitle.Movie
    };

    /// <summary>The Details page the action was pressed on, and where sign-in returns to: always local, built here, never taken from the request.</summary>
    public string ReturnUrl => Title switch
    {
        PendingTitle.Series => $"/Series/Details/{TvdbId.ToString(CultureInfo.InvariantCulture)}",
        PendingTitle.Episode => $"/Episodes/Details/{TvdbId.ToString(CultureInfo.InvariantCulture)}",
        _ => $"/Movies/Details/{TvdbId.ToString(CultureInfo.InvariantCulture)}"
    };

    /// <summary>"add-series", "rate-episode": the kind in a site metric key.</summary>
    public static string Slug(PendingActionKind kind) => kind switch
    {
        PendingActionKind.AddSeries => "add-series",
        PendingActionKind.LikeSeries => "like-series",
        PendingActionKind.RateSeries => "rate-series",
        PendingActionKind.MarkMovieWatched => "watched-movie",
        PendingActionKind.AddMovieToWatchlist => "watchlist-movie",
        PendingActionKind.LikeMovie => "like-movie",
        PendingActionKind.RateMovie => "rate-movie",
        PendingActionKind.MarkEpisodeWatched => "watched-episode",
        PendingActionKind.LikeEpisode => "like-episode",
        PendingActionKind.RateEpisode => "rate-episode",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

/// <summary>What a pending action's id is of.</summary>
public enum PendingTitle
{
    Series,
    Movie,
    Episode
}
