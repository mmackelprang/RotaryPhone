using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvRecordingFetcherTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvRecordingFetcher NewFetcher(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new MockHandler(handler)), BaseUrl, ApiKey,
               NullLogger<GvRecordingFetcher>.Instance);

    [Fact]
    public async Task FetchAsync_RequestsRecordingGetWithMediaId()
    {
        string? capturedUrl = null;
        var fetcher = NewFetcher(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return resp;
        });

        var result = await fetcher.FetchAsync("media-abc-123");

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.NotNull(capturedUrl);
        Assert.Contains("/recording/get", capturedUrl!);
        Assert.Contains("id=media-abc-123", capturedUrl!);
    }

    [Fact]
    public async Task FetchAsync_OnNon200_ReturnsFailure()
    {
        var fetcher = NewFetcher(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await fetcher.FetchAsync("media-abc-123");
        Assert.False(result.Success);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public async Task FetchAsync_DefaultsContentTypeToAudioMpeg()
    {
        var fetcher = NewFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 9 }) }); // no content-type header
        var result = await fetcher.FetchAsync("m1");
        Assert.True(result.Success);
        Assert.Equal("audio/mpeg", result.ContentType);
    }

    [Fact]
    public async Task FetchAsync_WhenProviderClientUnavailable_ReturnsFailureWithoutThrowing()
    {
        // Activation-order seam: adapter not yet activated → provider returns a null HttpClient.
        // The fetcher must degrade to Success=false (no throw) rather than crash a request handler.
        var provider = new NullClientProvider();
        var fetcher = new GvRecordingFetcher(provider, NullLogger<GvRecordingFetcher>.Instance);

        var result = await fetcher.FetchAsync("media-abc-123");

        Assert.False(result.Success);
        Assert.Null(result.Bytes);
    }

    private sealed class NullClientProvider : IGvAuthenticatedClientProvider
    {
        public HttpClient? GetAuthenticatedClient() => null;
        public string ApiBaseUrl => BaseUrl;
        public string ApiKey => GvRecordingFetcherTests.ApiKey;
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
