using AwesomeAssertions;
using Recall.Web.Infrastructure.Import;

namespace Recall.Tests.Infrastructure.Import;

[TestFixture]
public sealed class ImdbWatchlistCsvParserTests
{
    private const string Header =
        "Position,Const,Created,Modified,Description,Title,Original Title,URL,Title Type,IMDb Rating,Runtime (mins),Year,Genres,Num Votes,Release Date,Directors,Your Rating,Date Rated";

    [Test]
    public void Parse_Should_ExtractFields_FromRealSampleRows()
    {
        var csv = string.Join('\n',
            Header,
            "1,tt2479478,2016-01-16,2016-01-16,,\"The Ridiculous 6\",\"The Ridiculous 6\",https://www.imdb.com/title/tt2479478/,Movie,4.9,119,2015,\"Action, Adventure, Comedy, Western\",59420,2015-12-11,\"Frank Coraci\",3,2016-01-16",
            "2,tt1448755,2016-01-16,2016-01-16,,\"Killer Elite\",\"Killer Elite\",https://www.imdb.com/title/tt1448755/,Movie,6.4,116,2011,\"Action, Crime, Thriller\",142069,2011-09-23,\"Gary McKendry\",5,2016-01-16");

        var result = ImdbWatchlistCsvParser.Parse(new StringReader(csv));

        result.SkippedRowCount.Should().Be(0);
        result.Rows.Should().HaveCount(2);

        result.Rows[0].Should().BeEquivalentTo(new ImdbWatchlistCsvParser.ParsedRow(1, "tt2479478", "The Ridiculous 6", "Movie", 3));
        result.Rows[1].Should().BeEquivalentTo(new ImdbWatchlistCsvParser.ParsedRow(2, "tt1448755", "Killer Elite", "Movie", 5));
    }

    [Test]
    public void Parse_Should_TreatBlankRating_AsNull()
    {
        var csv = string.Join('\n',
            Header,
            "1,tt2479478,2016-01-16,2016-01-16,,\"The Ridiculous 6\",\"The Ridiculous 6\",https://www.imdb.com/title/tt2479478/,Movie,4.9,119,2015,\"Comedy\",59420,2015-12-11,\"Frank Coraci\",,");

        var result = ImdbWatchlistCsvParser.Parse(new StringReader(csv));

        result.Rows.Should().ContainSingle().Which.YourRating.Should().BeNull();
    }

    [Test]
    public void Parse_Should_SkipRows_WithMalformedImdbId()
    {
        var csv = string.Join('\n',
            Header,
            "1,not-an-imdb-id,2016-01-16,2016-01-16,,\"Bad Row\",\"Bad Row\",https://example.com,Movie,4.9,119,2015,\"Comedy\",1,2015-12-11,\"Someone\",,",
            "2,tt1448755,2016-01-16,2016-01-16,,\"Killer Elite\",\"Killer Elite\",https://www.imdb.com/title/tt1448755/,Movie,6.4,116,2011,\"Action\",142069,2011-09-23,\"Gary McKendry\",5,2016-01-16");

        var result = ImdbWatchlistCsvParser.Parse(new StringReader(csv));

        result.SkippedRowCount.Should().Be(1);
        result.Rows.Should().ContainSingle().Which.ImdbId.Should().Be("tt1448755");
    }

    [Test]
    public void Parse_Should_KeepUnknownTitleType_ForTheCallerToFilter()
    {
        var csv = string.Join('\n',
            Header,
            "1,tt0000001,2016-01-16,2016-01-16,,\"Some Episode\",\"Some Episode\",https://example.com,TV Episode,7.0,30,2015,\"Drama\",1,2015-12-11,\"Someone\",,");

        var result = ImdbWatchlistCsvParser.Parse(new StringReader(csv));

        result.Rows.Should().ContainSingle().Which.TitleType.Should().Be("TV Episode");
    }

    [Test]
    public void Parse_Should_ReturnEmpty_ForEmptyFile()
    {
        var result = ImdbWatchlistCsvParser.Parse(new StringReader(string.Empty));

        result.Rows.Should().BeEmpty();
        result.SkippedRowCount.Should().Be(0);
    }

    [Test]
    public void Parse_Should_Throw_WhenRequiredColumnsAreMissing()
    {
        var csv = "Position,Title\n1,Something";

        var act = () => ImdbWatchlistCsvParser.Parse(new StringReader(csv));

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Parse_Should_CapAtMaxRows()
    {
        var lines = new List<string> { Header };
        for (var i = 1; i <= ImdbWatchlistCsvParser.MaxRows + 10; i++)
            lines.Add($"{i},tt{i:D7},2016-01-16,2016-01-16,,\"Title {i}\",\"Title {i}\",https://example.com,Movie,5.0,100,2015,\"Drama\",1,2015-12-11,\"Someone\",,");

        var result = ImdbWatchlistCsvParser.Parse(new StringReader(string.Join('\n', lines)));

        result.Rows.Should().HaveCount(ImdbWatchlistCsvParser.MaxRows);
    }
}
