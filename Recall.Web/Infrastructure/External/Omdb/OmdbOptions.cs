namespace Recall.Web.Infrastructure.External.Omdb;

/// <summary>
/// Bound from the <c>Omdb</c> configuration section. The API key belongs in
/// user-secrets (dev) or environment variables (prod), never in appsettings.
/// Dev:  dotnet user-secrets set "Omdb:ApiKey" "&lt;key&gt;"
/// Prod: Omdb__ApiKey=&lt;key&gt;  (in .env.prod)
/// </summary>
public sealed class OmdbOptions
{
    public const string SectionName = "Omdb";

    public string BaseUrl { get; set; } = "https://www.omdbapi.com/";

    public string ApiKey { get; set; } = string.Empty;
}
