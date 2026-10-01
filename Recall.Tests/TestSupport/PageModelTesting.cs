using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;

namespace Recall.Tests.TestSupport;

/// <summary>
/// What a page model needs to run outside the framework: somewhere for toasts
/// to land, and a way to read them back.
/// </summary>
public static class PageModelTesting
{
    public static T WithTempData<T>(this T page) where T : PageModel
    {
        page.TempData = new TempDataDictionary(new DefaultHttpContext(), Mock.Of<ITempDataProvider>());
        return page;
    }

    public static string? SuccessToast(this PageModel page) => page.TempData["Toast.Success"] as string;

    public static string? ErrorToast(this PageModel page) => page.TempData["Toast.Error"] as string;

    public static string? InfoToast(this PageModel page) => page.TempData["Toast.Info"] as string;
}
