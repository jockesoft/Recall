using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Pages.Shared;
using Recall.Web.Services;
using Recall.Web.Services.Digest;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages;

// ---------------------------------------------------------------------------
// View DTOs — match the properties used in Index.cshtml
// ---------------------------------------------------------------------------

public sealed class UpcomingEpisodeItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";
    public string? ImageUrl { get; init; }
    public DateOnly AiredDate { get; init; }

    /// <summary>When it is released (<see cref="EpisodeRelease"/>): what the Upcoming groups and the card's time come from.</summary>
    public required ReleaseMoment Release { get; init; }

    /// <summary>TheTVDB finale marker ("season", "series", "midseason"), if any.</summary>
    public string? FinaleType { get; init; }

    /// <summary>True when the user has watched every aired episode of this series.</summary>
    public bool SeriesCaughtUp { get; init; }
}

public sealed record CatchUpItem
{
    public int SeriesId { get; init; }
    public string SeriesName { get; init; } = "";
    public int EpisodeId { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string Name { get; init; } = "";

    /// <summary>
    /// The next episode's art by the rule every page shares
    /// (<see cref="EpisodeArt.Resolve"/>): its still, else the series'
    /// background art (also 16:9), else null and the card shows a dark
    /// placeholder. Never the poster: a portrait cover cropped to 16:9 shows a
    /// strip of it.
    /// </summary>
    public string? ImageUrl { get; init; }
}

/// <summary>
/// One of the three figures in the Dashboard's header, and where it leads:
/// a page, optionally one of its sections (<c>?section=</c>) or a fragment.
/// </summary>
/// <param name="ShortLabel">
/// The label on a phone, one line ("This week"); <paramref name="Label"/> is
/// the full one, shown from 576px and always part of the link's name.
/// </param>
public sealed record DashboardStat(int Value, string Label, string ShortLabel, string Page, string? Section = null, string? Fragment = null);

// ---------------------------------------------------------------------------
// Page model
// ---------------------------------------------------------------------------

[Authorize]
public sealed partial class DashboardModel(
    ITheTvDbService theTvDbService,
    ITrackedSeriesRepository libraryRepository,
    IEpisodeWatchRepository watchedRepository,
    IWatchProgressService watchProgressService,
    ILogger<DashboardModel> logger,
    ICurrentUserService currentUserService,
    TimeProvider timeProvider,
    IOptions<LibraryOptions> libraryOptions,
    IAppUserRepository userRepository,
    IOptions<DigestOptions> digestOptions) : PageModel
{
    /// <summary>Today's date in UTC — the date every air-date comparison on this page (and its view) uses.</summary>
    public DateOnly Today => AirDate.Today(timeProvider);

    private const int UpcomingWindowDays = 30;
    private const int ThisWeekWindowDays = 7;

    /// <summary>
    /// How many series "Continue watching" shows before "Show all N": the first
    /// ones of the queue (<see cref="ContinueWatchingOrder"/>). Every series is
    /// on the page; the stylesheet hides the rest by count until the list is
    /// expanded (in place, or with <c>?all=true</c> without script).
    /// </summary>
    public const int ContinueWatchingLimit = 9;

    /// <summary>The Library's one-section view of Watching (<c>/Library?section=watching</c>).</summary>
    public const string LibraryWatchingSection = "watching";

    /// <summary>Every series in the queue (dormant ones left out): the N of "Show all N".</summary>
    public int ContinueWatchingTotal => CatchUpEpisodes.Count;

    /// <summary>Whether there are more than <see cref="ContinueWatchingLimit"/>, so "Show all N" is offered.</summary>
    public bool HasMoreToContinue => ContinueWatchingTotal > ContinueWatchingLimit;

    /// <summary>
    /// Continue watching expanded (<c>?all=true</c>): the page's state without
    /// script, and what a mark posted from an expanded list sends back, so the
    /// page returns expanded.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "all")]
    public bool ShowAll { get; set; }

    /// <summary>
    /// Posted by a card's or row's forms ("catchup-card-123", "catchup-row-123"):
    /// the redirect after the action ends at that card or row. Anything else is ignored.
    /// </summary>
    [BindProperty]
    public string? Anchor { get; set; }

    /// <summary>
    /// The header's three figures, each a link to what explains it: Tracked to
    /// the Library, Airing this week to Upcoming below, Unwatched to the
    /// Library's Watching section.
    /// </summary>
    public IReadOnlyList<DashboardStat> Stats =>
    [
        new(TrackedSeriesCount, "Tracked series", "Tracked", "/Library"),
        new(UpcomingThisWeekCount, "Airing this week", "This week", "/Dashboard", Fragment: "upcoming"),
        new(UnwatchedCount, "Unwatched episodes", "Unwatched", "/Library", Section: LibraryWatchingSection)
    ];

    public int TrackedSeriesCount { get; private set; }
    public int UpcomingThisWeekCount { get; private set; }
    public int UnwatchedCount { get; private set; }
    public List<UpcomingEpisodeItem> UpcomingEpisodes { get; private set; } = [];
    public List<CatchUpItem> CatchUpEpisodes { get; private set; } = [];

    /// <summary>
    /// Series in the queue that the user hasn't watched in a while. They get no
    /// card here: the page links to them in the Library instead.
    /// </summary>
    public int DormantSeriesCount { get; private set; }

    /// <summary>
    /// The one-time offer of the weekly email: shown to someone with a library
    /// who has neither switched the digest on nor said "No thanks", and only
    /// where the digest is enabled.
    /// </summary>
    public bool ShowDigestPrompt { get; private set; }

    /// <summary>The day the digest goes out, for the offer's wording.</summary>
    public DayOfWeek DigestDay => digestOptions.Value.DayOfWeek;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");
        var trackedSeriesIds = await libraryRepository.GetByUserAsync(userId, cancellationToken);
        TrackedSeriesCount = trackedSeriesIds.Count;

        if (trackedSeriesIds.Count == 0)
            return;

        ShowDigestPrompt = await ShouldOfferDigestAsync(userId, cancellationToken);

        // Everything below is about what there is to watch, so a series the
        // user stopped watching is left out of all of it: Continue watching,
        // Upcoming and the two counts beside "Tracked series" (which still
        // counts it: it is in the library).
        var followed = SeriesLibraryStateRule.Followed(trackedSeriesIds);

        var aggregates = (await Task.WhenAll(
                followed.Select(id => theTvDbService.TryGetSeriesAggregateAsync(id.TvdbId, logger, nameof(DashboardModel), cancellationToken))))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        var seriesIds = aggregates.Select(a => a.TvdbId).ToList();
        var watchedIds = await watchedRepository.GetWatchedEpisodeIdsAsync(userId, seriesIds, cancellationToken);

        var today = Today;
        var now = AirDate.Now(timeProvider);
        var upcomingCutoff = today.AddDays(UpcomingWindowDays);
        var thisWeekCutoff = today.AddDays(ThisWeekWindowDays);

        var upcoming = new List<UpcomingEpisodeItem>();
        var catchUp = new List<CatchUpItem>();
        var recentPremieres = new HashSet<int>();
        var unwatchedTotal = 0;

        foreach (var aggregate in aggregates)
        {
            // Next episode to watch + unwatched count: shared logic, same rule as
            // the series page (earliest aired episode not marked watched).
            var progress = watchProgressService.BuildProgress(aggregate.TvdbId, aggregate.ToWatchableEpisodes(), watchedIds);
            unwatchedTotal += progress.UnwatchedReleasedCount;

            var seriesCaughtUp = progress.UnwatchedReleasedCount == 0;

            // Upcoming: not released yet (EpisodeRelease), airing within the window.
            // An episode airing tonight in the US is here until it airs.
            foreach (var (ep, release) in aggregate.Episodes
                         .Select(e => (Episode: e, Release: EpisodeRelease.MomentUtc(e.Aired, aggregate.AirsTime, aggregate.OriginalCountry)))
                         .Where(x => x.Release is { } r && !r.IsReleasedBy(now) && r.AirDate <= upcomingCutoff))
            {
                upcoming.Add(new UpcomingEpisodeItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = ep.Id,
                    SeasonNumber = ep.SeasonNumber,
                    EpisodeNumber = ep.EpisodeNumber,
                    Name = ep.Name,
                    ImageUrl = aggregate.ImageUrl,
                    AiredDate = ep.Aired!.Value,
                    Release = release!,
                    FinaleType = ep.FinaleType,
                    SeriesCaughtUp = seriesCaughtUp
                });
            }

            if (progress.NextUnwatchedEpisode is { } next)
            {
                if (ContinueWatchingOrder.HasRecentPremiere(progress.OrderedEpisodes, now, libraryOptions.Value.PremiereReturnDays))
                    recentPremieres.Add(aggregate.TvdbId);

                // The image is filled in below, for the cards that are shown.
                catchUp.Add(new CatchUpItem
                {
                    SeriesId = aggregate.TvdbId,
                    SeriesName = aggregate.Name,
                    EpisodeId = next.Id,
                    SeasonNumber = next.SeasonNumber,
                    EpisodeNumber = next.EpisodeNumber,
                    Name = next.Name
                });
            }
        }

