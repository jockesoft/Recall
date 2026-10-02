using System.Net;
using System.Text;
using Recall.Web.Infrastructure.Display;

namespace Recall.Web.Services.Digest;

/// <summary>Where a digest's links point. Everything is absolute: an email has no page to be relative to.</summary>
/// <param name="BaseUrl">The site's public address without a trailing slash.</param>
/// <param name="UnsubscribeUrl">The signed, sign-in-free unsubscribe page for this recipient.</param>
public sealed record DigestLinks(string BaseUrl, string UnsubscribeUrl)
{
    public string Episode(int episodeId) => $"{BaseUrl}/Episodes/Details/{episodeId}";

    public string Series(int seriesId) => $"{BaseUrl}/Series/Details/{seriesId}";

    public string Library => $"{BaseUrl}/Library";

    public string Profile => $"{BaseUrl}/Account/Profile";
}

/// <summary>A rendered digest: the subject and the two parts of a multipart/alternative message.</summary>
public sealed record DigestEmail(string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Turns a <see cref="DigestContent"/> into an email. No images at all: most
/// mail clients block remote images, and loading posters would make every
/// reader's mail client call TheTVDB's servers. No tracking pixel and no
/// redirect links either; every link goes straight to the site. Both parts
/// carry the TheTVDB attribution its data requires, and the way to turn the
/// email off.
/// </summary>
public static class DigestEmailRenderer
{
    public const string TheTvDbAttribution = "Metadata provided by TheTVDB";
    private const string TheTvDbUrl = "https://thetvdb.com";

    public static DigestEmail Render(DigestContent content, string username, DateOnly today, DigestLinks links) =>
        new(Subject(content), Text(content, username, today, links), Html(content, username, today, links));

    /// <summary>"Your week on Recall: 1 new season, 5 to watch, 3 coming up".</summary>
    public static string Subject(DigestContent content)
    {
        var parts = new List<string>(3);

        if (content.NewSeasons.TotalCount > 0)
            parts.Add(content.NewSeasons.TotalCount == 1 ? "1 new season" : $"{content.NewSeasons.TotalCount} new seasons");
        if (content.ReadyToWatch.TotalCount > 0)
            parts.Add($"{content.ReadyEpisodeCount} to watch");
        if (content.ComingUp.TotalCount > 0)
            parts.Add($"{content.ComingEpisodeCount} coming up");

        return parts.Count == 0 ? "Your week on Recall" : "Your week on Recall: " + string.Join(", ", parts);
    }

    private static string EpisodeCountText(DigestEpisodeLine line) =>
        line.EpisodeCount == 1 ? string.Empty : $" ({line.EpisodeCount} episodes)";

    // ---- plain text ---------------------------------------------------------------

    private static string Text(DigestContent content, string username, DateOnly today, DigestLinks links)
    {
        var text = new StringBuilder();
        text.Append("Hi ").Append(username).AppendLine(",").AppendLine();
        text.AppendLine("Here is your week on Recall.").AppendLine();

        if (content.NewSeasons.TotalCount > 0)
        {
            text.AppendLine("NEW SEASONS");
            foreach (var premiere in content.NewSeasons.Items)
            {
                text.AppendLine($"- A new season of {premiere.SeriesName} is out: season {premiere.SeasonNumber} started {DisplayDate.Format(premiere.Aired, today)}.");
                text.AppendLine($"  {links.Episode(premiere.EpisodeId)}");
            }

            More(text, content.NewSeasons.MoreCount, links);
            text.AppendLine();
        }

        if (content.ReadyToWatch.TotalCount > 0)
        {
            text.AppendLine("READY TO WATCH");
            foreach (var line in content.ReadyToWatch.Items)
            {
                text.AppendLine($"- {line.SeriesName}: {line.Code}{Name(line)}{EpisodeCountText(line)}");
                text.AppendLine($"  {links.Episode(line.LinkEpisodeId)}");
            }

            More(text, content.ReadyToWatch.MoreCount, links);
            text.AppendLine();
        }

        if (content.ComingUp.TotalCount > 0)
        {
            text.AppendLine("COMING UP");
            foreach (var line in content.ComingUp.Items)
            {
                text.AppendLine($"- {DisplayDate.Format(line.FirstAired, today)}: {line.SeriesName}, {line.Code}{Name(line)}{EpisodeCountText(line)}");
                text.AppendLine($"  {links.Series(line.SeriesId)}");
            }

            More(text, content.ComingUp.MoreCount, links);
            text.AppendLine();
        }

        text.AppendLine("--");
        text.AppendLine("You get this email because you turned on the weekly email in Recall.");
        text.AppendLine($"Turn it off (no sign-in needed): {links.UnsubscribeUrl}");
        text.AppendLine($"Your settings: {links.Profile}");
        text.AppendLine($"{TheTvDbAttribution} ({TheTvDbUrl}).");

        return text.ToString();

        static string Name(DigestEpisodeLine line) => line.EpisodeName is null ? string.Empty : $" “{line.EpisodeName}”";

        static void More(StringBuilder builder, int moreCount, DigestLinks links)
        {
            if (moreCount > 0)
                builder.AppendLine($"  ...and {moreCount} more in your library: {links.Library}");
        }
    }

    // ---- HTML ---------------------------------------------------------------------
    // Table layout and inline styles, which is what mail clients render reliably.
    // The colours are the theme's (ink canvas, paper card). Clients that honour
    // prefers-color-scheme get the dark block below; clients that invert colours
    // on their own get mid-tones that survive it (no pure black or white).

    private const string Sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
    private const string Mono = "'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace";

    private static string Html(DigestContent content, string username, DateOnly today, DigestLinks links)
    {
        var body = new StringBuilder();

        if (content.NewSeasons.TotalCount > 0)
        {
            body.Append(Heading("New seasons"));
            foreach (var premiere in content.NewSeasons.Items)
            {
                body.Append(Row(
                    $"A new season of {Link(links.Series(premiere.SeriesId), premiere.SeriesName)} is out",
                    $"Season {premiere.SeasonNumber} started {E(DisplayDate.Format(premiere.Aired, today))}. " +
                    Link(links.Episode(premiere.EpisodeId), "Open the first episode")));
            }

            body.Append(MoreRow(content.NewSeasons.MoreCount, links));
        }

        if (content.ReadyToWatch.TotalCount > 0)
        {
            body.Append(Heading("Ready to watch"));
            foreach (var line in content.ReadyToWatch.Items)
            {
                body.Append(Row(
                    Link(links.Series(line.SeriesId), line.SeriesName),
                    $"{Code(line)}{NameHtml(line)}{E(EpisodeCountText(line))} &middot; {Link(links.Episode(line.LinkEpisodeId), "Open")}"));
            }

            body.Append(MoreRow(content.ReadyToWatch.MoreCount, links));
        }

        if (content.ComingUp.TotalCount > 0)
        {
            body.Append(Heading("Coming up"));
            foreach (var line in content.ComingUp.Items)
            {
                body.Append(Row(
                    Link(links.Series(line.SeriesId), line.SeriesName),
                    $"{E(DisplayDate.Format(line.FirstAired, today))} &middot; {Code(line)}{NameHtml(line)}{E(EpisodeCountText(line))}"));
            }

            body.Append(MoreRow(content.ComingUp.MoreCount, links));
        }

        return $$"""
                 <!DOCTYPE html>
                 <html lang="en" xmlns="http://www.w3.org/1999/xhtml">
                 <head>
                 <meta charset="utf-8">
                 <meta name="viewport" content="width=device-width,initial-scale=1">
                 <meta name="color-scheme" content="light dark">
                 <meta name="supported-color-schemes" content="light dark">
                 <title>{{E(Subject(content))}}</title>
                 <style>
                 @media (prefers-color-scheme: dark) {
                   .rc-card { background: #1f2733 !important; }
                   .rc-text { color: #f1ece2 !important; }
                   .rc-muted { color: #c3bdb1 !important; }
                   .rc-rule { border-color: #3a4554 !important; }
                   .rc-link { color: #e8a33d !important; }
                 }
                 </style>
                 </head>
                 <body style="margin:0;padding:0;background:#151b24;">
                 <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;">{{E(Subject(content))}}</div>
                 <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#151b24;">
                 <tr><td align="center" style="padding:24px 12px;">
                 <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;">
                 <tr><td style="padding:0 4px 12px 4px;font-family:{{Sans}};font-size:20px;font-weight:800;letter-spacing:0.04em;color:#f1ece2;">RECALL</td></tr>
                 <tr><td class="rc-card" style="background:#f1ece2;border-radius:12px;padding:24px;">
                 <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                 <tr><td class="rc-text" style="font-family:{{Sans}};font-size:16px;line-height:1.5;color:#2c2a24;padding-bottom:4px;">
                 Hi {{E(username)}}, here is your week on Recall.
                 </td></tr>
                 {{body}}
                 </table>
                 </td></tr>
                 <tr><td class="rc-muted" style="padding:16px 4px 0 4px;font-family:{{Sans}};font-size:12px;line-height:1.5;color:#b9b3a7;">
                 You get this email because you turned on the weekly email in Recall.
                 <a href="{{E(links.UnsubscribeUrl)}}" style="color:#e8a33d;">Turn it off</a> (no sign-in needed) or change it in
                 <a href="{{E(links.Profile)}}" style="color:#e8a33d;">your profile</a>.<br>
                 <a href="{{TheTvDbUrl}}" style="color:#b9b3a7;">{{TheTvDbAttribution}}</a>
                 </td></tr>
                 </table>
                 </td></tr>
                 </table>
                 </body>
                 </html>
                 """;
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private static string Link(string url, string text) =>
        $"<a class=\"rc-link\" href=\"{E(url)}\" style=\"color:#8f5410;\">{E(text)}</a>";

    private static string Code(DigestEpisodeLine line) =>
        $"<span style=\"font-family:{Mono};\">{E(line.Code)}</span>";

    private static string NameHtml(DigestEpisodeLine line) =>
        line.EpisodeName is null ? string.Empty : $" {E(line.EpisodeName)}";

    private static string Heading(string text) =>
        $"<tr><td class=\"rc-text rc-rule\" style=\"padding:20px 0 6px 0;border-bottom:1px solid #d9d2c3;font-family:{Sans};font-size:13px;font-weight:700;letter-spacing:0.08em;text-transform:uppercase;color:#2c2a24;\">{E(text)}</td></tr>";

    private static string Row(string titleHtml, string detailHtml) =>
        $"<tr><td class=\"rc-rule\" style=\"padding:10px 0;border-bottom:1px solid #e6dfd0;\">" +
        $"<div class=\"rc-text\" style=\"font-family:{Sans};font-size:15px;font-weight:600;line-height:1.35;color:#2c2a24;\">{titleHtml}</div>" +
        $"<div class=\"rc-muted\" style=\"font-family:{Sans};font-size:14px;line-height:1.45;color:#655b49;\">{detailHtml}</div>" +
        "</td></tr>";

    private static string MoreRow(int moreCount, DigestLinks links) =>
        moreCount <= 0
            ? string.Empty
            : $"<tr><td class=\"rc-muted\" style=\"padding:10px 0 0 0;font-family:{Sans};font-size:14px;color:#655b49;\">and {moreCount} more in {Link(links.Library, "your library")}</td></tr>";
}
