using System.Net;
using System.Text;

namespace BTCPayServer.Plugins.SweepSell.Tests;

/// <summary>Answers each request path with canned JSON and records what was sent. All responses are made up.</summary>
class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, string> Responses { get; } = new();
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request, body));
        var path = request.RequestUri!.AbsolutePath;
        return Responses.TryGetValue(path, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
    }

    public HttpClient Client() => new(this);
}
