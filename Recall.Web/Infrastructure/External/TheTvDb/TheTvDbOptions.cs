namespace Recall.Web.Infrastructure.External.TheTvDb;

public sealed class TheTvDbOptions
{
    public const string SectionName = "TheTvDb";

    public string BaseUrl { get; set; } = "https://api4.thetvdb.com/v4/";
    public string ApiKey { get; set; } = string.Empty;
    public string? Pin { get; set; }
}