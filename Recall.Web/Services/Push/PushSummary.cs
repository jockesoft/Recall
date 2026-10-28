using System.Text.RegularExpressions;
using Recall.Web.Infrastructure.Persistence.Repositories;

namespace Recall.Web.Services.Push;

/// <summary>
/// One push for any number of new-episode notifications. One episode names
/// it and opens it ("Silo: S02E05 is out"); several are counted and open the
/// Notifications page ("3 new episodes: Silo, The Bear and 1 more").
/// </summary>
public static partial class PushSummary
{
    public const string NotificationsUrl = "/Account/Notifications";

    /// <summary>How many series a summary names before "and N more".</summary>
    private const int NamedSeries = 2;

    /// <summary>The push for <paramref name="notices"/>, or null when there is nothing to tell.</summary>
    public static PushPayload? For(IReadOnlyList<UnreadEpisodeNotice> notices)
    {
        if (notices.Count == 0)
            return null;

        var episodes = notices.Sum(n => Math.Max(1, n.EpisodeCount));

        if (episodes == 1)
        {
            var notice = notices[0];
            var title = notice.EpisodeCode is { } code
                ? $"{SeriesName(notice)}: {code} is out"
                : $"{SeriesName(notice)}: a new episode is out";
            var url = notice.EpisodeTvdbId is > 0 ? $"/Episodes/Details/{notice.EpisodeTvdbId}" : NotificationsUrl;
            return new PushPayload(title, string.Empty, url, PushPayload.AppIcon);
        }

        var names = notices.Select(SeriesName).Distinct(StringComparer.Ordinal).ToList();
        return new PushPayload($"{episodes} new episodes: {Join(names)}", string.Empty, NotificationsUrl, PushPayload.AppIcon);
    }

    /// <summary>"Silo", "Silo and The Bear", "Silo, The Bear and 2 more".</summary>
    private static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(NamedSeries))} and {names.Count - NamedSeries} more"
    };

    /// <summary>
    /// The series' name. A notification written before the name was stored has
    /// it only in its title ("New episode of Silo", "3 new episodes of Silo").
    /// </summary>
    private static string SeriesName(UnreadEpisodeNotice notice) =>
        notice.SeriesName
        ?? (TitlePrefix().Match(notice.Title) is { Success: true } match ? notice.Title[match.Length..] : notice.Title);

    [GeneratedRegex(@"^(New episode|\d+ new episodes) of ")]
    private static partial Regex TitlePrefix();
}
