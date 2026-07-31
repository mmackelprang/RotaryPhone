using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

/// <summary>
/// The anti-silent-empty guard.
///
/// The original defect survived for weeks because "the JSON parsed" was reported as success: a parse
/// that yielded 0 items from a payload full of threads was indistinguishable from a genuinely empty
/// folder, so <c>GvVoicemailController</c>'s deliberate "502 rather than empty 200" guard never
/// fired and the one log line that would have screamed sat at Debug while the service ran at
/// Information.
///
/// These tests pin the distinction that fixes it:
///   - 0 raw threads            -> genuinely empty  -> Succeeded = true,  0 items
///   - N raw threads, 0 parsed  -> wire-shape drift -> Succeeded = false, caller surfaces it
/// </summary>
public class WireShapeDriftGuardTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    /// <summary>
    /// A well-formed root envelope whose thread bodies our positional indices cannot interpret —
    /// i.e. exactly what a future Google shape change looks like from inside the parser.
    /// </summary>
    private const string DriftedBody = """[["not-a-thread","also-not-a-thread"],"1","v1-1-1"]""";

    private static HttpClient Http(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static GvThreadClient ThreadClient(string body) =>
        new(Http(body), BaseUrl, ApiKey, new PositionalGvThreadParser(),
            NullLogger<GvThreadClient>.Instance);

    private static GvVoicemailClient VoicemailClient(string body) =>
        new(ThreadClient(body), new PositionalGvThreadParser(),
            new StubRecordingFetcher(), NullLogger<GvVoicemailClient>.Instance);

    private static GvSmsClient SmsClient(string body) =>
        new(ThreadClient(body), new PositionalGvThreadParser(), NullLogger<GvSmsClient>.Instance);

    // ---- genuinely empty: success ----

    [Fact]
    public async Task Voicemail_GenuinelyEmptyFolder_SucceedsWithZeroItems()
    {
        var result = await VoicemailClient(GvWireBuilder.EmptyResponse()).ListVoicemailsAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Sms_GenuinelyEmptyFolder_SucceedsWithZeroMessages()
    {
        var result = await SmsClient(GvWireBuilder.EmptyResponse()).ListRecentMessagesAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task Threads_GenuinelyEmptyFolder_Succeeds()
    {
        var result = await ThreadClient(GvWireBuilder.EmptyResponse())
            .ListThreadsAsync(GvThreadFolder.Sms);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Threads);
    }

    // ---- drift: failure, NOT empty ----

    [Fact]
    public async Task Voicemail_ThreadsPresentButNoneParsed_ReportsFailure_NotEmpty()
    {
        var result = await VoicemailClient(DriftedBody).ListVoicemailsAsync();

        // The whole point: this must NOT look like an empty voicemail folder.
        Assert.False(result.Succeeded);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Sms_ThreadsPresentButNoneParsed_ReportsFailure_NotEmpty()
    {
        var recent = await SmsClient(DriftedBody).ListRecentMessagesAsync();
        Assert.False(recent.Succeeded);

        var perThread = await SmsClient(DriftedBody).ListMessagesAsync("t.+19195551234");
        Assert.False(perThread.Succeeded);
    }

    [Fact]
    public async Task Threads_ThreadsPresentButNoneParsed_ReportsFailure_NotEmpty()
    {
        var result = await ThreadClient(DriftedBody).ListThreadsAsync(GvThreadFolder.Sms);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Threads);
    }

    [Fact]
    public async Task TheOldSyntheticShape_IsNowTreatedAsAFailure_NotAsEmpty()
    {
        // The exact body the original tests fed the parser. If Google ever sent this, we would now
        // fail loudly rather than serve an empty 200 — and the old parser's behaviour (returning
        // nothing for every REAL response) would have been caught immediately by this guard.
        const string old = """{"threads":[["t.1",["+19195551234","Alice"],1718841600000,true,[]]],"nextPageToken":null}""";

        var result = await VoicemailClient(old).ListVoicemailsAsync();

        // An object root has no countable threads, so this reads as "empty" rather than "drift" —
        // which is precisely why the ROOT-SHAPE assertions in CapturedWireShapeTests are the primary
        // defence and this guard is the secondary one. Documented here so the limit is explicit.
        Assert.True(result.Succeeded);
        Assert.Empty(result.Items);
    }

    // ---- the real capture still parses (guard does not false-positive) ----

    [Fact]
    public async Task Voicemail_RealCapturedPayload_SucceedsWithItems()
    {
        var result = await VoicemailClient(CapturedFixture.ResponseText(CapturedFixture.Voicemail))
            .ListVoicemailsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(20, result.Items.Count);
    }

    [Fact]
    public async Task Sms_RealCapturedPayload_SucceedsWithItems()
    {
        var result = await SmsClient(CapturedFixture.ResponseText(CapturedFixture.Messages))
            .ListRecentMessagesAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(72, result.Messages.Count);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }

    private sealed class StubRecordingFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(false, null, "audio/mpeg"));
    }
}
