using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

/// <summary>
/// XR-6 (RadioConsole cross-repo handoff, PHN-1c §5 item 2). GetAudio resolved a recording through
/// FindNodeAsync, which called ListVoicemailsAsync and ignored its Succeeded flag. A failed
/// authenticated list returns Empty(succeeded: false), so FirstOrDefault yielded null and the route
/// answered 404 "has no recording" for a recording that exists. RadioConsole maps 404 from this
/// route to "retrying will not help", so a guest was told a voicemail was permanently gone.
///
/// The distinction under test: 502 means "we could not look", 404 means "we looked and it is not
/// there". Each route is covered by a PAIR — the failure case and the genuine-miss case — because a
/// fix that turned every miss into a 502 would pass the failure half alone.
/// </summary>
public class GvVoicemailControllerAuthBlackoutTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmbo-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>A healthy list containing exactly one voicemail, vm.1 (isRead=0/UNREAD).</summary>
    private static HttpResponseMessage VmList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(GvWireBuilder.VoicemailResponse(
            threadId: "t.+19195551234", messageId: "vm.1", counterparty: "+19195551234",
            epochMs: 1718841600000, durationSeconds: 23, isRead: 0, transcript: "call me",
            mediaUrl: "https://www.google.com/voice/media/svm/acct/media-1"))
    };

    /// <summary>
    /// What a GV auth blackout looks like at our boundary: api2thread/list answers 401. After PR #72
    /// ListRawAsync recovers and replays once, but this controller-level test has no provider, so
    /// the retry is skipped and the list fails — exactly the state that remains when recovery itself
    /// fails on the box.
    /// </summary>
    private static HttpResponseMessage Blackout() =>
        new(HttpStatusCode.Unauthorized);

    private (GvVoicemailController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser,
            NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher();
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher,
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig
        {
            VoicemailCacheDir = _dir, EnableMarkRead = true, AllowMarkUnread = false
        });
        var cache = new GvVoicemailCache(fetcher, config, NullLogger<GvVoicemailCache>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var controller = new GvVoicemailController(vmClient, cache, readStateClient,
            new TestReadSink(events), config, NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.SetReadStateClientForTest(http);
        return (controller, events);
    }

    // ---- GetAudio: the route XR-6 names ------------------------------------------------

    [Fact]
    public async Task GetAudio_WhenListFails_Returns502_Not404()
    {
        var (c, _) = NewController(_ => Blackout());
        var result = await c.GetAudio("vm.1", default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetAudio_WhenListSucceedsButIdAbsent_Still404()
    {
        // The twin. A successful list that simply does not contain the id is a genuine miss and
        // must stay 404 — RadioConsole's "retrying will not help" is CORRECT here.
        var (c, _) = NewController(_ => VmList());
        var result = await c.GetAudio("vm.does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- GetItem: same helper, same defect ----------------------------------------------

    [Fact]
    public async Task GetItem_WhenListFails_Returns502_Not404()
    {
        var (c, _) = NewController(_ => Blackout());
        var result = await c.GetItem("vm.1", default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetItem_WhenListSucceedsButIdAbsent_Still404()
    {
        var (c, _) = NewController(_ => VmList());
        var result = await c.GetItem("vm.does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- MarkRead step 2 -----------------------------------------------------------------

    [Fact]
    public async Task MarkRead_WhenListFails_Returns502_AndNeverWrites()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return Blackout();
        });
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal(0, posts);      // 502 before any write is attempted
        Assert.Empty(events);        // and no broadcast on a failure
    }

    [Fact]
    public async Task MarkRead_WhenListSucceedsButIdAbsent_Still404()
    {
        var (c, _) = NewController(_ => VmList());
        var result = await c.MarkRead("vm.does-not-exist", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- MarkRead step 5: the deliberate exception ---------------------------------------

    [Fact]
    public async Task MarkRead_WhenReReadFailsAfterSuccessfulWrite_Still200_NotA502()
    {
        // Pins Task 5b. The list succeeds and the updateread POST succeeds; only the step-5 re-read
        // fails. The write really happened, so answering 502 would make RadioConsole reconcile away
        // a change that is real. Must stay 200 with IsRead reflecting the applied value.
        var listCalls = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread"))
                return new HttpResponseMessage(HttpStatusCode.OK);
            listCalls++;
            return listCalls >= 2 ? Blackout() : VmList();   // 1st list OK, re-read fails
        });

        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailItemDto>(ok.Value);
        Assert.True(dto.IsRead);          // the applied truth survives the failed re-read
        Assert.Equal("vm.1", dto.Id);     // fell back to the optimistic node, not to an empty DTO
        Assert.Single(events);            // the write happened, so the broadcast must fire
    }

    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1, 2, 3 }, "audio/mpeg"));
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> sink) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => sink.Add(dto);
    }

    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
