using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Infrastructure.External.TheTvDb.Dto.Search;
using Recall.Web.Mappings;

namespace Recall.Tests.Mappings;

[TestFixture]
public class SearchResultMappingsTests
{
    private static SearchResultDto Dto(string? overview, string? primaryLanguage, Dictionary<string, string>? overviews = null) => new()
    {
        TvdbId = 1,
        Name = "Dark",
        Type = "series",
        Overview = overview,
        PrimaryLanguage = primaryLanguage,
        Overviews = overviews
    };

    [Test]
    public void ToDomain_Should_PreferTheEnglishOverview_OverTheTitlesOwnLanguage()
    {
        var item = Dto("Ein Kind verschwindet.", "deu", new() { ["deu"] = "Ein Kind verschwindet.", ["eng"] = "A child goes missing." }).ToDomain()!;

        item.Overview.Should().Be("A child goes missing.");
        item.OverviewLanguage.Should().BeNull("English needs no lang attribute on an English page");
    }

    [Test]
    public void ToDomain_Should_KeepTheOriginalOverview_AndSayWhatLanguageItIsIn_WhenThereIsNoEnglishOne()
    {
        var item = Dto("Ein Kind verschwindet.", "deu", new() { ["deu"] = "Ein Kind verschwindet.", ["eng"] = " " }).ToDomain()!;

        item.Overview.Should().Be("Ein Kind verschwindet.");
        item.OverviewLanguage.Should().Be("de");
    }

    [Test]
    public void ToDomain_Should_NotMarkAnEnglishTitlesOverview()
    {
        Dto("A chemistry teacher.", "eng").ToDomain()!.OverviewLanguage.Should().BeNull();
        Dto("A chemistry teacher.", null).ToDomain()!.OverviewLanguage.Should().BeNull("an unknown language is not guessed at");
        Dto(null, "deu").ToDomain()!.Overview.Should().BeNull();
    }

    [TestCase("deu", "de")]
    [TestCase("fra", "fr")]
    [TestCase("jpn", "ja")]
    [TestCase("kor", "ko")]
    [TestCase("SPA", "es")]
    [TestCase("pt", "pt-BR")]
    [TestCase("zhtw", "zh-Hant")]
    [TestCase("xx!", null)]
    [TestCase("toolong", null)]
    public void ToLanguageTag_Should_TurnTheTvdbsCodeIntoALangAttributeValue(string code, string? expected)
    {
        SearchResultMappings.ToLanguageTag(code).Should().Be(expected);
    }

    [Test]
    public void ToDomain_Should_DropWhatIsNeitherASeriesNorAMovie()
    {
        new SearchResultDto { TvdbId = 1, Name = "Somebody", Type = "person" }.ToDomain().Should().BeNull();
        new SearchResultDto { TvdbId = 2, Name = "Heat", Type = "movie" }.ToDomain()!.Type.Should().Be(SearchResultType.Movie);
    }
}
