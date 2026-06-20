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

public class GvSmsControllerSendTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";

    private static SendSmsResponse Body(IActionResult r) =>
        Assert.IsType<SendSmsResponse>((r as ObjectResult)!.Value);

    private static (GvSmsController c, List<SmsMessageDto> sent) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> handler, int maxSends = 3, bool enableSend = true)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var limiter = new SmsSendRateLimiter(maxSends, TimeSpan.FromSeconds(10));
        var resolver = new SmsThreadIdResolver();
        var sentSink = new List<SmsMessageDto>();
        var sink = new TestSink(sentSink);
        var config = Options.Create(new GVBridgeConfig { EnableSmsSend = enableSend });
        var controller = new GvSmsController(smsClient, limiter, resolver, sink, config,
            NullLogger<GvSmsController>.Instance);
        // Inject the same http as the "authenticated client" for the write path test seam:
        controller.SetSendClientForTest(http);
        return (controller, sentSink);
    }

    private static HttpResponseMessage Ok200() => new(HttpStatusCode.OK) { Content = new StringContent("[]") };
    private static HttpResponseMessage InvalidArg() =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent("INVALID_ARGUMENT") };

    [Fact]
    public async Task Send_NewConversation_NormalizesAndReturnsOutboundEcho_WithStableId()
    {
        var (controller, sent) = NewController(_ => Ok200());
        var result = await controller.Send(
            new SendSmsRequest("(919) 555-1234", "hi there", null), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<SendSmsResponse>(ok.Value);
        Assert.True(resp.Queued);
        Assert.Equal("queued", resp.Code);
        Assert.Equal("t.+19195551234", resp.ThreadId);
        Assert.NotNull(resp.Message);
        Assert.Equal("Outbound", resp.Message!.Direction);
        Assert.Equal("hi there", resp.Message.Text);
        Assert.StartsWith("csid:t.+19195551234:", resp.Message.Id);   // stable correlation id, not a guid
        Assert.Single(sent);                                          // broadcast over the sink
        Assert.Equal(resp.Message.Id, sent[0].Id);                    // echo and broadcast share the Id
    }

    [Fact]
    public async Task Send_HonorsClientCorrelationId()
    {
        var (controller, _) = NewController(_ => Ok200());
        var result = await controller.Send(
            new SendSmsRequest("9195551234", "hi", null, ClientCorrelationId: "ui-optimistic-42"), default);
        Assert.Equal("ui-optimistic-42", Body(result).Message!.Id);   // UI's optimistic id echoed back
    }

    [Fact]
    public async Task Send_FlagOff_Returns409_SendDisabled_NoSend()
    {
        var (controller, sent) = NewController(_ => Ok200(), enableSend: false);
        var result = await controller.Send(new SendSmsRequest("9195551234", "hi", null), default);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, status.StatusCode);
        Assert.Equal("send_disabled", Body(result).Code);
        Assert.Empty(sent);                          // NO GV call when dark
    }

    [Fact]
    public async Task Send_InvalidNumber_Returns400_invalid_number_NoSend()
    {
        var (controller, sent) = NewController(_ => Ok200());
        var result = await controller.Send(new SendSmsRequest("not-a-number", "hi", null), default);
        var status = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_number", Body(result).Code);
        Assert.Empty(sent);                          // never reached Google
    }

    [Fact]
    public async Task Send_EmptyText_Returns400_invalid_text()
    {
        var (controller, _) = NewController(_ => Ok200());
        var result = await controller.Send(new SendSmsRequest("9195551234", "   ", null), default);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("invalid_text", Body(result).Code);
    }

    [Fact]
    public async Task Send_OverRateLimit_Returns429_rate_limited()
    {
        var (controller, _) = NewController(_ => Ok200(), maxSends: 1);
        await controller.Send(new SendSmsRequest("9195551234", "one", null), default);
        var second = await controller.Send(new SendSmsRequest("9195551234", "two", null), default);
        var status = Assert.IsType<ObjectResult>(second);
        Assert.Equal(429, status.StatusCode);
        Assert.Equal("rate_limited", Body(second).Code);
    }

    [Fact]
    public async Task Send_GoogleInvalidArgument_Returns400_invalid_number_NoBroadcast()
    {
        // GV rejected the recipient AFTER we sent → still surfaces as invalid_number (taxonomy rule).
        var (controller, sent) = NewController(_ => InvalidArg());
        var result = await controller.Send(new SendSmsRequest("9195551234", "hi", null), default);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, status.StatusCode);
        Assert.Equal("invalid_number", Body(result).Code);
        Assert.Empty(sent);                          // no fake "sent" echo on failure (honest status)
    }

    [Fact]
    public async Task Send_GoogleOtherError_Returns502_upstream_error_NoBroadcast()
    {
        var (controller, sent) = NewController(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("bad") });
        var result = await controller.Send(new SendSmsRequest("9195551234", "hi", null), default);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        Assert.Equal("upstream_error", Body(result).Code);
        Assert.Empty(sent);
    }

    [Fact]
    public void CorrelationId_ControllerEcho_AndPollerSurface_AreEqual_ForSameInputs()
    {
        // id-consistency rule: the controller echo id and the poller-surfaced id MUST be byte-identical
        // for the same (threadId, text, epoch), or the UI can never collapse the optimistic bubble.
        const string threadId = "t.+19195551234";
        const string text = "hello world";
        const long epoch = 1_700_000_000_000L;
        var controllerId = GvSmsController.CorrelationId(threadId, text, epoch);
        var pollerId = SmsCorrelationId.For(threadId, text, epoch);
        Assert.Equal(controllerId, pollerId);
    }

    private sealed class TestSink(List<SmsMessageDto> captured) : IGvOutboundSmsSink
    {
        public void NotifySent(SmsMessageDto dto) => captured.Add(dto);
    }

    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
