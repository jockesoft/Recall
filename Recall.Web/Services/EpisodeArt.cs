using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services;

/// <summary>
/// The image shown for an episode: its still, or the art standing in for one.
/// </summary>
/// <param name="Url">Null when there is nothing to show; the page then draws its placeholder.</param>
/// <param name="IsStill">
/// True when <paramref name="Url"/> is the episode's own still; false when it
/// is the series' background art, or nothing.
/// </param>
public sealed record EpisodeArt(string? Url, bool IsStill)
{
    public static EpisodeArt None { get; } = new(null, false);

    public bool HasImage => !string.IsNullOrWhiteSpace(Url);

    /// <summary>The still, for uses that must not pass off series art as the episode's (structured data).</summary>
    public string? StillUrl => IsStill ? Url : null;

    /// <summary>
    /// The one rule every page follows (Episode Details, the Dashboard's
    /// continue-watching cards, Favorites), so the same episode never shows a
    /// still on one page and an empty frame on another:
    /// <list type="number">
    /// <item>the episode's still in the series aggregate;</item>
    /// <item>the still on the episode's own cached record, which can have one
    /// before the aggregate is next refreshed;</item>
    /// <item>the series' background art (16:9 like a still; never the poster,
    /// which is the wrong shape);</item>
    /// <item>nothing: the page's placeholder.</item>
    /// </list>
    /// TheTVDB has no still at all for many episodes (a third of the older
    /// ones in the dev data), so the fallback is the normal case for them, not
    /// a transient one.
    /// </summary>
    public static EpisodeArt Resolve(SeriesAggregate? series, int episodeId, Episode? record)
    {
        var still = StillInAggregate(series, episodeId);
        if (string.IsNullOrWhiteSpace(still))
            still = record?.Image;

        if (!string.IsNullOrWhiteSpace(still))
            return new EpisodeArt(still, IsStill: true);

        return string.IsNullOrWhiteSpace(series?.BackgroundUrl)
            ? None
            : new EpisodeArt(series.BackgroundUrl, IsStill: false);
    }

    /// <summary>The episode's still as the series aggregate has it, or null.</summary>
    public static string? StillInAggregate(SeriesAggregate? series, int episodeId)
    {
        var image = series?.Episodes.FirstOrDefault(e => e.Id == episodeId)?.Image;
        return string.IsNullOrWhiteSpace(image) ? null : image;
    }
}
