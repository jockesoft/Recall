using Recall.Web.Domain.TheTvDb;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Entities;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.Metrics;
using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Services.PendingActions;

public sealed class PendingActionService(
    ITheTvDbService theTvDbService,
    ISeriesLibraryService seriesLibraryService,
    IMovieTrackingService movieTrackingService,
    IWatchProgressService watchProgressService,
    ILikeRepository likeRepository,
    IRatingRepository ratingRepository,
    ISiteMetrics metrics,
    ILogger<PendingActionService> logger) : IPendingActionService
{
    /// <summary>Ends every prompt's second sentence.</summary>
    public const string FreeNoPassword = "Free, no password.";

    public async Task<PendingActionPrompt> DescribeAsync(PendingAction action, CancellationToken cancellationToken = default)
    {
        string? name = null, poster = null, seriesName = null;

        try
        {
            switch (action.Title)
            {
                case PendingTitle.Series:
                    var series = await theTvDbService.GetCachedSeriesAggregateAsync(action.TvdbId, cancellationToken);
                    (name, poster) = (series?.Name, series?.ImageUrl);
                    break;
                case PendingTitle.Movie:
                    var movie = await theTvDbService.GetCachedMovieAggregateAsync(action.TvdbId, cancellationToken);
                    (name, poster) = (movie?.Name, movie?.ImageUrl);
                    break;
                default:
                    var episode = await theTvDbService.GetCachedEpisodeDetailsAsync(action.TvdbId, cancellationToken);
                    name = episode is null ? null : Code(episode);
                    if (episode?.SeriesId is > 0 and var seriesId)
                    {
                        var parent = await theTvDbService.GetCachedSeriesAggregateAsync(seriesId, cancellationToken);
                        (seriesName, poster) = (parent?.Name, parent?.ImageUrl);
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            // The sentence just gets more general; the sign-in page still works.
            logger.LogWarning(ex, "Could not read the cached title of a pending {Kind} {TvdbId}.", action.Kind, action.TvdbId);
        }

        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var series1 = name ?? "this series";
        var movie1 = name ?? "this movie";
        var episode1 = name ?? "this episode";

        var after = "Once you're signed in, we'll " + action.Kind switch
        {
            PendingActionKind.AddSeries => $"add {series1} to your library.",
            PendingActionKind.AddMovieToWatchlist => $"add {movie1} to your watchlist.",
            PendingActionKind.MarkMovieWatched => $"mark {movie1} as watched.",
            PendingActionKind.MarkEpisodeWatched => $"mark {episode1} as watched.",
            PendingActionKind.LikeSeries => $"add {series1} to your favorites.",
            PendingActionKind.LikeMovie => $"add {movie1} to your favorites.",
            PendingActionKind.LikeEpisode => $"add {episode1} to your favorites.",
            PendingActionKind.RateSeries => $"rate {series1} {action.Rating} out of 10.",
            PendingActionKind.RateMovie => $"rate {movie1} {action.Rating} out of 10.",
            _ => $"rate {episode1} {action.Rating} out of 10."
        };

        var (lead, detail) = action.Kind switch
        {
            PendingActionKind.AddSeries => ($"Sign in to add {series1} to your library.", "You'll get a notification when new episodes air."),
            PendingActionKind.LikeSeries => ($"Sign in to like {series1}.", "Everything you like is kept together under Favorites."),
            PendingActionKind.RateSeries => ($"Sign in to rate {series1} {action.Rating} out of 10.", "Your rating counts towards the Recall rating."),
            PendingActionKind.MarkMovieWatched => ($"Sign in to mark {movie1} as watched.", "Recall keeps track of everything you've watched."),
            PendingActionKind.AddMovieToWatchlist => ($"Sign in to add {movie1} to your watchlist.", "Keep the movies you want to see in one place."),
            PendingActionKind.LikeMovie => ($"Sign in to like {movie1}.", "Everything you like is kept together under Favorites."),
            PendingActionKind.RateMovie => ($"Sign in to rate {movie1} {action.Rating} out of 10.", "Your rating counts towards the Recall rating."),
            PendingActionKind.MarkEpisodeWatched => ($"Sign in to mark {episode1} as watched.",
                seriesName is null ? "Recall keeps your place in every series you watch." : $"Recall keeps your place in {seriesName}."),
            PendingActionKind.LikeEpisode => ($"Sign in to like {episode1}.", "Everything you like is kept together under Favorites."),
            _ => ($"Sign in to rate {episode1} {action.Rating} out of 10.", "Your rating counts towards the Recall rating.")
        };

        return new PendingActionPrompt(lead, $"{detail} {FreeNoPassword}", string.IsNullOrWhiteSpace(poster) ? null : poster, after);
    }

    public async Task<PendingActionOutcome> CompleteAsync(Guid userId, PendingAction action, CancellationToken cancellationToken = default)
    {
        if (!action.IsValid)
            return new PendingActionOutcome(PendingOutcomeTone.Error, "That could not be done.");

        PendingActionOutcome outcome;
        try
        {
            outcome = action.Title switch
            {
                PendingTitle.Series => await CompleteSeriesAsync(userId, action, cancellationToken),
                PendingTitle.Movie => await CompleteMovieAsync(userId, action, cancellationToken),
                _ => await CompleteEpisodeAsync(userId, action, cancellationToken)
            };
        }
        catch (Exception ex) when (ex.IsNotCancellationBy(cancellationToken))
        {
            logger.LogError(ex, "Could not complete pending {Kind} {TvdbId} after sign-in.", action.Kind, action.TvdbId);
            return new PendingActionOutcome(PendingOutcomeTone.Error, "You're signed in, but that could not be done right now. Try again from this page.");
        }

        if (outcome.Tone != PendingOutcomeTone.Error)
            metrics.Increment(SiteMetricKeys.ActionCompleted(action.Kind));

        return outcome;
    }

    private async Task<PendingActionOutcome> CompleteSeriesAsync(Guid userId, PendingAction action, CancellationToken cancellationToken)
    {
        var id = action.TvdbId;

        if (action.Kind == PendingActionKind.AddSeries)
        {
            var added = await seriesLibraryService.AddAsync(userId, id, cancellationToken);
            var addedName = added.SeriesName ?? "The series";
            return added.Outcome switch
            {
                AddSeriesOutcome.Added => new PendingActionOutcome(PendingOutcomeTone.Success, $"Added {addedName} to your library.")
                {
                    UndoAdd = new PendingUndo(PendingTitle.Series, id)
                },
                AddSeriesOutcome.AlreadyInLibrary => new PendingActionOutcome(PendingOutcomeTone.Info,
                    $"{(await SeriesNameAsync(id, cancellationToken)) ?? "The series"} is already in your library."),
                _ => new PendingActionOutcome(PendingOutcomeTone.Error, "TheTVDB no longer has that series, so it could not be added.")
            };
        }

        // Like and rate check that the series exists, which the page's own handlers leave to the page.
        if (await SeriesNameAsync(id, cancellationToken) is not { } name)
            return new PendingActionOutcome(PendingOutcomeTone.Error, "Could not find that series on TheTVDB.");

        return action.Kind == PendingActionKind.LikeSeries
            ? await LikeAsync(userId, LikeTargetType.Series, id, id, name, cancellationToken)
            : await RateAsync(userId, RatingTargetType.Series, id, id, name, action.Rating!.Value, cancellationToken);
    }

    private async Task<PendingActionOutcome> CompleteMovieAsync(Guid userId, PendingAction action, CancellationToken cancellationToken)
    {
        var id = action.TvdbId;
        var movie = await theTvDbService.TryGetMovieAggregateAsync(id, logger, "Pending action", cancellationToken);
        if (movie is null)
            return new PendingActionOutcome(PendingOutcomeTone.Error, "Could not find that movie on TheTVDB.");

        var name = string.IsNullOrWhiteSpace(movie.Name) ? "the movie" : movie.Name;

        switch (action.Kind)
        {
            case PendingActionKind.MarkMovieWatched:
                var already = await movieTrackingService.MarkWatchedAsync(userId, id, WatchSource.Single, cancellationToken);
                return already
                    ? new PendingActionOutcome(PendingOutcomeTone.Info, $"You had already watched {name}.")
                    : new PendingActionOutcome(PendingOutcomeTone.Success, $"Marked {name} as watched.");

            case PendingActionKind.AddMovieToWatchlist:
                return await movieTrackingService.AddToWatchlistAsync(userId, id, name, cancellationToken) switch
                {
                    MovieWatchlistOutcome.Added => new PendingActionOutcome(PendingOutcomeTone.Success, $"Added {name} to your watchlist.")
                    {
                        UndoAdd = new PendingUndo(PendingTitle.Movie, id)
                    },
                    MovieWatchlistOutcome.AlreadyOnWatchlist => new PendingActionOutcome(PendingOutcomeTone.Info, $"{name} is already on your watchlist."),
                    MovieWatchlistOutcome.AlreadyWatched => new PendingActionOutcome(PendingOutcomeTone.Info, $"You've already watched {name}."),
                    _ => new PendingActionOutcome(PendingOutcomeTone.Error, "Could not find that movie on TheTVDB.")
                };

            case PendingActionKind.LikeMovie:
                return await LikeAsync(userId, LikeTargetType.Movie, id, id, name, cancellationToken);

            default:
                return await RateAsync(userId, RatingTargetType.Movie, id, id, name, action.Rating!.Value, cancellationToken);
        }
    }

    private async Task<PendingActionOutcome> CompleteEpisodeAsync(Guid userId, PendingAction action, CancellationToken cancellationToken)
    {
        var id = action.TvdbId;

        // As the Episode Details handlers do: the episode's own record names its series.
        var episode = await theTvDbService.GetEpisodeDetailsAsync(id, cancellationToken);
        if (episode is not { SeriesId: > 0 and var seriesId })
            return new PendingActionOutcome(PendingOutcomeTone.Error, "Could not verify this episode against its series right now.");

        var code = Code(episode);

        switch (action.Kind)
        {
            case PendingActionKind.MarkEpisodeWatched:
                var marked = await watchProgressService.MarkEpisodeWatchedUndoablyAsync(userId, seriesId, id, cancellationToken);
                switch (marked.Outcome)
                {
                    case EpisodeWatchOutcome.MarkedWatched when marked.Batch is { InsertedCount: > 0 }:
                        return new PendingActionOutcome(PendingOutcomeTone.Success,
                            $"Marked {code} as watched{MarkLibraryEffect.Clause(marked.AddedToLibrary, marked.ResumedWatching)}.")
                        {
                            WatchedSeriesId = seriesId,
                            Batch = marked.Batch,
                            CaughtUp = marked.CaughtUp,
                            ResumedFromStoppedUtc = marked.ResumedFromStoppedUtc
                        };
                    case EpisodeWatchOutcome.MarkedWatched:
                        return new PendingActionOutcome(PendingOutcomeTone.Info, $"You had already watched {code}.");
                    case EpisodeWatchOutcome.NotAired:
                        return new PendingActionOutcome(PendingOutcomeTone.Error, "You can't mark an episode as watched before it has aired.");
                    default:
                        return new PendingActionOutcome(PendingOutcomeTone.Error, "Could not verify this episode against its series right now.");
                }

            case PendingActionKind.LikeEpisode:
                return await LikeAsync(userId, LikeTargetType.Episode, id, seriesId, code, cancellationToken);

            default:
                return await RateAsync(userId, RatingTargetType.Episode, id, seriesId, code, action.Rating!.Value, cancellationToken);
        }
    }

    private async Task<PendingActionOutcome> LikeAsync(
        Guid userId, LikeTargetType type, int targetId, int seriesId, string name, CancellationToken cancellationToken)
    {
        // A like, never a toggle: a heart pressed before signing in must not take away one given before.
        if (await likeRepository.IsLikedAsync(userId, type, targetId, cancellationToken))
            return new PendingActionOutcome(PendingOutcomeTone.Info, $"You already like {name}.");

        await likeRepository.ToggleAsync(userId, type, targetId, seriesId, cancellationToken);
        return new PendingActionOutcome(PendingOutcomeTone.Success, $"Liked {name}.");
    }

    private async Task<PendingActionOutcome> RateAsync(
        Guid userId, RatingTargetType type, int targetId, int seriesId, string name, int value, CancellationToken cancellationToken)
    {
        await ratingRepository.RateAsync(userId, type, targetId, seriesId, value, cancellationToken);
        return new PendingActionOutcome(PendingOutcomeTone.Success, $"Rated {name} {value} out of 10.");
    }

    private async Task<string?> SeriesNameAsync(int seriesId, CancellationToken cancellationToken) =>
        (await theTvDbService.TryGetSeriesAggregateAsync(seriesId, logger, "Pending action", cancellationToken))?.Name is { Length: > 0 } name
            ? name
            : null;

    /// <summary>"S03E01"; "Special 3" for a special; "the episode" without numbers.</summary>
    public static string Code(Episode episode) => episode switch
    {
        { SeasonNumber: 0, Number: { } special } => $"Special {special}",
        { SeasonNumber: { } season, Number: { } number } => $"S{season:D2}E{number:D2}",
        _ => "the episode"
    };
}
