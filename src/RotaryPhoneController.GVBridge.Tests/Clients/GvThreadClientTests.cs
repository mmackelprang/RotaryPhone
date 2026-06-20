using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvThreadClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvThreadClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new MockHandler(handler)), BaseUrl, ApiKey,
               new PositionalGvThreadParser(), NullLogger<GvThreadClient>.Instance);

    [Fact]
    public async Task ListThreadsAsync_PostsToApi2ThreadList()
    {
        string? capturedUrl = null;
        var client = NewClient(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"threads":[],"nextPageToken":null}""") };
        });

        await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/api2thread/list", capturedUrl!);
        Assert.Contains($"key={ApiKey}", capturedUrl!);
    }

    [Fact]
    public async Task ListThreadsAsync_ParsesThreadsViaParser()
    {
        var body = """
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false]]]],
         "nextPageToken":"P2"}
        """;
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) });

        var result = await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.Single(result.Threads);
        Assert.Equal("t.+19195551234", result.Threads[0].ThreadId);
        Assert.Equal("P2", result.NextPageToken);
    }

    [Fact]
    public async Task ListThreadsAsync_OnNon200_ReturnsEmptyResult()
    {
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.Empty(result.Threads);
        Assert.False(result.Succeeded);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
