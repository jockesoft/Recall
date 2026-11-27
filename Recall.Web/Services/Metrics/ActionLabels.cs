using Recall.Web.Services.PendingActions;

namespace Recall.Web.Services.Metrics;

/// <summary>
/// The kinds of action a signed-out visitor can press on a Details page, as
/// they appear in the metric keys ("add-series"), with the admin page's words
/// for them.
/// </summary>
public static class ActionLabels
{
    private static readonly IReadOnlyDictionary<string, string> Labels = Enum.GetValues<PendingActionKind>()
        .ToDictionary(PendingAction.Slug, kind => kind switch
        {
            PendingActionKind.AddSeries => "Add a series to the library",
            PendingActionKind.LikeSeries => "Like a series",
            PendingActionKind.RateSeries => "Rate a series",
            PendingActionKind.MarkMovieWatched => "Mark a movie watched",
            PendingActionKind.AddMovieToWatchlist => "Add a movie to the watchlist",
            PendingActionKind.LikeMovie => "Like a movie",
            PendingActionKind.RateMovie => "Rate a movie",
            PendingActionKind.MarkEpisodeWatched => "Mark an episode watched",
            PendingActionKind.LikeEpisode => "Like an episode",
            _ => "Rate an episode"
        });

    /// <summary>The kinds' slugs, in the order the admin page lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = Enum.GetValues<PendingActionKind>().Select(PendingAction.Slug).ToList();

    public static string For(string slug) => Labels.GetValueOrDefault(slug) ?? slug;
}
