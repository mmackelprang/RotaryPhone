using System.Net;
using System.Text;
using RotaryPhoneController.GVBridge.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvCallClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task InitiateAsync_PostsToCorrectEndpoint()
    {
        string? capturedUrl = null;
        var handler = new MockHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new GvCallClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvCallClient>.Instance);

        await client.InitiateAsync("+15551234567");

        Assert.NotNull(capturedUrl);
        Assert.Contains("/call/create", capturedUrl!);
    }

    [Fact]
    public async Task HangupAsync_PostsToCorrectEndpoint()
    {
        string? capturedUrl = null;
        var handler = new MockHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new GvCallClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvCallClient>.Instance);

        await client.HangupAsync("call-123");

        Assert.NotNull(capturedUrl);
        Assert.Contains("/call/cancel", capturedUrl!);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
