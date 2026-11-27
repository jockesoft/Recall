namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_CatchUpModal.cshtml</c>: the "Catch up?" dialog that asks,
/// before an episode is marked, whether to mark the earlier unwatched ones
/// too. js/tvdb-series.js opens it for a form with <c>data-prior-check="true"</c>
/// and copies that form's <see cref="Fields"/> into the dialog's two forms.
/// Series Details (its episode rows) and Episode Details (the action row and
/// the Up next card).
/// </summary>
public sealed class CatchUpModalModel
{
    public required int RouteId { get; init; }

    /// <summary>The handler that marks just the one episode.</summary>
    public required string JustThisHandler { get; init; }

    /// <summary>The handler that marks it and every earlier one.</summary>
    public required string ThroughHandler { get; init; }

    /// <summary>The hidden fields both forms carry, filled from the form that opened the dialog.</summary>
    public IReadOnlyList<string> Fields { get; init; } = ["episodeId"];
}
