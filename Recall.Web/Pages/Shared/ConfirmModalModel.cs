namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_ConfirmModal.cshtml</c>: a Bootstrap modal that asks before a
/// POST that removes something. Open it with
/// <c>data-bs-toggle="modal" data-bs-target="#{Id}"</c>. Use this instead of a
/// browser <c>confirm()</c>.
/// </summary>
public sealed class ConfirmModalModel
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Text { get; init; }

    public required string ConfirmLabel { get; init; }

    public required string Handler { get; init; }

    public required int RouteId { get; init; }

    public IDictionary<string, string> HiddenFields { get; init; } = new Dictionary<string, string>();
}
