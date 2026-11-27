namespace Recall.Web.Services.Metrics;

/// <summary>
/// The kinds of action a signed-out visitor can press on a Details page, as
/// they appear in the metric keys, with the admin page's words for them.
/// </summary>
public static class ActionLabels
{
    /// <summary>The kinds, in the order the admin page lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [];

    public static string For(string kind) => kind;
}
