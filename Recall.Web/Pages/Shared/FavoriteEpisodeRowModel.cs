namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_FavoriteEpisodeRow" partial — a compact, full-width row
/// (parent series name in bold, then "S02E03 · Episode title", with the episode
/// still on the right) that links to Episodes/Details. Used on Account/Favorites.
/// </summary>
public sealed class FavoriteEpisodeRowModel
{
    /// <summary>TheTVDB episode id — used to build the Episodes/Details link.</summary>
    public required int EpisodeTvdbId { get; init; }

    /// <summary>Parent series name — the bold first line.</summary>
    public required string SeriesName { get; init; }

    public string? EpisodeName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    /// <summary>Episode still URL. Null or blank renders a "No art" placeholder.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>"S02E03" when both numbers are known, otherwise an empty string.</summary>
    public string SlateCode =>
        SeasonNumber is { } s && EpisodeNumber is { } e ? $"S{s:D2}E{e:D2}" : string.Empty;

    /// <summary>The second line: slate code and episode title joined by " · ".</summary>
    public string SubLine =>
        string.Join(" · ", new[] { SlateCode, EpisodeName }.Where(p => !string.IsNullOrWhiteSpace(p)));
}
