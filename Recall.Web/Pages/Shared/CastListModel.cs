using Recall.Web.Domain.TheTvDb;
using Display = Recall.Web.Infrastructure.Display;

namespace Recall.Web.Pages.Shared;

/// <summary>Input for <c>_CastList.cshtml</c>: one headed list of people.</summary>
public sealed class CastListModel
{
    /// <summary>"Cast" or "Crew".</summary>
    public required string Heading { get; init; }

    /// <summary>Unique on the page: the id of the list, used by its "Show all" button.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<CastPerson> People { get; init; }

    /// <summary>
    /// False (the cast): portrait cards. True (the crew): a compact list of
    /// name and jobs beside a small round photo, since most crew have no photo
    /// and a wall of initials tiles says nothing.
    /// </summary>
    public bool Compact { get; init; }
}

/// <summary>
/// One person in a cast or crew list. <paramref name="Detail"/> is the character
/// for an actor ("Walter White") and the job for crew ("Director, Writer").
/// </summary>
public sealed record CastPerson(string Name, string? Detail, string? ImageUrl, string? Url)
{
    /// <summary>Up to two initials, for the avatar of a person without a photo.</summary>
    public string Initials => Display.Initials.Of(Name);
}

/// <summary>
/// Turns TheTVDB's flat character list into a cast and a crew. TheTVDB has one
/// row per role, so a person who directed and wrote appears twice; here each
/// person appears once per list, with their characters or jobs joined.
/// </summary>
public static class CastBuilder
{
    // TheTVDB "peopleType" values that mean the person is on screen.
    private static readonly HashSet<string> OnScreenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Actor", "Guest Star", "Host", "Musical Guest"
    };

    // Crew is listed by job, in this order; anything else follows in the order TheTVDB gave it.
    private static readonly string[] JobOrder = ["Creator", "Showrunner", "Director", "Writer", "Executive Producer", "Producer"];

    public static (IReadOnlyList<CastPerson> Cast, IReadOnlyList<CastPerson> Crew) Build(IEnumerable<Character> characters)
    {
        var rows = characters
            .Where(c => !string.IsNullOrWhiteSpace(c.PersonName) || !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Sort ?? int.MaxValue)
            .ToList();

        var cast = Merge(rows.Where(IsOnScreen), c => c.Name, " / ");

        var crewRows = rows.Where(c => !IsOnScreen(c)).ToList();
        var crew = Merge(
            crewRows.OrderBy(c => JobRank(c.PeopleType)),
            c => c.PeopleType,
            ", ");

        return (cast, crew);
    }

    /// <summary>On screen: an acting type, or no type at all but a character name.</summary>
    private static bool IsOnScreen(Character character) =>
        string.IsNullOrWhiteSpace(character.PeopleType)
            ? !string.IsNullOrWhiteSpace(character.Name)
            : OnScreenTypes.Contains(character.PeopleType.Trim());

    private static int JobRank(string? job)
    {
        var index = Array.FindIndex(JobOrder, j => string.Equals(j, job?.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? JobOrder.Length : index;
    }

    /// <summary>One entry per person, keeping first-seen order, with the distinct details joined.</summary>
    private static IReadOnlyList<CastPerson> Merge(
        IEnumerable<Character> rows,
        Func<Character, string?> detail,
        string separator)
    {
        var people = new List<(string Key, Character First, List<string> Details)>();

        foreach (var row in rows)
        {
            // The same person: TheTVDB's person id when there is one, the name otherwise.
            var key = row.PeopleId is { } id ? $"id:{id}" : $"name:{row.PersonName?.Trim().ToLowerInvariant()}";
            var entry = people.FirstOrDefault(p => p.Key == key);
            if (entry.Details is null)
            {
                entry = (key, row, []);
                people.Add(entry);
            }

            var text = detail(row)?.Trim();
            if (!string.IsNullOrEmpty(text) && !entry.Details.Contains(text, StringComparer.OrdinalIgnoreCase))
                entry.Details.Add(text);
        }

        return people
            .Select(p => new CastPerson(
                string.IsNullOrWhiteSpace(p.First.PersonName) ? "Unknown" : p.First.PersonName.Trim(),
                p.Details.Count == 0 ? null : string.Join(separator, p.Details),
                !string.IsNullOrWhiteSpace(p.First.PersonImageUrl) ? p.First.PersonImageUrl : NullIfBlank(p.First.Image),
                NullIfBlank(p.First.Url)))
            .ToList();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
