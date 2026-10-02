namespace Recall.Web.Pages.Shared;

/// <summary>
/// Drives the shared "_FavoriteEpisodeCard" partial — a small card for a liked
/// episode: the still on top, then the series name, "S02E03" with the episode
/// title, and the air date. The whole card links to Episodes/Details. Put a
/// run of these inside <c>&lt;div class="tvdb-fav-episode-grid"&gt;</c>.
/// </summary>
public sealed class FavoriteEpisodeCardModel
{
    /// <summary>TheTVDB episode id — used to build the Episodes/Details link.</summary>
    public required int EpisodeTvdbId { get; init; }

    /// <summary>Parent series name — the bold first line.</summary>
    public required string SeriesName { get; init; }

    public string? EpisodeName { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }

    /// <summary>Episode still URL. Null or blank renders the "No image" placeholder.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>When the episode aired; null hides the date line.</summary>
    public DateOnly? Aired { get; init; }

    /// <summary>The page's "today" (UTC), for the date format.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>"S02E03" when both numbers are known, otherwise null.</summary>
    public string? SlateCode =>
        SeasonNumber is { } s && EpisodeNumber is { } e ? $"S{s:D2}E{e:D2}" : null;
}
