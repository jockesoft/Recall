using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Recall.Web.Extensions;

public static class PageModelToastExtensions
{
    private const string SuccessKey = "Toast.Success";
    private const string ErrorKey = "Toast.Error";
    private const string InfoKey = "Toast.Info";

    extension(PageModel pageModel)
    {
        public void SetSuccessToast(string message)
            => pageModel.TempData[SuccessKey] = message;

        public void SetErrorToast(string message)
            => pageModel.TempData[ErrorKey] = message;

        public void SetInfoToast(string message)
            => pageModel.TempData[InfoKey] = message;
    }
}