using System.Globalization;
using AwesomeAssertions;
using Recall.Web.Infrastructure.Hosting;

namespace Recall.Tests.Infrastructure;

[TestFixture]
[NonParallelizable] // swaps the process-wide default culture for the duration of the test
public class AppCultureTests
{
    private CultureInfo? _originalCulture;
    private CultureInfo? _originalUiCulture;

    [SetUp]
    public void SetUp()
    {
        _originalCulture = CultureInfo.DefaultThreadCurrentCulture;
        _originalUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    }

    [TearDown]
    public void TearDown()
    {
        CultureInfo.DefaultThreadCurrentCulture = _originalCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _originalUiCulture;
    }

    [Test]
    public void PinToEnglish_Should_MakeNewThreadsFormatInEnglish_WhateverTheHostWasSetTo()
    {
        // Stand in for a Swedish host — the configuration this was written to stop leaking through.
        var swedish = CultureInfo.GetCultureInfo("sv-SE");
        CultureInfo.DefaultThreadCurrentCulture = swedish;
        CultureInfo.DefaultThreadCurrentUICulture = swedish;

        AppCulture.PinToEnglish();

        var (date, shortDate, number, culture) = OnNewThread(() => (
            new DateTime(2026, 10, 1).ToString("dddd, MMMM d"),
            new DateOnly(2026, 10, 1).ToString("ddd, MMM d"),
            1234.5.ToString("N1"),
            CultureInfo.CurrentCulture.Name));

        culture.Should().Be("en-US");
        date.Should().Be("Thursday, October 1");
        shortDate.Should().Be("Thu, Oct 1");
        number.Should().Be("1,234.5");
    }

    // A thread with no culture of its own shows what the process default now is — the same way
    // a request or job thread picks it up. UnsafeStart, because the test runner pins a culture on
    // the test thread, and a normal Start would carry that across in the execution context.
    private static T OnNewThread<T>(Func<T> read)
    {
        T result = default!;
        var thread = new Thread(() => result = read());
        thread.UnsafeStart();
        thread.Join();
        return result;
    }
}
