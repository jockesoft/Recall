using System.Text.Json.Serialization;

namespace Recall.Web.Domain.TheTvDb;

/// <summary>A series or a movie, as Discover tells them apart (and stores them, by name).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscoverKind>))]
public enum DiscoverKind
{
    Series = 1,
    Movie = 2
}

/// <summary>
/// One title of a Discover list, as TheTVDB's filter endpoints describe it:
/// enough for a poster card ("Severance", "Series · 2022") and for the row
/// rules (the year, the first air date, the status, the score), nothing more.
/// Stored as JSON in <c>discover_list</c>, so a property is never renamed.
/// </summary>
public sealed record DiscoverTitle
{
    public required DiscoverKind Kind { get; init; }
    public required int TvdbId { get; init; }
    public required string Name { get; init; }

    /// <summary>The title's own year, when TheTVDB gives a usable one.</summary>
    public int? Year { get; init; }

    /// <summary>Absolute poster URL (normalized when mapped).</summary>
    public string? ImageUrl { get; init; }

    /// <summary>A series' first air date; movies have none in the filter results.</summary>
    public DateOnly? FirstAired { get; init; }

    /// <summary>TheTVDB's status name: "Continuing", "Ended", "Upcoming"; for a movie "Released", "Announced", ….</summary>
    public string? Status { get; init; }

    /// <summary>TheTVDB's popularity score: an all-time count that changes slowly, not a rating.</summary>
    public double Score { get; init; }
}
