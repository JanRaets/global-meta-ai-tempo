using System.Net;
using System.Text;

namespace Tempo.Tests.Fakes;

/// <summary>Replays a fixed sequence of HTTP responses and records every request body.</summary>
public sealed class StubHttpHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private int index;

    public List<string> RequestBodies { get; } = [];
    public int Calls => index;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var (status, body) = responses[Math.Min(index, responses.Length - 1)];
        index++;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
