using Recall.Web.Infrastructure.Persistence.Entities;

namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// How an import row's status is shown: the words, the badge colour and the
/// icon. The one mapping for it — pages never print the enum name.
/// </summary>
/// <param name="Label">What people read: "Already had it", not "AlreadyInLibrary".</param>
/// <param name="BadgeClass">The <c>tvdb-badge--*</c> modifier carrying the status colour.</param>
/// <param name="Icon">Icon class, from <see cref="Icons"/>.</param>
public sealed record ImportStatusDisplay(string Label, string BadgeClass, string Icon)
{
    private static readonly ImportStatusDisplay Waiting = new("Waiting", "tvdb-badge--neutral", Icons.NotAiredYet);
    private static readonly ImportStatusDisplay Imported = new("Imported", "tvdb-badge--ok", Icons.Success);
    private static readonly ImportStatusDisplay AlreadyHadIt = new("Already had it", "tvdb-badge--neutral", Icons.Check);
    private static readonly ImportStatusDisplay NoMatch = new("No match", "tvdb-badge--amber", Icons.Search);
    private static readonly ImportStatusDisplay Unsupported = new("Unsupported", "tvdb-badge--neutral", Icons.Unsupported);
    private static readonly ImportStatusDisplay Failed = new("Failed", "tvdb-badge--danger", Icons.Error);

    public static ImportStatusDisplay For(WatchlistImportItemStatus status) => status switch
    {
        WatchlistImportItemStatus.Pending => Waiting,
        WatchlistImportItemStatus.Imported => Imported,
        WatchlistImportItemStatus.AlreadyInLibrary => AlreadyHadIt,
        WatchlistImportItemStatus.NotFound => NoMatch,
        WatchlistImportItemStatus.Unsupported => Unsupported,
        WatchlistImportItemStatus.Failed => Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "An import status without a display mapping.")
    };

    /// <summary>The statuses an import ends in, in the order the summary lists them.</summary>
    public static IReadOnlyList<WatchlistImportItemStatus> Outcomes { get; } =
    [
        WatchlistImportItemStatus.Imported,
        WatchlistImportItemStatus.AlreadyInLibrary,
        WatchlistImportItemStatus.NotFound,
        WatchlistImportItemStatus.Unsupported,
        WatchlistImportItemStatus.Failed
    ];
}
