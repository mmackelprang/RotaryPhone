using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvVoicemailControllerMarkReadTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmmr-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    // The list fixture (one voicemail vm.1, isRead=false). Identical shape to GvVoicemailControllerTests.
    private static HttpResponseMessage VmList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
         "nextPageToken":null}
        """)
    };

    // Same shape as VmList() but vm.1 already isRead=true (the 7th node element, VmIsReadIdx=6, flipped to
    // true) — used to exercise the idempotent already-read no-op path.
    private static HttpResponseMessage VmListRead() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,true,"call me","media-1"]]]],
         "nextPageToken":null}
        """)
    };

    private (GvVoicemailController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler,
        bool enableMarkRead = true, bool allowUnread = false)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher();
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher, NullLogger<GvVoicemailClient>.Instance);
        var cache = new GvVoicemailCache(fetcher,
            Options.Create(new GVBridgeConfig { VoicemailCacheDir = _dir }),
            NullLogger<GvVoicemailCache>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var sink = new TestReadSink(events);
        var config = Options.Create(new GVBridgeConfig
        {
            EnableMarkRead = enableMarkRead, AllowMarkUnread = allowUnread
        });
        var controller = new GvVoicemailController(vmClient, cache, readStateClient, sink, config,
            NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.SetReadStateClientForTest(http);   // test seam: explicit authenticated client
        return (controller, events);
    }

    [Fact]
    public async Task MarkRead_FlagOff_Returns409_markread_disabled_NoGvCall()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        }, enableMarkRead: false);
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.Equal(0, posts);                          // NO GV call when dark
        Assert.Empty(events);
    }

    [Fact]
    public async Task MarkRead_AppliesAndReturnsUpdatedDto_WithIsReadTrue_AndBroadcasts()
    {
        var (c, events) = NewController(_ => VmList());
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailItemDto>(ok.Value);
        Assert.Equal("vm.1", dto.Id);
        Assert.True(dto.IsRead);                          // authoritative: reflects the applied mark
        Assert.Single(events);                            // path-a ReadStateChanged fired
        Assert.Equal("Voicemail", events[0].Kind);
        Assert.Equal("vm.1", events[0].Id);
        Assert.True(events[0].IsRead);
    }

    [Fact]
    public async Task MarkRead_UnknownId_Returns404_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        });
        var result = await c.MarkRead("does-not-exist", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, posts);                          // 404 before any write
    }

    [Fact]
    public async Task MarkRead_Unread_WhenDisallowed_Returns400_unread_unsupported_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        }, allowUnread: false);
        var result = await c.MarkRead("vm.1", new MarkReadRequest(false), default);
        var obj = Assert.IsAssignableFrom<ObjectResult>(result);   // BadRequestObjectResult : ObjectResult
        Assert.Equal(400, obj.StatusCode);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task MarkRead_AlreadyRead_Returns200_NoGvCall_NoBroadcast()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmListRead();   // vm.1 already isRead=true
        });
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailItemDto>(ok.Value);
        Assert.True(dto.IsRead);
        Assert.Equal(0, posts);      // idempotent no-op: no updateread POST
        Assert.Empty(events);        // no broadcast on a no-op
    }

    [Fact]
    public async Task MarkRead_UpstreamFailure_Returns502_NoBroadcast()
    {
        // List succeeds (so the node is found) but the updateread POST fails → 502, no false success.
        var (c, events) = NewController(req =>
            req.RequestUri!.ToString().Contains("updateread")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : VmList());
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Empty(events);                            // no broadcast on failure (honest)
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> captured) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => captured.Add(dto);
    }
    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1 }, "audio/mpeg"));
    }
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
