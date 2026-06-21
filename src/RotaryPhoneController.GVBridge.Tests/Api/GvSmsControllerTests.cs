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

public class GvSmsControllerTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsController NewController(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var limiter = new SmsSendRateLimiter(5, TimeSpan.FromSeconds(10));
        var resolver = new SmsThreadIdResolver();
        var sink = new NoopSink();
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var config = Options.Create(new GVBridgeConfig());
        return new GvSmsController(smsClient, limiter, resolver, sink, readStateClient, new NoopReadSink(),
            config, NullLogger<GvSmsController>.Instance);
    }

    private sealed class NoopSink : IGvOutboundSmsSink
    {
        public void NotifySent(SmsMessageDto dto) { }
    }

    private sealed class NoopReadSink : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) { }
    }

    private static HttpResponseMessage Response() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task GetThreads_MapsToThreadDtos()
    {
        var controller = NewController(_ => Response());
        var result = await controller.GetThreads(count: 20, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadListDto>(ok.Value);
        Assert.Single(dto.Threads);
        Assert.Equal("t.+19195551234", dto.Threads[0].ThreadId);
        Assert.Equal("Alice", dto.Threads[0].CounterpartyName);
        Assert.True(dto.Threads[0].HasUnread);
    }

    [Fact]
    public async Task GetThreadMessages_MapsToMessageDtos()
    {
        var controller = NewController(_ => Response());
        var result = await controller.GetThreadMessages("t.+19195551234", count: 50, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadMessagesDto>(ok.Value);
        Assert.Single(dto.Messages);
        Assert.Equal("m.1", dto.Messages[0].Id);
        Assert.Equal("Inbound", dto.Messages[0].Direction);
    }

    [Fact]
    public async Task GetThreads_OnUpstreamFailure_Returns502NotEmpty200()
    {
        var controller = NewController(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await controller.GetThreads(count: 20, default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetThreadMessages_OnUpstreamFailure_Returns502NotEmpty200()
    {
        var controller = NewController(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await controller.GetThreadMessages("t.+19195551234", count: 50, default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
