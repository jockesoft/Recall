using Recall.Web.Domain.TheTvDb;

namespace Recall.Web.Services.Discover;

/// <summary>What a Discover toast's Undo takes back.</summary>
public enum DiscoverUndoAction
{
    /// <summary>The title was added (a series to the library, a movie to the watchlist): Undo takes it out again.</summary>
    Add,

    /// <summary>The title was hidden ("Not interested"): Undo shows it again.</summary>
    Hide
}

/// <summary>The Undo of a Discover action: what to take back, for which title, and the card's place to return to.</summary>
public sealed record DiscoverUndo(DiscoverUndoAction Action, DiscoverKind Kind, int TvdbId, string? Anchor);
