using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvSmsControllerMarkReadTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";

    // SMS-folder list fixture: one thread t.+19195551234 (hasUnread=true) + its message m.1. This is the
    // REAL positional fixture from GvSmsControllerTests so PositionalGvThreadParser parses it (thread +
    // one message m.1 on that thread).
    private static HttpResponseMessage SmsList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false]]]],
         "nextPageToken":null}
        """)
    };

    private (GvSmsController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler,
        bool enableMarkRead = true, bool allowUnread = false)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var limiter = new SmsSendRateLimiter(5, TimeSpan.FromSeconds(10));
        var resolver = new SmsThreadIdResolver();
        var outboundSink = new NoopOutboundSink();
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var readSink = new TestReadSink(events);
        var config = Options.Create(new GVBridgeConfig
        {
            EnableMarkRead = enableMarkRead, AllowMarkUnread = allowUnread, EnableSmsSend = false
        });
        var controller = new GvSmsController(smsClient, limiter, resolver, outboundSink,
            readStateClient, readSink, config, NullLogger<GvSmsController>.Instance);
        controller.SetReadStateClientForTest(http);
        return (controller, events);
    }

    [Fact]
    public async Task MarkThreadRead_FlagOff_Returns409_markread_disabled_NoGvCall()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return SmsList();
        }, enableMarkRead: false);
        var result = await c.MarkThreadRead("t.+19195551234", new MarkReadRequest(true), default);
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, posts);
        Assert.Empty(events);
    }

    [Fact]
    public async Task MarkThreadRead_AppliesAndReturnsThreadDto_HasUnreadFalse_AndBroadcasts()
    {
        var (c, events) = NewController(_ => SmsList());
        var result = await c.MarkThreadRead("t.+19195551234", new MarkReadRequest(true), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadDto>(ok.Value);
        Assert.Equal("t.+19195551234", dto.ThreadId);
        Assert.False(dto.HasUnread);                      // authoritative: thread fully read
        Assert.Single(events);
        Assert.Equal("Sms", events[0].Kind);
        Assert.Equal("t.+19195551234", events[0].ThreadId);
        Assert.True(events[0].IsRead);                    // "thread fully read" == !hasUnread
    }

    [Fact]
    public async Task MarkThreadRead_UnknownThread_Returns404_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return SmsList();
        });
        var result = await c.MarkThreadRead("t.nonexistent", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task MarkThreadRead_Unread_WhenDisallowed_Returns400_unread_unsupported()
    {
        var (c, _) = NewController(_ => SmsList(), allowUnread: false);
        var result = await c.MarkThreadRead("t.+19195551234", new MarkReadRequest(false), default);
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);  // BadRequestObjectResult : ObjectResult
    }

    [Fact]
    public async Task MarkThreadRead_UpstreamFailure_Returns502_NoBroadcast()
    {
        var (c, events) = NewController(req =>
            req.RequestUri!.ToString().Contains("updateread")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : SmsList());
        var result = await c.MarkThreadRead("t.+19195551234", new MarkReadRequest(true), default);
        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(events);
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> captured) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => captured.Add(dto);
    }
    private sealed class NoopOutboundSink : IGvOutboundSmsSink
    {
        public void NotifySent(SmsMessageDto dto) { }
    }
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
