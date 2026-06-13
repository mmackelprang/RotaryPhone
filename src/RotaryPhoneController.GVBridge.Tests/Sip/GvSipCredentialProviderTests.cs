using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Sip;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Sip;

/// <summary>
/// The credential fetch (sipregisterinfo/get) is where the real HTTP 401/403 stale-cookie
/// failure surfaces. It must throw a typed <see cref="GvAuthException"/> so the transport's
/// reconnect/register path can escalate to a cookie refresh instead of treating it as a
/// generic failure.
/// </summary>
public class GvSipCredentialProviderTests
{
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static GvSipCredentialProvider CreateProvider(HttpStatusCode status, string body = "")
    {
        var handler = new MockHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://clients6.google.com/") };
        var factory = new SingleClientFactory(client);
        var config = new GVBridgeConfig { GvApiKey = "test", GvPhoneNumber = "+15551234567" };
        return new GvSipCredentialProvider(factory, config, NullLogger<GvSipCredentialProvider>.Instance);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetCredentials_OnAuthStatus_ThrowsGvAuthException(HttpStatusCode status)
    {
        var provider = CreateProvider(status, "SESSION_COOKIE_INVALID");

        var ex = await Assert.ThrowsAsync<GvAuthException>(() => provider.GetCredentialsAsync());
        Assert.Equal((int)status, ex.StatusCode);
    }

    [Fact]
    public async Task GetCredentials_OnServerError_DoesNotThrowGvAuthException()
    {
        // A 500 is a transient/network-ish failure, NOT an auth failure — must not escalate.
        var provider = CreateProvider(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var result = await provider.GetCredentialsAsync();
            _ = result;
        });
        // The thrown type must not be GvAuthException.
        var thrown = await Record.ExceptionAsync(() => provider.GetCredentialsAsync());
        Assert.NotNull(thrown);
        Assert.IsNotType<GvAuthException>(thrown);
    }

    [Fact]
    public async Task GetCredentials_OnSuccess_ParsesTokens()
    {
        // Response: [[ts, expiryMs], null, null, ["sipIdentity","cryptoKey"]]
        var body = "[[0, 3600], null, null, [\"sip-identity\", \"crypto-key\"]]";
        var provider = CreateProvider(HttpStatusCode.OK, body);

        var creds = await provider.GetCredentialsAsync();

        Assert.Equal("sip-identity", creds.SipUsername);
        Assert.Equal("crypto-key", creds.BearerToken);
        Assert.Equal(3600, creds.ExpirySeconds);
    }
}
