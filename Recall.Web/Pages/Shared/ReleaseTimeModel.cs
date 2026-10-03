using Recall.Web.Services.WatchTracking;

namespace Recall.Web.Pages.Shared;

/// <summary>
/// Input for <c>_ReleaseTime.cshtml</c>: a release moment written as a
/// <c>&lt;time&gt;</c> element the browser can put in the viewer's zone.
/// </summary>
/// <param name="TimeOnly">Show the time alone (the card sits under a "Today" or "Tomorrow" heading).</param>
public sealed record ReleaseTimeModel(ReleaseMoment Release, DateOnly Today, bool TimeOnly = false);
