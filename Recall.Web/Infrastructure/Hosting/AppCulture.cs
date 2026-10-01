using System.Globalization;

namespace Recall.Web.Infrastructure.Hosting;

/// <summary>
/// Recall is English-only. Without this, .NET takes its culture from the host
/// (<c>LANG</c> in the container, the OS setting on a developer machine), so
/// every culture-sensitive <c>ToString</c> — "dddd, MMMM d" on the dashboard,
/// "N0" on the admin page — silently rendered in whatever language that
/// happened to be.
/// </summary>
public static class AppCulture
{
    public const string Name = "en-US";

    /// <summary>
    /// Makes <see cref="Name"/> the culture of every thread that hasn't been
    /// given one explicitly: request threads, Quartz job threads, and the
    /// startup thread alike. Call once, first thing in <c>Program.cs</c>.
    /// </summary>
    public static void PinToEnglish()
    {
        var culture = CultureInfo.GetCultureInfo(Name);

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
