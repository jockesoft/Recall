using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Recall.Web.Extensions;
using Recall.Web.Infrastructure.Persistence.Repositories;
using Recall.Web.Services.WatchTracking;

namespace Recall.Tests.Pipeline;

/// <summary>
/// The toast partial (<c>_ToastMessages.cshtml</c>), rendered by the real view
/// engine: what each kind of toast carries for the script that closes it
/// (<c>js/tvdb-toasts.js</c>). A toast that closes itself has
/// <c>data-toast-duration</c> and a countdown bar; an error or a warning has
/// neither.
/// </summary>
[TestFixture]
public sealed class ToastPartialTests
{
    private static readonly DateTime Stamp = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private RecallWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void StartApplication()
    {
        _factory = new RecallWebApplicationFactory();
        _ = _factory.Server;
    }

    [OneTimeTearDown]
    public void StopApplication() => _factory.Dispose();

    private sealed class ToastPage : PageModel;

    /// <summary>Lets a test set toasts with the helpers the page models use, then renders the partial from that TempData.</summary>
    private async Task<string> RenderAsync(Action<PageModel> setToasts)
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Path = "/Dashboard";
        // Endpoint routing, as in a real request: the Undo forms build their addresses with it.
        httpContext.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "toast partial test"));
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        var tempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        setToasts(new ToastPage { TempData = tempData });

        var view = services.GetRequiredService<IRazorViewEngine>()
            .GetView(executingFilePath: null, "/Pages/Shared/_ToastMessages.cshtml", isMainPage: false);
        view.Success.Should().BeTrue("the partial is where every toast is drawn");

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view.View,
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            tempData,
            writer,
            new HtmlHelperOptions());

        await view.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    /// <summary>The opening tags of the toasts, in order.</summary>
    private static List<string> ToastTags(string html) =>
        Regex.Matches(html, "<div class=\"alert [^>]*>").Select(m => m.Value).ToList();

    private static int Bars(string html) => Regex.Matches(html, "class=\"tvdb-toast-timer\"").Count;

    [Test]
    public async Task ASuccessToast_Should_CarryFiveSeconds_AndACountdownBar()
    {
        var html = await RenderAsync(page => page.SetSuccessToast("Rating saved."));

        var toast = ToastTags(html).Should().ContainSingle().Subject;
        toast.Should().Contain("alert-success").And.Contain("data-toast-duration=\"5000\"");
        toast.Should().Contain("role=\"alert\"", "it is announced, as before");
        toast.Should().NotContain("data-toast-actions");
        html.Should().Contain("Rating saved.");
        html.Should().Contain("<span class=\"tvdb-toast-timer\" aria-hidden=\"true\"></span>");
        html.Should().Contain("aria-label=\"Close\"", "the close button stays");
    }

    [Test]
    public async Task AnInfoToast_Should_CarryFiveSeconds_AndACountdownBar()
    {
        var html = await RenderAsync(page => page.SetInfoToast("Rating removed."));

        ToastTags(html).Should().ContainSingle().Which.Should().Contain("alert-info").And.Contain("data-toast-duration=\"5000\"");
        Bars(html).Should().Be(1);
    }

    [Test]
    public async Task AToastWithAnUndo_Should_CarryTenSeconds_AndNoLongerPersist()
    {
        var html = await RenderAsync(page =>
            page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", 42, new WatchedBatch(3, Stamp)));

        var toast = ToastTags(html).Should().ContainSingle().Subject;
        toast.Should().Contain("data-toast-duration=\"10000\"").And.Contain("data-toast-actions=\"true\"");
        html.Should().NotContain("data-toast-persist");
        html.Should().Contain("handler=UndoWatched").And.Contain(">Undo</button>");
        html.Should().Contain($"name=\"stamp\" value=\"{Stamp.Ticks}\"");
        html.Should().Contain("name=\"returnUrl\" value=\"/Dashboard\"");
        Bars(html).Should().Be(1);
    }

    [Test]
    public async Task TheRateItAndStoppedToasts_Should_CarryTenSeconds()
    {
        var rateIt = await RenderAsync(page => page.SetWatchedToast(
            "Episode marked as watched.", new SeriesCaughtUp(42, "Chernobyl", Finished: true, UserHasRated: false), new DateOnly(2026, 10, 6)));

        ToastTags(rateIt).Should().ContainSingle().Which.Should()
            .Contain("data-toast-duration=\"10000\"").And.Contain("data-toast-kind=\"Finished\"");
        rateIt.Should().Contain(">Rate it</a>");

        var stopped = await RenderAsync(page =>
            page.SetStopWatchingToast(new StopWatchingResult(StopWatchingOutcome.Stopped, "Silo"), 42));

        ToastTags(stopped).Should().ContainSingle().Which.Should().Contain("data-toast-duration=\"10000\"");
        stopped.Should().Contain("handler=ResumeWatching").And.Contain("name=\"undo\" value=\"true\"");
    }

    [Test]
    public async Task AnErrorToast_Should_CarryNoDuration_AndNoCountdownBar()
    {
        var html = await RenderAsync(page => page.SetErrorToast("Could not update watched status right now."));

        var toast = ToastTags(html).Should().ContainSingle().Subject;
        toast.Should().Contain("alert-danger").And.NotContain("data-toast-duration");
        Bars(html).Should().Be(0, "a toast that does not close itself has no bar");
        html.Should().Contain("aria-label=\"Close\"");
    }

    [Test]
    public async Task AWarningToast_Should_CarryNoDuration_AndNoCountdownBar()
    {
        var html = await RenderAsync(page => page.SetWarningToast("Some rows could not be read."));

        ToastTags(html).Should().ContainSingle().Which.Should().Contain("alert-warning").And.NotContain("data-toast-duration");
        Bars(html).Should().Be(0);
    }

    [Test]
    public async Task SeveralToasts_Should_EachCarryTheirOwnDuration()
    {
        var html = await RenderAsync(page =>
        {
            page.SetSuccessToastWithWatchedUndo("Marked 3 episodes as watched.", 42, new WatchedBatch(3, Stamp));
            page.SetErrorToast("Could not load your rating.");
            page.SetInfoToast("Rating removed.");
        });

        var toasts = ToastTags(html);

        toasts.Should().HaveCount(3);
        toasts[0].Should().Contain("alert-success").And.Contain("data-toast-duration=\"10000\"");
        toasts[1].Should().Contain("alert-danger").And.NotContain("data-toast-duration");
        toasts[2].Should().Contain("alert-info").And.Contain("data-toast-duration=\"5000\"");
        Bars(html).Should().Be(2, "one per toast that closes itself");
    }

    [Test]
    public async Task NoToast_Should_RenderAnEmptyStack_AndNothingShouldTakeFocus()
    {
        var empty = await RenderAsync(_ => { });
        ToastTags(empty).Should().BeEmpty();
        empty.Should().Contain("tvdb-toast-stack");

        var one = await RenderAsync(page => page.SetSuccessToast("Saved."));
        one.Should().NotContain("autofocus").And.NotContain("tabindex", "a toast never takes focus when it appears");
        one.Should().NotContain("<script", "the one script is loaded by the layout");
    }

    [Test]
    public async Task EveryPage_Should_LoadTheOneToastScript()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/Privacy");

        // The served name carries a fingerprint (tvdb-toasts.<hash>.js).
        html.Should().MatchRegex(@"<script src=""/js/tvdb-toasts(\.[a-z0-9]+)?\.js").And.Contain("tvdb-toast-stack");
    }
}
