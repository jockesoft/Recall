using System.Net;
using System.Text;

namespace Recall.Tests.TestSupport;

/// <summary>
/// Stands in for the network: answers every request with whatever
/// <paramref name="respond"/> returns (or throws), and keeps what it was sent.
/// </summary>
public sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri? Uri, string? Body)> Requests { get; } = [];

    public static StubHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public static StubHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri, body));
        return respond(request);
    }
}
