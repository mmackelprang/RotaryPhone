using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

/// <summary>
/// B2 §4.2: reactive refresh-and-retry on the api2thread READ path, and signal-but-never-replay on
/// the WRITE paths. The correctness boundary these tests lock in:
///  - only 401/403 triggers recovery (never 429, never 5xx, never a network fault);
///  - exactly ONE retry, and it RE-RESOLVES the HttpClient (rungs 1/2 dispose the old one);
///  - the `_http`-injected test-constructor path never retries (there is no provider to recover through);
///  - writes may signal recovery but MUST NOT replay the request (ADR §4.2 #4).
/// </summary>
public class GvThreadClientAuthRetryTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task ListRaw_On401_RecoversAndRetriesOnce_ReturnsData()
    {
        var handler = new SequenceHandler(
            _ => Unauthorized(),
            _ => Ok(GvWireBuilder.EmptyResponse()));
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = true };
        var client = NewProviderClient(provider);

        using var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.NotNull(doc);
        Assert.Equal(1, provider.RecoverCalls);
        Assert.Equal(2, handler.Attempts);

        // Both attempts reported their real outcome to the adapter's data-plane health (spec §4.3).
        Assert.Equal(1, provider.AuthFailureOutcomes);
        Assert.Equal(1, provider.SuccessOutcomes);
    }

    [Fact]
    public async Task ListRaw_On429_RecordsFailureButNotAuthFailure()
    {
        // Health must not report an auth blackout for a throttle or a server error.
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var provider = new FakeProvider(() => new HttpClient(handler));
        var client = NewProviderClient(provider);

        await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Equal(0, provider.AuthFailureOutcomes);
        Assert.Equal(0, provider.SuccessOutcomes);
    }

    [Fact]
    public async Task ListRaw_On401_WhenRecoveryFails_ReturnsNull_NoRetry()
    {
        var handler = new SequenceHandler(_ => Unauthorized());
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = false };
        var client = NewProviderClient(provider);

        var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Null(doc);
        Assert.Equal(1, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);   // no replay when recovery failed
    }

    [Fact]
    public async Task ListRaw_On429_DoesNotRecoverOrRetry()
    {
        // Throttling is FALSIFIED for this defect, and replaying into a 429 is exactly wrong.
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = true };
        var client = NewProviderClient(provider);

        var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Null(doc);
        Assert.Equal(0, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ListRaw_On500_DoesNotRecoverOrRetry()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = true };
        var client = NewProviderClient(provider);

        var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Null(doc);
        Assert.Equal(0, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ListRaw_On200_DoesNotRecover()
    {
        var handler = new SequenceHandler(_ => Ok(GvWireBuilder.EmptyResponse()));
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = true };
        var client = NewProviderClient(provider);

        using var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.NotNull(doc);
        Assert.Equal(0, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ListRaw_On200WithUnparseableBody_RecordsNoOutcome()
    {
        // A 200 carrying content that will not parse is NOT a data-plane success — ListRawAsync
        // returns null for it — and recording one would clear an authBlackout that nothing has
        // actually resolved. It is not an auth failure either, so it must record NOTHING.
        var handler = new SequenceHandler(_ => Ok("this is not json"));
        var provider = new FakeProvider(() => new HttpClient(handler));
        var client = NewProviderClient(provider);

        var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Null(doc);
        Assert.Equal(0, provider.SuccessOutcomes);
        Assert.Equal(0, provider.AuthFailureOutcomes);
        Assert.Equal(0, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);   // and no replay
    }

    [Fact]
    public async Task ListRaw_RetryReResolvesClient()
    {
        // Rungs 1 and 2 DISPOSE and re-create the adapter's HttpClient. A retry that reused the
        // captured instance would throw ObjectDisposedException, so each attempt must re-resolve.
        var first = new SequenceHandler(_ => Unauthorized());
        var second = new SequenceHandler(_ => Ok(GvWireBuilder.EmptyResponse()));
        var clients = new Queue<HttpClient>([new HttpClient(first), new HttpClient(second)]);
        var provider = new FakeProvider(() => clients.Dequeue()) { RecoverResult = true };
        var client = NewProviderClient(provider);

        using var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.NotNull(doc);
        Assert.Equal(2, provider.GetClientCalls);   // re-resolved, not captured
        Assert.Equal(1, first.Attempts);
        Assert.Equal(1, second.Attempts);           // the retry rode the POST-recovery client
    }

    [Fact]
    public async Task ListRaw_TestConstructorPath_NeverRetries()
    {
        // The _http-injected constructor has no provider to recover through, so every existing
        // GvThreadClientTests / GvSmsClientTests / GvVoicemailClientTests fixture is unaffected.
        var handler = new SequenceHandler(_ => Unauthorized());
        var client = new GvThreadClient(new HttpClient(handler), BaseUrl, ApiKey,
            new PositionalGvThreadParser(), NullLogger<GvThreadClient>.Instance);

        var doc = await client.ListRawAsync(GvThreadFolder.Sms, 20, pageToken: null);

        Assert.Null(doc);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task SendAsync_On401_SignalsRecovery_ButDoesNotReplay()
    {
        // ADR §4.2 #4: a replayed sendsms could double-send. Signal only.
        var handler = new SequenceHandler(_ => Unauthorized());
        var provider = new FakeProvider(() => new HttpClient(handler)) { RecoverResult = true };
        var threadClient = NewProviderClient(provider);
        var sms = new GvSmsClient(threadClient, new PositionalGvThreadParser(), provider,
            NullLogger<GvSmsClient>.Instance);

        var result = await sms.SendAsync(new HttpClient(handler), "t.+19195551234", "hello");

        Assert.False(result.Queued);
        Assert.Equal(1, provider.RecoverCalls);
        Assert.Equal(1, handler.Attempts);   // NEVER replayed
    }

    // ---------------------------------------------------------------- helpers

    private static GvThreadClient NewProviderClient(IGvAuthenticatedClientProvider provider)
        => new(provider, new PositionalGvThreadParser(), NullLogger<GvThreadClient>.Instance);

    private static HttpResponseMessage Unauthorized() => new(HttpStatusCode.Unauthorized)
    { Content = new StringContent("") };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body) };

    /// <summary>Serves one canned response per attempt (last one repeats) and counts attempts.</summary>
    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref _attempts) - 1;
            var pick = responses[Math.Min(index, responses.Length - 1)];
            return Task.FromResult(pick(request));
        }
    }

    private sealed class FakeProvider(Func<HttpClient?> clientFactory) : IGvAuthenticatedClientProvider
    {
        private int _getClientCalls;
        private int _recoverCalls;
        private int _successOutcomes;
        private int _authFailureOutcomes;

        public bool RecoverResult { get; set; } = true;
        public int GetClientCalls => Volatile.Read(ref _getClientCalls);
        public int RecoverCalls => Volatile.Read(ref _recoverCalls);
        public int SuccessOutcomes => Volatile.Read(ref _successOutcomes);
        public int AuthFailureOutcomes => Volatile.Read(ref _authFailureOutcomes);

        public HttpClient? GetAuthenticatedClient()
        {
            Interlocked.Increment(ref _getClientCalls);
            return clientFactory();
        }

        public string ApiBaseUrl => BaseUrl;
        public string ApiKey => GvThreadClientAuthRetryTests.ApiKey;

        public Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _recoverCalls);
            return Task.FromResult(RecoverResult);
        }

        public void RecordApiOutcome(bool success, bool authFailure)
        {
            if (success) Interlocked.Increment(ref _successOutcomes);
            else if (authFailure) Interlocked.Increment(ref _authFailureOutcomes);
        }
    }
}
