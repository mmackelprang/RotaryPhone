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

public class GvVoicemailControllerTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmctl-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private GvVoicemailController NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler, byte[] audioBytes)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher(audioBytes);
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher,
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig { VoicemailCacheDir = _dir });
        var cache = new GvVoicemailCache(fetcher, config, NullLogger<GvVoicemailCache>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var controller = new GvVoicemailController(vmClient, cache, readStateClient, new NoopReadSink(),
            config, NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static HttpResponseMessage VmListResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task GetList_MapsNodesToDtosWithRelativeAudioUrl()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1, 2 });

        var result = await controller.GetList(count: 20, pageToken: null, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailListDto>(ok.Value);
        Assert.Single(dto.Items);
        Assert.Equal("vm.1", dto.Items[0].Id);
        Assert.Equal("/api/gvbridge/voicemail/vm.1/audio", dto.Items[0].AudioUrl);
        Assert.Equal("call me", dto.Items[0].Transcript);
        Assert.Equal(23, dto.Items[0].DurationSeconds);
    }

    [Fact]
    public async Task GetList_OnUpstreamFailure_Returns502NotEmpty200()
    {
        // A non-200 from Google → ListVoicemailsAsync(Succeeded=false). The controller must surface
        // a 502, not a 200 with an empty list (which RadioConsole would read as "no voicemails").
        var controller = NewController(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new byte[] { 1 });
        var result = await controller.GetList(count: 20, pageToken: null, default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetItem_NotFound_Returns404()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1 });
        var result = await controller.GetItem("does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetAudio_ServesBytesAsAudioMpeg()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 7, 7, 7 });
        var result = await controller.GetAudio("vm.1", default);
        var file = Assert.IsAssignableFrom<PhysicalFileResult>(result);
        Assert.Equal("audio/mpeg", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task GetAudio_UnknownId_Returns404()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1 });
        var result = await controller.GetAudio("nope", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    private sealed class StubFetcher(byte[] bytes) : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, bytes, "audio/mpeg"));
    }

    private sealed class NoopReadSink : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) { }
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
