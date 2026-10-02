namespace Recall.Web.Infrastructure.Display;

/// <summary>
/// Up to two initials from a name, for an avatar that stands in for a photo:
/// the user's own (navbar, profile) and a cast or crew member without one.
/// </summary>
public static class Initials
{
    private static readonly char[] Separators = [' ', '-', '_', '.'];

    /// <summary>
    /// "Bryan Cranston" → "BC", "dev-user" → "DU", "madonna" → "M". The first
    /// and last word that start with a letter; "?" when there is none.
    /// </summary>
    public static string Of(string? name)
    {
        var words = (name ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => char.IsLetter(w[0]))
            .ToArray();

        return words.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(words[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[^1][0])}"
        };
    }
}
