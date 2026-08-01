using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
/// Route-value decoding for thread ids (RadioConsole handoff §B1), plus the §B1.3 zero-message guard.
///
/// Kestrel leaves %2F encoded in a path segment (it must not let a client forge a segment boundary) but
/// decodes everything else. GV group thread ids are <c>g.Group Message.&lt;base64&gt;</c> and that
/// alphabet contains '/', so the action used to receive the literal
/// <c>g.Group Message.d5Mri%2FNrDUQgXNXNQehOfw</c>, match nothing in GvSmsClient's exact-string filter,
/// and answer 200 with an empty list. Reproduced live on 2026-07-31 in a verified-healthy window:
/// <c>t.32665 → messages=2</c>, the two group threads → <c>messages=0</c>.
///
/// The strings below are therefore EXACTLY what each action receives after Kestrel: the %20 already a
/// space, the %2F still encoded.
/// </summary>
public class GvSmsControllerThreadIdDecodeTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";

    // The real ids from the handoff's reproduction.
    private const string GroupThreadId = "g.Group Message.d5Mri/NrDUQgXNXNQehOfw";
    private const string GroupRouteValue = "g.Group Message.d5Mri%2FNrDUQgXNXNQehOfw";
    private const string ShortCodeThreadId = "t.32665";
    private const string PhoneThreadId = "t.+18019208129";
    private const string PhoneRouteValueEncodedPlus = "t.%2B18019208129";

    private const long Epoch = 1718841600000;

    /// <summary>
    /// One SMS folder containing all three id shapes, so a single fixture proves both that the group id
    /// now resolves AND that the ids which already worked are not collateral damage.
    /// </summary>
    private static HttpResponseMessage SmsFolder(int groupIsRead = 0) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(GvWireBuilder.Response(
            GvWireBuilder.Thread(ShortCodeThreadId, folder: 2, isRead: 0, counterparty: "32665",
                GvWireBuilder.Message("m.short", Epoch, "32665",
                    GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "code 123456")),
            GvWireBuilder.Thread(PhoneThreadId, folder: 2, isRead: 0, counterparty: "+18019208129",
                GvWireBuilder.Message("m.phone", Epoch + 1, "+18019208129",
                    GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "on my way")),
            GvWireBuilder.Thread(GroupThreadId, folder: 2, isRead: groupIsRead, counterparty: "+19195551234",
                GvWireBuilder.Message("m.group", Epoch + 2, "+19195551234",
                    GvWireBuilder.TypeSmsInbound, isRead: groupIsRead, smsText: "dinner at 7?"))))
    };

    /// <summary>
    /// The group thread is present in the folder list but carries NO messages — the other path that
    /// reaches 200-with-empty (§B1.3: messages outside the fetched window), used to exercise the guard.
    /// </summary>
    private static HttpResponseMessage SmsFolderGroupThreadHasNoMessages() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(GvWireBuilder.Response(
            GvWireBuilder.Thread(PhoneThreadId, folder: 2, isRead: 0, counterparty: "+18019208129",
                GvWireBuilder.Message("m.phone", Epoch, "+18019208129",
                    GvWireBuilder.TypeSmsInbound, isRead: 0, smsText: "on my way")),
            GvWireBuilder.Thread(GroupThreadId, folder: 2, isRead: 0, counterparty: "+19195551234")))
    };

    private static (GvSmsController Controller, CapturingLogger<GvSmsController> Log,
        List<ReadStateChangedDto> Events) NewController(
            Func<HttpRequestMessage, HttpResponseMessage> handler, bool enableMarkRead = true)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var log = new CapturingLogger<GvSmsController>();
        var config = Options.Create(new GVBridgeConfig
        {
            EnableMarkRead = enableMarkRead, AllowMarkUnread = false, EnableSmsSend = false
        });
        var controller = new GvSmsController(
            smsClient, new SmsSendRateLimiter(5, TimeSpan.FromSeconds(10)), new SmsThreadIdResolver(),
            new NoopOutboundSink(), readStateClient, new TestReadSink(events), config, log);
        controller.SetReadStateClientForTest(http);
        return (controller, log, events);
    }

    private static SmsThreadMessagesDto Messages(IActionResult result) =>
        Assert.IsType<SmsThreadMessagesDto>(Assert.IsType<OkObjectResult>(result).Value);

    // ---- GetThreadMessages: the defect ----

    [Fact]
    public async Task GetThreadMessages_GroupIdWithEncodedSlash_ResolvesMessages()
    {
        var (c, _, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages(GroupRouteValue, count: 50, default));

        // Before the decode this was a 200 with messages: [] — every group/MMS thread unreadable.
        Assert.Single(dto.Messages);
        Assert.Equal("m.group", dto.Messages[0].Id);
        Assert.Equal("dinner at 7?", dto.Messages[0].Text);
        // The response echoes the DECODED id, so the caller's thread list and this payload agree.
        Assert.Equal(GroupThreadId, dto.ThreadId);
        Assert.Equal(GroupThreadId, dto.Messages[0].ThreadId);
    }

    [Fact]
    public async Task GetThreadMessages_GroupIdAlreadyDecoded_StillResolves()
    {
        // Defensive: if the id ever arrives already decoded (a different host, or a client that does not
        // escape), the decode must be a no-op rather than a second failure mode.
        var (c, _, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages(GroupThreadId, count: 50, default));

        Assert.Single(dto.Messages);
        Assert.Equal("m.group", dto.Messages[0].Id);
    }

    // ---- GetThreadMessages: the ids that already worked must keep working ----

    [Fact]
    public async Task GetThreadMessages_ShortCodeId_StillResolves()
    {
        var (c, _, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages(ShortCodeThreadId, count: 50, default));

        Assert.Single(dto.Messages);
        Assert.Equal("m.short", dto.Messages[0].Id);
        Assert.Equal(ShortCodeThreadId, dto.ThreadId);
    }

    [Fact]
    public async Task GetThreadMessages_PhoneId_LiteralPlus_StillResolves()
    {
        // Uri.UnescapeDataString is NOT form decoding: a literal '+' stays a '+' and never becomes a space.
        var (c, _, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages(PhoneThreadId, count: 50, default));

        Assert.Single(dto.Messages);
        Assert.Equal("m.phone", dto.Messages[0].Id);
        Assert.Equal(PhoneThreadId, dto.ThreadId);
    }

    [Fact]
    public async Task GetThreadMessages_PhoneId_EncodedPlus_StillResolves()
    {
        // t.%2B18019208129 worked before this change (Kestrel decodes %2B) and must still work: both
        // spellings have to land on the same thread.
        var (c, _, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages(PhoneRouteValueEncodedPlus, count: 50, default));

        Assert.Single(dto.Messages);
        Assert.Equal("m.phone", dto.Messages[0].Id);
        Assert.Equal(PhoneThreadId, dto.ThreadId);
    }

    // ---- §B1.3 zero-message sanity guard ----

    [Fact]
    public async Task GetThreadMessages_ZeroMessages_LogsExactlyOneWarning()
    {
        var (c, log, _) = NewController(_ => SmsFolder());

        var dto = Messages(await c.GetThreadMessages("t.99999", count: 50, default));

        Assert.Empty(dto.Messages);
        var warning = Assert.Single(log.AtLevel(LogLevel.Warning));
        Assert.Contains("t.99999", warning.Message);
        Assert.Contains("0 messages", warning.Message);
        // One line, no exception: journald churn on this box correlates with audio distortion.
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task GetThreadMessages_WithMessages_LogsNoWarning()
    {
        // A guard that fires on the happy path is noise, not a signal.
        var (c, log, _) = NewController(_ => SmsFolder());

        await c.GetThreadMessages(GroupRouteValue, count: 50, default);

        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task GetThreadMessages_UpstreamFailure_DoesNotLogTheZeroMessageWarning()
    {
        // A 502 is already honest; the guard exists for the 200-with-empty case only.
        var (c, log, _) = NewController(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await c.GetThreadMessages(GroupRouteValue, count: 50, default);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    // ---- MarkThreadRead: same id, same route shape, same defect ----

    [Fact]
    public async Task MarkThreadRead_GroupIdWithEncodedSlash_ResolvesThreadAndBroadcastsDecodedId()
    {
        var posts = 0;
        var (c, _, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return SmsFolder();
        });

        var result = await c.MarkThreadRead(GroupRouteValue, new MarkReadRequest(true), default);

        // Before the decode the step-2 lookup missed and this was a 404 (or a mark of nothing).
        var dto = Assert.IsType<SmsThreadDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(GroupThreadId, dto.ThreadId);
        Assert.False(dto.HasUnread);
        Assert.Equal(1, posts);                              // one per message id — the group's m.group
        var broadcast = Assert.Single(events);
        Assert.Equal(GroupThreadId, broadcast.ThreadId);     // decoded, so RadioConsole can match it
        Assert.True(broadcast.IsRead);
    }

    [Fact]
    public async Task MarkThreadRead_PhoneId_EncodedPlus_StillResolves()
    {
        var (c, _, events) = NewController(_ => SmsFolder());

        var result = await c.MarkThreadRead(PhoneRouteValueEncodedPlus, new MarkReadRequest(true), default);

        var dto = Assert.IsType<SmsThreadDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(PhoneThreadId, dto.ThreadId);
        Assert.Equal(PhoneThreadId, Assert.Single(events).ThreadId);
    }

    [Fact]
    public async Task MarkThreadRead_PhoneId_LiteralPlus_StillResolves()
    {
        var (c, _, events) = NewController(_ => SmsFolder());

        var result = await c.MarkThreadRead(PhoneThreadId, new MarkReadRequest(true), default);

        var dto = Assert.IsType<SmsThreadDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(PhoneThreadId, dto.ThreadId);
        Assert.Equal(PhoneThreadId, Assert.Single(events).ThreadId);
    }

    [Fact]
    public async Task MarkThreadRead_ThreadPresentButZeroMessages_LogsWarning()
    {
        // The thread resolved at step 2, so this is the strongest form of the §B1.3 signal: we are about
        // to answer 200 having marked no individual message.
        var (c, log, _) = NewController(_ => SmsFolderGroupThreadHasNoMessages());

        var result = await c.MarkThreadRead(GroupRouteValue, new MarkReadRequest(true), default);

        Assert.IsType<OkObjectResult>(result);
        var warning = Assert.Single(log.AtLevel(LogLevel.Warning));
        Assert.Contains(GroupThreadId, warning.Message);
        Assert.Contains("0 messages", warning.Message);
    }

    [Fact]
    public async Task MarkThreadRead_WithMessages_LogsNoWarning()
    {
        var (c, log, _) = NewController(_ => SmsFolder());

        await c.MarkThreadRead(GroupRouteValue, new MarkReadRequest(true), default);

        Assert.Empty(log.AtLevel(LogLevel.Warning));
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
