using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvSmsClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
    }

    private static HttpResponseMessage SmsListResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false],
               ["m.2","t.+19195551234",1,"+19195551234","hello back",1718841700000,true]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task ListThreadsAsync_ReturnsThreadNodes()
    {
        var client = NewClient(_ => SmsListResponse());
        var result = await client.ListThreadsAsync(count: 20);
        Assert.True(result.Succeeded);
        Assert.Single(result.Threads);
        Assert.Equal("t.+19195551234", result.Threads[0].ThreadId);
    }

    [Fact]
    public async Task ListMessagesAsync_ReturnsMessagesForThread()
    {
        var client = NewClient(_ => SmsListResponse());
        var result = await client.ListMessagesAsync("t.+19195551234", count: 50);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("Inbound", result.Messages[0].Direction);
        Assert.Equal("Outbound", result.Messages[1].Direction);
    }

    [Fact]
    public async Task ListMessagesAsync_FiltersByThreadId()
    {
        var client = NewClient(_ => SmsListResponse());
        var result = await client.ListMessagesAsync("t.+nonexistent", count: 50);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task ListRecentMessagesAsync_OnFailure_ReturnsNotSucceeded()
    {
        var client = NewClient(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        var result = await client.ListRecentMessagesAsync(count: 50);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Messages);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
