using System.Net;
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class GvHttpClientHandlerTests
{
    private static GvCookieJar TestCookies => new()
    {
        Sapisid = "SAP_TEST", Sid = "SID_TEST", Hsid = "HSID_TEST",
        Ssid = "SSID_TEST", Apisid = "API_TEST",
        Secure1Psid = "SEC1_TEST", Secure3Psid = "SEC3_TEST"
    };

    [Fact]
    public async Task SendAsync_InjectsAuthorizationHeader()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/voice/v1/voiceclient/account/get");

        Assert.NotNull(captured);
        Assert.StartsWith("SAPISIDHASH", captured!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task SendAsync_InjectsCookieHeader()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/test");

        Assert.Contains("SAPISID=SAP_TEST", captured!.Headers.GetValues("Cookie").First());
    }

    [Fact]
    public async Task SendAsync_InjectsOriginAndReferer()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/test");

        Assert.Equal("https://voice.google.com", captured!.Headers.GetValues("Origin").First());
        Assert.Equal("https://voice.google.com/", captured.Headers.GetValues("Referer").First());
        Assert.Equal("0", captured.Headers.GetValues("X-Goog-AuthUser").First());
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
