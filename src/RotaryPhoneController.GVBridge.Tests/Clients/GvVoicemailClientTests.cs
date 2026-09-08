using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvVoicemailClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";
    private const long Epoch = 1718841600000;

    private static GvVoicemailClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvVoicemailClient(threadClient, parser, new StubFetcher(),
            NullLogger<GvVoicemailClient>.Instance);
    }

    /// <summary>
    /// Same wiring as <see cref="NewClient"/>, but hands back the logger too — for the tests whose
    /// deliverable IS the log line (the saturation signal), not the returned data.
    /// </summary>
    private static (GvVoicemailClient Client, CapturingLogger<GvVoicemailClient> Log) NewLoggingClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var log = new CapturingLogger<GvVoicemailClient>();
        return (new GvVoicemailClient(threadClient, parser, new StubFetcher(), log), log);
    }

    /// <summary>N voicemail threads, each with <paramref name="messagesPerThread"/> messages.</summary>
    private static string VoicemailThreads(int threadCount, int messagesPerThread = 1)
        => GvWireBuilder.Response(Enumerable.Range(0, threadCount)
            .Select(t => VoicemailThread(t, messagesPerThread))
            .ToArray());

    /// <summary>
    /// <paramref name="threadCount"/> threads of which only the first <paramref name="threadsWithMessages"/>
    /// carry a message — a full page that nonetheless yields FEWER items than threads. A thread with no
    /// messages is a real shape (see <c>GvSmsControllerThreadIdDecodeTests</c>'s group thread).
    /// </summary>
    private static string SparseVoicemailThreads(int threadCount, int threadsWithMessages)
        => GvWireBuilder.Response(Enumerable.Range(0, threadCount)
            .Select(t => VoicemailThread(t, t < threadsWithMessages ? 1 : 0))
            .ToArray());

    private static string VoicemailThread(int index, int messageCount)
        => GvWireBuilder.Thread(
            $"t.{index}", folder: 4, isRead: 0, counterparty: "+19195551234",
            Enumerable.Range(0, messageCount)
                .Select(m => GvWireBuilder.Message($"vm.{index}.{m}", Epoch + m, "+19195551234",
                    GvWireBuilder.TypeVoicemail, isRead: 0))
                .ToArray());

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
        var body = GvWireBuilder.VoicemailResponse(
            threadId: "t.+19195551234", messageId: "vm.1", counterparty: "+19195551234",
            epochMs: 1718841600000, durationSeconds: 23, isRead: 0, transcript: "call me",
            mediaUrl: "https://www.google.com/voice/media/svm/acct/media-1");
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) });

        var result = await client.ListVoicemailsAsync(count: 20);

        Assert.Single(result.Items);
        Assert.Equal("vm.1", result.Items[0].MessageId);
        Assert.Equal("https://www.google.com/voice/media/svm/acct/media-1", result.Items[0].MediaId);
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

    [Fact]
    public async Task ListVoicemailsAsync_FullPage_LogsExactlyOneSaturationWarning()
    {
        // 100 threads for a requested count of 100 — the ceiling may be real and we cannot see past it.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(100)) });

        var result = await client.ListVoicemailsAsync(count: 100);

        Assert.True(result.Succeeded);
        var warning = Assert.Single(log.AtLevel(LogLevel.Warning));
        Assert.Contains("FULL page", warning.Message);
        Assert.Contains("100", warning.Message);
    }

    [Fact]
    public async Task ListVoicemailsAsync_UnderTheLimit_LogsNoSaturationWarning()
    {
        // A guard that fires on the happy path is noise, not a signal.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(99)) });

        await client.ListVoicemailsAsync(count: 100);

        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task ListVoicemailsAsync_MultiMessageThreads_DoesNotFalselyReportSaturation()
    {
        // ⚠ THE CORRECTNESS TRAP. 40 threads x 3 messages = 120 ITEMS but only 40 THREADS, against a
        // requested count of 100. An implementation testing items.Count warns here, wrongly — and would
        // also MISS real saturation whenever a full page happens to hold single-message threads.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(40, messagesPerThread: 3)) });

        var result = await client.ListVoicemailsAsync(count: 100);

        Assert.Equal(120, result.Items.Count);
        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task ListVoicemailsAsync_FullPageOfSparseThreads_StillReportsSaturation()
    {
        // The OTHER half of the same trap: a genuinely full page — 100 threads for a requested 100 —
        // that flattens to only 60 items because 40 threads carry no message. The page IS saturated and
        // older voicemail IS invisible, but an items.Count test sees 60 < 100 and stays silent, which is
        // the failure mode that actually costs us: real saturation, MISSED.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(SparseVoicemailThreads(100, threadsWithMessages: 60)) });

        var result = await client.ListVoicemailsAsync(count: 100);

        Assert.Equal(60, result.Items.Count);
        var warning = Assert.Single(log.AtLevel(LogLevel.Warning));
        Assert.Contains("FULL page", warning.Message);
    }

    [Fact]
    public async Task ListVoicemailsAsync_PollerPageSize_SaturatesAtItsOwnCount_Not100()
    {
        // GvThreadPoller asks for 50 (Services/GvThreadPoller.cs:129). Comparing against `count` rather
        // than a hardcoded 100 is what makes this case work with no extra code.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(50)) });

        await client.ListVoicemailsAsync(count: 50);

        Assert.Single(log.AtLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListVoicemailsAsync_NonPositiveCount_LogsNoSaturationWarning(int count)
    {
        // GvVoicemailController.GetList takes [FromQuery] int count = 20 with NO clamp, so ?count=0
        // reaches here — and `rawThreads >= 0` is true for every response, including an empty folder.
        // The result was the alarming, self-contradictory "FULL page: 0 threads for a requested count
        // of 0" on a completely idle box. A non-positive count expresses no ceiling, so there is no
        // ceiling to be near.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(0)) });

        await client.ListVoicemailsAsync(count: count);

        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task ListVoicemailsAsync_NonPositiveCountWithRealThreads_StillLogsNoSaturation()
    {
        // ...and it is the COUNT that is meaningless, not the threads. Threads came back; there is still
        // no requested ceiling for them to have reached.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(5)) });

        var result = await client.ListVoicemailsAsync(count: 0);

        Assert.Equal(5, result.Items.Count);
        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
