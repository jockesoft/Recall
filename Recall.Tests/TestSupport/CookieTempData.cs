using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Recall.Tests.TestSupport;

/// <summary>
/// TempData as it really travels between a POST and the page that shows its
/// toast: written in one request, saved by ASP.NET Core's own
/// <see cref="CookieTempDataProvider"/> (its serializer, Data Protection and
/// cookie chunking), and loaded from that cookie in the next request.
/// </summary>
/// <remarks>
/// A plain <see cref="TempDataDictionary"/> over a mocked provider (what
/// <c>WithTempData()</c> gives page-model tests) hands back exactly what was
/// written, so it cannot show that the cookie serializer returns a GUID-shaped
/// string as a <see cref="Guid"/>; a test of what a toast reads needs this.
/// </remarks>
public static class CookieTempData
{
    private static readonly ServiceProvider Services = BuildServices();

    private sealed class TempDataPage : PageModel;

    /// <summary>
    /// Runs <paramref name="write"/> against a page model's TempData (as the
    /// toast helpers are called), then returns the TempData the next request
    /// reads from the cookie that was set.
    /// </summary>
    public static ITempDataDictionary RoundTrip(Action<PageModel> write)
    {
        var provider = Services.GetRequiredService<ITempDataProvider>();
        provider.Should().BeOfType<CookieTempDataProvider>("the round trip must go through the app's real TempData provider");

        var post = new DefaultHttpContext();
        var written = new TempDataDictionary(post, provider);
        write(new TempDataPage { TempData = written });
        written.Save();

        var cookies = SetCookieHeaderValue.ParseList(post.Response.Headers.SetCookie.ToList());
        cookies.Should().NotBeEmpty("TempData with values is saved as a cookie");

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        var read = new TempDataDictionary(next, provider);
        read.Load();
        return read;
    }

    /// <summary>The TempData <paramref name="write"/> leaves in the same request, before any cookie: exactly what was written.</summary>
    public static ITempDataDictionary InRequest(Action<PageModel> write)
    {
        var page = new TempDataPage
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), Services.GetRequiredService<ITempDataProvider>())
        };
        write(page);
        return page.TempData;
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Keys in memory: nothing is written to the developer's key ring.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddRazorPages();
        return services.BuildServiceProvider();
    }
}
