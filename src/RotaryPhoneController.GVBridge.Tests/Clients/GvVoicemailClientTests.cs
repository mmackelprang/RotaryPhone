using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvVoicemailClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvVoicemailClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvVoicemailClient(threadClient, parser, new StubFetcher(),
            NullLogger<GvVoicemailClient>.Instance);
    }

    private sealed class StubFetcher : RotaryPhoneController.GVBridge.Clients.IGvRecordingFetcher
    {
        public Task<RotaryPhoneController.GVBridge.Clients.GvRecordingFetchResult> FetchAsync(
            string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new RotaryPhoneController.GVBridge.Clients.GvRecordingFetchResult(
                true, new byte[] { 1 }, "audio/mpeg"));
    }

    [Fact]
    public async Task ListVoicemailsAsync_ParsesMediaIdAndTranscript()
    {
        var body = """
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
         "nextPageToken":null}
        """;
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) });

        var result = await client.ListVoicemailsAsync(count: 20);

        Assert.Single(result.Items);
        Assert.Equal("vm.1", result.Items[0].MessageId);
        Assert.Equal("media-1", result.Items[0].MediaId);
        Assert.Equal("call me", result.Items[0].Transcript);
    }

    [Fact]
    public async Task ListVoicemailsAsync_OnFailure_ReturnsEmptyNotSucceeded()
    {
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.ListVoicemailsAsync(count: 20);
        Assert.Empty(result.Items);
        Assert.False(result.Succeeded);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
