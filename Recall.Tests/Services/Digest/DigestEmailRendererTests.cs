using AwesomeAssertions;
using Recall.Web.Services.Digest;

namespace Recall.Tests.Services.Digest;

[TestFixture]
public sealed class DigestEmailRendererTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static readonly DigestLinks Links = new("https://recall.example", "https://recall.example/Digest/Unsubscribe?token=abc");

    private static DigestSection<T> Section<T>(params T[] items) => new(items, 0);

    private static DigestContent Full() => new(
        Section(new DigestPremiere(1, "Severance", 3, 101, Today.AddDays(-2))),
        Section(
            new DigestEpisodeLine(2, "Fish & <Chips>", 1, 4, 5, 2, 201, null, Today.AddDays(-3)),
            new DigestEpisodeLine(3, "The Bear", 2, 6, 6, 1, 301, "Fishes", Today.AddDays(-1))),
        new DigestSection<DigestEpisodeLine>(
            [new DigestEpisodeLine(4, "Silo", 4, 1, 1, 1, 401, null, Today.AddDays(2))], MoreCount: 3));

    private static DigestEmail Render(DigestContent? content = null) =>
        DigestEmailRenderer.Render(content ?? Full(), "saga", Today, Links);

    [Test]
    public void Subject_Should_SummariseTheSectionsThatHaveSomething()
    {
        Render().Subject.Should().Be("Your week on Recall: 1 new season, 3 to watch, 1 coming up");

        var onlyComing = new DigestContent(
            DigestSection<DigestPremiere>.Empty,
            DigestSection<DigestEpisodeLine>.Empty,
            Section(new DigestEpisodeLine(4, "Silo", 4, 1, 2, 2, 401, null, Today.AddDays(2))));

        Render(onlyComing).Subject.Should().Be("Your week on Recall: 2 coming up");
    }

    [Test]
    public void BothParts_Should_CarryTheTheTvDbAttribution_AndTheWayToTurnTheEmailOff()
    {
        var email = Render();

        foreach (var part in new[] { email.TextBody, email.HtmlBody })
        {
            part.Should().Contain("Metadata provided by TheTVDB").And.Contain("https://thetvdb.com");
            part.Should().Contain("https://recall.example/Digest/Unsubscribe?token=abc");
            part.Should().Contain("https://recall.example/Account/Profile");
        }
    }

    [Test]
    public void BothParts_Should_LinkStraightToTheEpisodeAndSeriesPages()
    {
        var email = Render();

        foreach (var part in new[] { email.TextBody, email.HtmlBody })
        {
            part.Should().Contain("https://recall.example/Episodes/Details/101", "the new season's first episode");
            part.Should().Contain("https://recall.example/Episodes/Details/201", "the first episode of a run to watch");
            part.Should().Contain("https://recall.example/Series/Details/4", "an upcoming episode links to its series");
        }
    }

    [Test]
    public void TheText_Should_SayWhatIsInEachSection()
    {
        var text = Render().TextBody;

        text.Should().Contain("A new season of Severance is out: season 3 started Wed, Sep 30.");
        text.Should().Contain("Fish & <Chips>: S01 · E04–E05 (2 episodes)");
        text.Should().Contain("The Bear: S02 · E06 “Fishes”");
        text.Should().Contain("Sun, Oct 4: Silo, S04 · E01");
        text.Should().Contain("...and 3 more in your library: https://recall.example/Library");
    }

    [Test]
    public void TheHtml_Should_EncodeNames_AndLoadNothingFromAnywhere()
    {
        var html = Render().HtmlBody;

        html.Should().Contain("Fish &amp; &lt;Chips&gt;").And.NotContain("<Chips>");
        html.Should().NotContain("<img", "no posters, no logos and no tracking pixel");
        html.Should().NotContain("<script").And.NotContain("<link ");
        html.Should().NotContain("src=", "nothing in the email is fetched when it is opened");
    }

    [Test]
    public void TheHtml_Should_DeclareBothColourSchemes()
    {
        var html = Render().HtmlBody;

        html.Should().Contain("<meta name=\"color-scheme\" content=\"light dark\">");
        html.Should().Contain("prefers-color-scheme: dark");
    }

    [Test]
    public void AnEmptySection_Should_LeaveNoHeadingBehind()
    {
        var onlyReady = new DigestContent(
            DigestSection<DigestPremiere>.Empty,
            Section(new DigestEpisodeLine(3, "The Bear", 2, 6, 6, 1, 301, "Fishes", Today.AddDays(-1))),
            DigestSection<DigestEpisodeLine>.Empty);

        var email = Render(onlyReady);

        email.TextBody.Should().Contain("READY TO WATCH").And.NotContain("NEW SEASONS").And.NotContain("COMING UP");
        email.HtmlBody.Should().Contain("Ready to watch").And.NotContain("New seasons").And.NotContain("Coming up");
    }
}
