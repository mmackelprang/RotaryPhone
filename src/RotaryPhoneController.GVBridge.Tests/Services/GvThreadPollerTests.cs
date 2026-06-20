using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
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

    private const string OneInbound = """
        {"threads":[["t.1",["+19195551234","Alice"],1000,true,
          [["m.1","t.1",0,"+19195551234","first",1000,false]]]],"nextPageToken":null}
        """;
    private const string TwoInbound = """
        {"threads":[["t.1",["+19195551234","Alice"],2000,true,
          [["m.1","t.1",0,"+19195551234","first",1000,false],
           ["m.2","t.1",0,"+19195551234","second",2000,false]]]],"nextPageToken":null}
        """;

    [Fact]
    public async Task FirstPoll_SeedsWithoutRaising()
    {
        // SMS folder poll + voicemail folder poll per cycle → enqueue both.
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        { SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}""") }));

        await poller.PollOnceAsync(default);

        Assert.Empty(received); // history not pushed on first poll
    }

    [Fact]
    public async Task SecondPoll_RaisesOnlyNewInbound()
    {
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}"""), // seed
            SmsResponse(TwoInbound), SmsResponse("""{"threads":[],"nextPageToken":null}""")  // new m.2
        }));

        await poller.PollOnceAsync(default); // seed
        await poller.PollOnceAsync(default); // diff

        Assert.Single(received);
        Assert.Equal("m.2", received[0].Id);
        Assert.Equal("Inbound", received[0].Direction);
        Assert.Equal("second", received[0].Text);
    }

    [Fact]
    public async Task OutboundMessage_DoesNotRaise()
    {
        const string outbound = """
            {"threads":[["t.1",["+19195551234","Alice"],3000,false,
              [["m.1","t.1",0,"+19195551234","first",1000,false],
               ["m.3","t.1",1,"+19195551234","me replying",3000,true]]]],"nextPageToken":null}
            """;
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}"""),
            SmsResponse(outbound), SmsResponse("""{"threads":[],"nextPageToken":null}""")
        }));

        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);

        Assert.Empty(received); // outbound (direction=1) is not an inbound "received" event
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
                  { Content = new StringContent("""{"threads":[],"nextPageToken":null}""") });
    }
}
