namespace Recall.Web.Infrastructure.External.TheTvDb;

public sealed class TheTvDbOptions
{
    public const string SectionName = "TheTvDb";

    public string BaseUrl { get; set; } = "https://api4.thetvdb.com/v4/";
    public string ApiKey { get; set; } = string.Empty;
    public string? Pin { get; set; }

    /// <summary>
    /// A series counts as one that rarely has episode stills when fewer than
    /// this percentage of its aired regular episodes have one. Its episodes are
    /// then not rechecked for a still (they keep the background-art fallback):
    /// asking daily for something TheTVDB almost never has is wasted quota.
    /// 0 turns the rule off.
    /// </summary>
    public int StillRecheckMinStillPercent { get; set; } = 10;

    /// <summary>
    /// The rule above only applies once a series has at least this many aired
    /// regular episodes; a new series with a handful of episodes and no stills
    /// yet is simply new.
    /// </summary>
    public int StillRecheckMinAiredEpisodes { get; set; } = 10;
}