        UpcomingEpisodes = [.. upcoming.OrderBy(e => e.Release.Utc)];

        // "Continue watching": what the user is in the middle of comes first,
        // and what they haven't touched in a while is left to the Library
        // (ContinueWatchingOrder has both rules, shared with the Library). The
        // unwatched count above still includes those series.
        var lastWatchedBySeries = await watchedRepository.GetLastWatchedUtcBySeriesAsync(userId, cancellationToken);

        var queue = ContinueWatchingOrder.Arrange(
            catchUp,
            c => c.SeriesId,
            c => c.SeriesName,
            lastWatchedBySeries,
            ContinueWatchingOrder.AddedUtc(followed),
            recentPremieres,
            today,
            libraryOptions.Value);

        var orderedCatchUp = queue.Active.ToList();
        DormantSeriesCount = queue.Dormant.Count;

        // Every series is on the page (the rest are hidden until expanded), so
        // every one gets its image; only an episode without a still in the
        // series aggregate costs a lookup of its own record.
        CatchUpEpisodes = await WithArtAsync(orderedCatchUp, aggregates.ToDictionary(a => a.TvdbId), cancellationToken);
        UpcomingThisWeekCount = upcoming.Count(e => e.Release.GroupDate <= thisWeekCutoff);
        UnwatchedCount = unwatchedTotal;
    }

    /// <summary>
    /// Gives each "Continue watching" card its image, by the same rule Episode
    /// Details and Favorites use (<see cref="EpisodeArt.Resolve"/>). A card
    /// whose still is already in the series aggregate costs no lookup; only an
    /// episode without one has its own record read.
    /// </summary>
    private async Task<List<CatchUpItem>> WithArtAsync(
        List<CatchUpItem> items,
        IReadOnlyDictionary<int, SeriesAggregate> aggregatesBySeries,
        CancellationToken cancellationToken)
    {
        var art = await Task.WhenAll(items.Select(item =>
            theTvDbService.GetEpisodeArtAsync(
                aggregatesBySeries.GetValueOrDefault(item.SeriesId), item.EpisodeId, logger, cancellationToken: cancellationToken)));

        return items.Zip(art, (item, image) => item with { ImageUrl = image.Url }).ToList();
    }

    /// <summary>A failed lookup just means no offer this time; it must not take the Dashboard down.</summary>
    private async Task<bool> ShouldOfferDigestAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!digestOptions.Value.Enabled)
            return false;

        try
        {
            return await userRepository.GetByIdAsync(userId, cancellationToken)
                is { DigestOptedInUtc: null, DigestPromptDismissedUtc: null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the digest preference for the dashboard.");
            return false;
        }
    }

    /// <summary>"Turn it on" on the one-time offer: the same opt-in as the switch on Profile.</summary>
    public async Task<IActionResult> OnPostDigestOptInAsync(CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

        if (!digestOptions.Value.Enabled)
            return RedirectToPage();

        try
        {
            await userRepository.SetDigestOptInAsync(userId, optedIn: true, cancellationToken);
            this.SetSuccessToast($"Weekly email is on. It arrives on {DigestDay}s, when there is something new. You can turn it off in your profile.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed switching the weekly digest on for user {UserId}.", userId);
            this.SetErrorToast("Could not turn the weekly email on right now.");
        }

        return RedirectToPage();
    }

    /// <summary>"No thanks" on the one-time offer: remembered, so it is not shown again. The switch on Profile stays.</summary>
    public async Task<IActionResult> OnPostDigestDismissAsync(CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

        try
        {
            await userRepository.DismissDigestPromptAsync(userId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed recording the dismissed digest offer for user {UserId}.", userId);
        }

        return RedirectToPage();
    }


    /// <summary>
    /// The catch-up card's one-tap "watched" button. There is no confirmation
    /// and the card is gone after the redirect, so the success toast carries an
    /// Undo (the same one a bulk mark offers, for a batch of one).
    /// </summary>
    public async Task<IActionResult> OnPostMarkWatchedAsync(int seriesId, int episodeId, CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId  ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

        try
        {
            var result = await watchProgressService.MarkEpisodeWatchedUndoablyAsync(userId, seriesId, episodeId, cancellationToken);

            switch (result.Outcome)
            {
                case EpisodeWatchOutcome.MarkedWatched when result.Batch is { InsertedCount: > 0 } batch:
                    this.SetSuccessToastWithWatchedUndo(
                        await OneTapMarkToast.DescribeAsync(theTvDbService, logger, nameof(DashboardModel), seriesId, episodeId, result.ResumedWatching, cancellationToken),
                        seriesId,
                        batch,
                        undoSingle: true,
                        caughtUp: result.CaughtUp,
                        today: Today,
                        resumedFromStoppedUtc: result.ResumedFromStoppedUtc);
                    break;
                case EpisodeWatchOutcome.EpisodeNotInSeries:
                    this.SetErrorToast("That episode doesn't belong to this series.");
                    break;
                case EpisodeWatchOutcome.NotAired:
                    this.SetErrorToast("You can't mark an episode as watched before it has aired.");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed marking episode {EpisodeId} of series {SeriesId} watched from the dashboard.", episodeId, seriesId);
            this.SetErrorToast("Could not update watched status right now.");
        }

        return BackToList();
    }

    /// <summary>
    /// Back to the Dashboard after a card's or row's action: expanded when it
    /// was posted from an expanded list, at the card or row it came from.
    /// </summary>
    private RedirectToPageResult BackToList() =>
        RedirectToPage(pageName: null, pageHandler: null, routeValues: ShowAll ? new { all = "true" } : null,
            fragment: Anchor is { } anchor && AnchorPattern().IsMatch(anchor) ? anchor : null);

    [System.Text.RegularExpressions.GeneratedRegex(@"^catchup-(card|row)-\d{1,10}$")]
    private static partial System.Text.RegularExpressions.Regex AnchorPattern();


    /// <summary>
    /// "Stop watching" in a Continue watching card's menu. No confirmation: the
    /// card is gone after the redirect and the toast carries an Undo, and
    /// nothing is lost either way (the series stays in the Library, under Stopped).
    /// </summary>
    public async Task<IActionResult> OnPostStopWatchingAsync(int seriesId, CancellationToken cancellationToken)
    {
        var userId = currentUserService.UserId ?? throw new InvalidOperationException("No authenticated user id found on the current request.");

        try
        {
            this.SetStopWatchingToast(await watchProgressService.StopWatchingAsync(userId, seriesId, cancellationToken), seriesId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed stopping series {SeriesId} from the dashboard.", seriesId);
            this.SetErrorToast("Could not update your library right now.");
        }

        return BackToList();
    }
}
