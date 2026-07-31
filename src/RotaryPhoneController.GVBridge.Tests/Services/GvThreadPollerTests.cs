using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvThreadPollerTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static (GvThreadPoller poller, List<SmsMessageDto> sms) NewPoller(
        Queue<HttpResponseMessage> responses)
    {
        var http = new HttpClient(new QueueHandler(responses));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var vmClient = new GvVoicemailClient(threadClient, parser, new StubFetcher(),
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig());
        var poller = new GvThreadPoller(smsClient, vmClient, config, NullLogger<GvThreadPoller>.Instance);

        var received = new List<SmsMessageDto>();
        poller.OnSmsReceived += dto => received.Add(dto);
        return (poller, received);
    }

    private static HttpResponseMessage SmsResponse(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage EmptyFolder() =>
        SmsResponse(GvWireBuilder.EmptyResponse());

    // Thread isRead=0 (UNREAD) with one inbound message m.1.
    private static string OneInbound() => GvWireBuilder.Response(
        GvWireBuilder.Thread("t.1", folder: 2, isRead: 0, "+19195551234",
            GvWireBuilder.Message("m.1", 1000, "+19195551234",
                GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "first")));

    // Same thread, with a second, newer inbound message m.2.
    private static string TwoInbound() => GvWireBuilder.Response(
        GvWireBuilder.Thread("t.1", folder: 2, isRead: 0, "+19195551234",
            GvWireBuilder.Message("m.1", 1000, "+19195551234",
                GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "first"),
            GvWireBuilder.Message("m.2", 2000, "+19195551234",
                GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "second")));

    // Same thread, now with an outbound reply m.3 (thread isRead=1 — the outbound reply cleared unread).
    private static string OutboundReply() => GvWireBuilder.Response(
        GvWireBuilder.Thread("t.1", folder: 2, isRead: 1, "+19195551234",
            GvWireBuilder.Message("m.1", 1000, "+19195551234",
                GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "first"),
            GvWireBuilder.Message("m.3", 3000, "+19195551234",
                GvWireBuilder.TypeSmsOutbound, isRead: 1, smsText: "me replying")));

    [Fact]
    public async Task FirstPoll_SeedsWithoutRaising()
    {
        // SMS folder poll + voicemail folder poll per cycle → enqueue both.
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        { SmsResponse(OneInbound()), EmptyFolder() }));

        await poller.PollOnceAsync(default);

        Assert.Empty(received); // history not pushed on first poll
    }

    [Fact]
    public async Task SecondPoll_RaisesOnlyNewInbound()
    {
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound()), EmptyFolder(), // seed
            SmsResponse(TwoInbound()), EmptyFolder()  // new m.2
        }));

        await poller.PollOnceAsync(default); // seed
        await poller.PollOnceAsync(default); // diff

        Assert.Single(received);
        Assert.Equal("m.2", received[0].Id);
        Assert.Equal("Inbound", received[0].Direction);
        Assert.Equal("second", received[0].Text);
    }

    [Fact]
    public async Task FailedFirstSmsPoll_DoesNotSeed_AndDoesNotFloodOnRecovery()
    {
        // Regression (review HIGH): the first SMS poll FAILS (401). The poller must NOT seed an empty
        // high-water mark — otherwise the first SUCCESSFUL poll would treat all history as "new" and
        // flood RadioConsole. After the failed poll, a successful poll carrying history must seed
        // silently (raise nothing); only a genuinely newer message on a later poll fires.
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            // cycle 1: SMS folder 401 (fail) + voicemail folder empty 200
            new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized),
            EmptyFolder(),
            // cycle 2: SMS folder now succeeds WITH history → must seed, not flood
            SmsResponse(OneInbound()),
            EmptyFolder(),
            // cycle 3: a genuinely newer inbound (m.2) → fires exactly once
            SmsResponse(TwoInbound()),
            EmptyFolder()
        }));

        await poller.PollOnceAsync(default); // failed SMS poll — no seed
        await poller.PollOnceAsync(default); // first success — seeds history silently
        Assert.Empty(received);              // NOT flooded with history

        await poller.PollOnceAsync(default); // m.2 is genuinely new
        Assert.Single(received);
        Assert.Equal("m.2", received[0].Id);
    }

    [Fact]
    public async Task OutboundMessage_DoesNotRaise()
    {
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound()), EmptyFolder(),
            SmsResponse(OutboundReply()), EmptyFolder()
        }));

        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);

        Assert.Empty(received); // outbound (type=11) is not an inbound "received" event
    }

    [Fact]
    public async Task NewOutboundMessage_RaisesOnSmsSent_WithCorrelationId_NotOnSmsReceived()
    {
        // id-consistency (PR4 Task 1 Step 1c): a NEW outbound message must re-surface via OnSmsSent
        // (NOT OnSmsReceived) carrying the SAME csid: id the controller echo uses, so the UI collapses
        // the optimistic bubble. Inbound behavior is unchanged.
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound()), EmptyFolder(),
            SmsResponse(OutboundReply()), EmptyFolder()
        }));
        var sent = new List<SmsMessageDto>();
        poller.OnSmsSent += dto => sent.Add(dto);

        await poller.PollOnceAsync(default); // seed
        await poller.PollOnceAsync(default); // diff → m.3 outbound is new

        Assert.Empty(received);                                  // inbound channel untouched
        Assert.Single(sent);
        Assert.Equal("Outbound", sent[0].Direction);
        Assert.Equal("me replying", sent[0].Text);
        // id matches the shared formula for (threadId, text, epoch) → exact UI collapse.
        Assert.Equal(SmsCorrelationId.For("t.1", "me replying", 3000), sent[0].Id);
        Assert.StartsWith("csid:t.1:", sent[0].Id);
    }

    [Fact]
    public void NotifyReadStateChanged_RaisesOnReadStateChanged()
    {
        var (poller, _) = NewPoller(new Queue<HttpResponseMessage>());
        ReadStateChangedDto? captured = null;
        poller.OnReadStateChanged += d => captured = d;

        ((IGvReadStateSink)poller).NotifyReadStateChanged(
            new ReadStateChangedDto("Voicemail", "vm.1", "t.1", true, DateTime.UtcNow));

        Assert.NotNull(captured);
        Assert.Equal("vm.1", captured!.Id);
        Assert.True(captured.IsRead);
    }

    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1 }, "audio/mpeg"));
    }

    private sealed class QueueHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responses.Count > 0
                ? responses.Dequeue()
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                  { Content = new StringContent(GvWireBuilder.EmptyResponse()) });
    }
}
