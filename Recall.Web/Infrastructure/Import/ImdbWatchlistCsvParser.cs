using System.Text;
using System.Text.RegularExpressions;

namespace Recall.Web.Infrastructure.Import;

/// <summary>
/// Parses an IMDb list export (either the "Ratings" or plain "Watchlist" CSV —
/// both share the same column layout, the latter just leaves "Your Rating" blank).
/// Hand-rolled rather than a library dependency: the only real complexity is
/// RFC4180 quoting (the Genres column embeds commas inside quotes), which a
/// simple character-level reader handles directly.
/// </summary>
public static partial class ImdbWatchlistCsvParser
{
    /// <summary>Hard cap on rows accepted from a single upload.</summary>
    public const int MaxRows = 5000;

    public sealed record ParsedRow(int RowNumber, string ImdbId, string Title, string TitleType, int? YourRating);

    public sealed record ParseResult(IReadOnlyList<ParsedRow> Rows, int SkippedRowCount)
    {
        public static readonly ParseResult Empty = new([], 0);
    }

    [GeneratedRegex("^tt\\d+$")]
    private static partial Regex ImdbIdPattern();

    /// <summary>
    /// Parses <paramref name="reader"/> into rows, silently dropping any row
    /// whose "Const" column isn't a well-formed IMDb id (counted in
    /// <see cref="ParseResult.SkippedRowCount"/> rather than thrown).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The file has no header row, or is missing the "Const"/"Title Type" columns
    /// this isn't recognizable as an IMDb export.
    /// </exception>
    public static ParseResult Parse(TextReader reader)
    {
        using var records = ReadCsvRecords(reader).GetEnumerator();

        if (!records.MoveNext())
            return ParseResult.Empty;

        var header = records.Current;
        var constIndex = IndexOf(header, "Const");
        var titleIndex = IndexOf(header, "Title");
        var titleTypeIndex = IndexOf(header, "Title Type");
        var yourRatingIndex = IndexOf(header, "Your Rating");

        if (constIndex < 0 || titleTypeIndex < 0)
        {
            throw new InvalidOperationException(
                "This doesn't look like an IMDb list export — missing \"Const\"/\"Title Type\" columns.");
        }

        var rows = new List<ParsedRow>();
        var skipped = 0;
        var rowNumber = 0;

        while (records.MoveNext() && rows.Count < MaxRows)
        {
            rowNumber++;
            var record = records.Current;

            var imdbId = Field(record, constIndex);
            if (!ImdbIdPattern().IsMatch(imdbId))
            {
                skipped++;
                continue;
            }

            var title = titleIndex >= 0 ? Field(record, titleIndex) : imdbId;
            var titleType = Field(record, titleTypeIndex);

            int? yourRating = null;
            if (yourRatingIndex >= 0)
            {
                var raw = Field(record, yourRatingIndex);
                if (int.TryParse(raw, out var parsed) && parsed is >= 1 and <= 10)
                    yourRating = parsed;
            }

            rows.Add(new ParsedRow(rowNumber, imdbId, title, titleType, yourRating));
        }

        return new ParseResult(rows, skipped);
    }

    private static string Field(List<string> record, int index)
        => index >= 0 && index < record.Count ? record[index].Trim() : string.Empty;

    private static int IndexOf(List<string> header, string name)
        => header.FindIndex(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Minimal RFC4180 reader: handles quoted fields, embedded commas/newlines
    /// inside quotes, and escaped ("") quotes. Operates over the raw character
    /// stream rather than splitting lines first, so a quoted newline can't
    /// desynchronize a row.
    /// </summary>
    private static IEnumerable<List<string>> ReadCsvRecords(TextReader reader)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        int current;

        while ((current = reader.Read()) != -1)
        {
            var c = (char)current;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record;
                    record = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
