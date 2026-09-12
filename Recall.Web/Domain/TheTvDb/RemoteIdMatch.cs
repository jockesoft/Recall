namespace Recall.Web.Domain.TheTvDb;

/// <summary>A TheTVDB entity resolved from an external id (e.g. an IMDb id).</summary>
public sealed record RemoteIdMatch(int TvdbId, string? Name, bool IsMovie);
