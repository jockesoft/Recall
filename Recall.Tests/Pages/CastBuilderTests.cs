using AwesomeAssertions;
using Recall.Web.Domain.TheTvDb;
using Recall.Web.Pages.Shared;

namespace Recall.Tests.Pages;

[TestFixture]
public sealed class CastBuilderTests
{
    private static Character Role(int personId, string person, string type, string? character = null, int? sort = null, string? photo = null) =>
        new() { PeopleId = personId, PersonName = person, PeopleType = type, Name = character, Sort = sort, PersonImageUrl = photo, Url = $"https://thetvdb.com/people/{personId}" };

    [Test]
    public void Build_Should_SeparateCastFromCrew()
    {
        var (cast, crew) = CastBuilder.Build(
        [
            Role(1, "Cillian Murphy", "Actor", "J. Robert Oppenheimer"),
            Role(2, "Christopher Nolan", "Director"),
            Role(3, "Emily Blunt", "Guest Star", "Kitty"),
        ]);

        cast.Select(p => p.Name).Should().Equal("Cillian Murphy", "Emily Blunt");
        crew.Select(p => p.Name).Should().Equal("Christopher Nolan");
    }

    [Test]
    public void Build_Should_ShowTheCharacterForActors_AndTheJobForCrew()
    {
        var (cast, crew) = CastBuilder.Build(
        [
            Role(1, "Bryan Cranston", "Actor", "Walter White"),
            Role(2, "Vince Gilligan", "Creator"),
        ]);

        cast.Single().Detail.Should().Be("Walter White");
        crew.Single().Detail.Should().Be("Creator");
    }

    [Test]
    public void Build_Should_MergeAPersonWithSeveralJobs_IntoOneEntry()
    {
        var (_, crew) = CastBuilder.Build(
        [
            Role(2, "Christopher Nolan", "Writer"),
            Role(2, "Christopher Nolan", "Director"),
            Role(2, "Christopher Nolan", "Producer"),
            Role(5, "Emma Thomas", "Producer"),
        ]);

        crew.Should().HaveCount(2);
        crew[0].Name.Should().Be("Christopher Nolan");
        crew[0].Detail.Should().Be("Director, Writer, Producer", "jobs are listed in a fixed order, director first");
        crew[1].Detail.Should().Be("Producer");
    }

    [Test]
    public void Build_Should_MergeAnActorPlayingSeveralCharacters()
    {
        var (cast, _) = CastBuilder.Build(
        [
            Role(7, "Tatiana Maslany", "Actor", "Sarah Manning"),
            Role(7, "Tatiana Maslany", "Actor", "Cosima Niehaus"),
            Role(7, "Tatiana Maslany", "Actor", "Sarah Manning"),
        ]);

        cast.Should().ContainSingle().Which.Detail.Should().Be("Sarah Manning / Cosima Niehaus");
    }

    [Test]
    public void Build_Should_ListSomeoneWhoActsAndDirects_InBothLists()
    {
        var (cast, crew) = CastBuilder.Build(
        [
            Role(9, "Ben Affleck", "Actor", "Tony Mendez"),
            Role(9, "Ben Affleck", "Director"),
        ]);

        cast.Single().Detail.Should().Be("Tony Mendez");
        crew.Single().Detail.Should().Be("Director");
    }

    [Test]
    public void Build_Should_KeepTheTvDbOrder_ForTheCast()
    {
        var (cast, _) = CastBuilder.Build(
        [
            Role(1, "Third", "Actor", "C", sort: 3),
            Role(2, "First", "Actor", "A", sort: 1),
            Role(3, "Unsorted", "Actor", "Z"),
            Role(4, "Second", "Actor", "B", sort: 2),
        ]);

        cast.Select(p => p.Name).Should().Equal("First", "Second", "Third", "Unsorted");
    }

    [Test]
    public void Build_Should_TreatARowWithoutAType_AsCast_WhenItNamesACharacter()
    {
        var (cast, crew) = CastBuilder.Build(
        [
            new Character { PersonName = "Someone", Name = "The Stranger" },
            new Character { PersonName = "Someone Else" },
        ]);

        cast.Single().Name.Should().Be("Someone");
        crew.Single().Name.Should().Be("Someone Else");
        crew.Single().Detail.Should().BeNull();
    }

    [Test]
    public void Build_Should_CarryThePhotoAndLink_AndLeaveThemNullWhenBlank()
    {
        var (cast, _) = CastBuilder.Build(
        [
            Role(1, "With Photo", "Actor", "A", photo: "https://img/1.jpg"),
            new Character { PeopleId = 2, PersonName = "Without", PeopleType = "Actor", Name = "B", PersonImageUrl = " ", Url = "" },
        ]);

        cast[0].ImageUrl.Should().Be("https://img/1.jpg");
        cast[0].Url.Should().Be("https://thetvdb.com/people/1");
        cast[1].ImageUrl.Should().BeNull();
        cast[1].Url.Should().BeNull();
    }

    [TestCase("Christopher Nolan", "CN")]
    [TestCase("Robert Downey Jr.", "RJ")]
    [TestCase("Zendaya", "Z")]
    [TestCase("  ", "?")]
    [TestCase("50 Cent", "C")]
    public void Initials_Should_UseTheFirstAndLastWord(string name, string expected)
    {
        new CastPerson(name, null, null, null).Initials.Should().Be(expected);
    }
}
