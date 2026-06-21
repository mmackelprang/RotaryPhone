using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvReadStateClientTests
{
    private static GvReadStateClient NewClient(HttpClient http) =>
        new(new UpdateReadPayloadBuilder(), NullLogger<GvReadStateClient>.Instance);

    [Fact]
    public async Task MarkVoicemail_PostsUpdateread_AndReturnsAppliedOn200()
    {
        string? capturedUrl = null;
        var http = new HttpClient(new CapturingHandler((req, _) =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = NewClient(http);

        var result = await client.MarkVoicemailReadAsync(http, "vm.1", "t.+19195551234", isRead: true);

        Assert.Equal(GvUpdateReadOutcome.Applied, result.Outcome);
        Assert.Null(result.Error);
        Assert.Contains("api2thread/updateread", capturedUrl);
        Assert.Contains("alt=protojson", capturedUrl);
    }

    [Fact]
    public async Task MarkSmsThread_PostsOnePerMessageId_AppliedOnAll200()
    {
        var posts = 0;
        var http = new HttpClient(new CapturingHandler((_, _) =>
        {
            posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = NewClient(http);

        var result = await client.MarkSmsThreadReadAsync(
            http, "t.abc", new[] { "m.1", "m.2" }, isRead: true);

        Assert.Equal(GvUpdateReadOutcome.Applied, result.Outcome);
        Assert.Equal(2, posts);                         // one updateread per message id (per-thread grain)
    }

    [Fact]
    public async Task Mark_NonSuccess_ClassifiesUpstreamError_NoThrow()
    {
        var http = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("x") }));
        var client = NewClient(http);

        var result = await client.MarkVoicemailReadAsync(http, "vm.1", "t.1", isRead: true);

        Assert.Equal(GvUpdateReadOutcome.UpstreamError, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MarkSmsThread_FirstPostFails_StopsAndClassifiesUpstreamError()
    {
        // Honest status: if any message in the thread fails, the thread mark did NOT fully apply → fail
        // (RadioConsole reconciles on the next list/poll). Do not claim "applied" on a partial mark.
        var posts = 0;
        var http = new HttpClient(new CapturingHandler((_, _) =>
        {
            posts++;
            return new HttpResponseMessage(
                posts == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }));
        var client = NewClient(http);

        var result = await client.MarkSmsThreadReadAsync(http, "t.abc", new[] { "m.1", "m.2" }, true);

        Assert.Equal(GvUpdateReadOutcome.UpstreamError, result.Outcome);
        Assert.Equal(1, posts);                         // short-circuits on the first failure
    }

    [Fact]
    public async Task Mark_NullClient_ClassifiesAdapterUnavailable_NoThrow()
    {
        var client = NewClient(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK))));
        var result = await client.MarkVoicemailReadAsync(
            authenticatedClient: null, "vm.1", "t.1", isRead: true);
        Assert.Equal(GvUpdateReadOutcome.AdapterUnavailable, result.Outcome);
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
