using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;
using Recall.Tests.TestSupport;
using Recall.Web.Extensions;

namespace Recall.Tests.Extensions;

/// <summary>
/// <see cref="TempDataValueExtensions"/>: each typed read accepts the value as
/// it was written (a string) and as the cookie serializer may return it (a
/// Guid, a DateTime, an int, a bool), and gives null for anything else.
/// </summary>
[TestFixture]
public sealed class TempDataValueExtensionsTests
{
    private const string Key = "k";
    private static readonly Guid Id = new("0b5e0c1d-7a6f-4a1e-9a55-2f4c7c1a9d01");

    private static ITempDataDictionary With(object? value)
    {
        var tempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>());
        if (value is not null) tempData[Key] = value;
        return tempData;
    }

    [Test]
    public void GetGuid_Should_ReadAGuid_ItsTextInAnyFormat_AndNothingElse()
    {
        With(Id).GetGuid(Key).Should().Be(Id);
        With(Id.ToString("D")).GetGuid(Key).Should().Be(Id);
        With(Id.ToString("N")).GetGuid(Key).Should().Be(Id);

        With(null).GetGuid(Key).Should().BeNull();
        With("not a guid").GetGuid(Key).Should().BeNull();
        With(42).GetGuid(Key).Should().BeNull();
    }

    [Test]
    public void GetInt32_Should_ReadAnInt_OrItsInvariantText()
    {
        With(370112).GetInt32(Key).Should().Be(370112);
        With("370112").GetInt32(Key).Should().Be(370112);
        With("-5").GetInt32(Key).Should().Be(-5);

        With(null).GetInt32(Key).Should().BeNull();
        With("1,234").GetInt32(Key).Should().BeNull("no group separators, in any culture");
        With("99999999999").GetInt32(Key).Should().BeNull("too big for an int");
        With(Id).GetInt32(Key).Should().BeNull();
    }

    [Test]
    public void GetInt64_Should_ReadTicksWrittenAsText_OrAnInt()
    {
        var ticks = new DateTime(2026, 9, 4, 18, 30, 15, DateTimeKind.Utc).Ticks;

        With(ticks.ToString()).GetInt64(Key).Should().Be(ticks);
        With(7).GetInt64(Key).Should().Be(7);

        With(null).GetInt64(Key).Should().BeNull();
        With("soon").GetInt64(Key).Should().BeNull();
    }

    [Test]
    public void GetText_Should_GiveTextBack_WhateverTheSerializerMadeOfIt()
    {
        With("Marked 7 episodes as watched.").GetText(Key).Should().Be("Marked 7 episodes as watched.");
        With(Id).GetText(Key).Should().Be(Id.ToString("D"));
        With(42).GetText(Key).Should().Be("42");
        With(true).GetText(Key).Should().Be("true");
        With(new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc)).GetText(Key).Should().Be("2026-11-22T10:00:00.0000000Z");

        With(null).GetText(Key).Should().BeNull();
    }

    [Test]
    public void TheReads_Should_WorkOnWhatTheRealCookieBringsBack()
    {
        var ticks = DateTime.UtcNow.Ticks;
        var tempData = CookieTempData.RoundTrip(p =>
        {
            p.TempData["guid"] = Id.ToString("D");
            p.TempData["id"] = "370112";
            p.TempData["ticks"] = ticks.ToString();
            p.TempData["text"] = "Finished";
        });

        tempData.GetGuid("guid").Should().Be(Id);
        tempData.GetInt32("id").Should().Be(370112);
        tempData.GetInt64("ticks").Should().Be(ticks);
        tempData.GetText("text").Should().Be("Finished");
    }
}
