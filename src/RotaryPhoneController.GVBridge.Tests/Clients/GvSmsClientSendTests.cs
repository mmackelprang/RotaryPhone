using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvSmsClientSendTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsClient ReadOnlyClient(HttpClient http)
    {
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
    }

    [Fact]
    public async Task SendAsync_PostsExpectedPayload_AndReturnsQueuedOn200()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        var http = new HttpClient(new CapturingHandler((req, body) =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = ReadOnlyClient(http);

        var result = await client.SendAsync(http, "t.+19195551234", "hello world");

        Assert.True(result.Queued);
        Assert.Null(result.Error);
        Assert.Contains("api2thread/sendsms", capturedUrl);
        Assert.Contains("alt=protojson", capturedUrl);
        // ADR §4.1 payload shape: [null,null,null,null,"<text>","<threadId>"]
        using var doc = JsonDocument.Parse(capturedBody!);
        var arr = doc.RootElement;
        Assert.Equal(6, arr.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, arr[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[3].ValueKind);
        Assert.Equal("hello world", arr[4].GetString());
        Assert.Equal("t.+19195551234", arr[5].GetString());
    }

    [Fact]
    public async Task SendAsync_InvalidArgumentBody_ClassifiesAsInvalidArgument_NoThrow()
    {
        // GV signals a bad recipient with INVALID_ARGUMENT → controller will map to 400 invalid_number.
        var http = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("INVALID_ARGUMENT") }));
        var client = ReadOnlyClient(http);

        var result = await client.SendAsync(http, "t.+19195551234", "hi");

        Assert.False(result.Queued);                 // honest: NOT queued
        Assert.Equal(GvSendOutcome.InvalidArgument, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SendAsync_OtherNonSuccess_ClassifiesAsUpstreamError_NoThrow()
    {
        var http = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("backend error") }));
        var client = ReadOnlyClient(http);

        var result = await client.SendAsync(http, "t.+19195551234", "hi");

        Assert.False(result.Queued);
        Assert.Equal(GvSendOutcome.UpstreamError, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_NullClient_ClassifiesAsAdapterUnavailable_NoThrow()
    {
        var client = ReadOnlyClient(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK))));
        var result = await client.SendAsync(authenticatedClient: null, "t.+19195551234", "hi");
        Assert.False(result.Queued);
        Assert.Equal(GvSendOutcome.AdapterUnavailable, result.Outcome);  // adapter down → honest failure
        Assert.NotNull(result.Error);
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return handler(request, body);
        }
    }
}
