using System.Net;
using System.Text;
using RotaryPhoneController.GVBridge.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvAccountClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task IsHealthyAsync_ReturnsTrueOn200()
    {
        var handler = new MockHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[[[[1,null,0]]]]", Encoding.UTF8, "application/json")
            });
        var client = new GvAccountClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvAccountClient>.Instance);

        Assert.True(await client.IsHealthyAsync());
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsFalseOn401()
    {
        var handler = new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new GvAccountClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvAccountClient>.Instance);

        Assert.False(await client.IsHealthyAsync());
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
