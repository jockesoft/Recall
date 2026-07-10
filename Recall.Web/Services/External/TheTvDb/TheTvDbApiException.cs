namespace Recall.Web.Services.External.TheTvDb;

public sealed class TheTvDbApiException : Exception
{
    public int? StatusCode { get; }

    public TheTvDbApiException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}