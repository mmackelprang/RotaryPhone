using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

/// <summary>
/// Tests the browser-less RotateCookies refresh seam. The EXACT request shape for the
/// voice.google.com origin is UNCONFIRMED (see TODO in GvCookieRotator), so these tests
/// pin the observable behavior: parse fresh PSIDTS from Set-Cookie on success; return a
/// clean "did not rotate" result on failure so the caller falls back to CDP. The rotator
/// must never throw out — failures are signalled via the result so the ladder can fall back.
/// </summary>
public class GvCookieRotatorTests
{
    private static GvCookieSet Cookies(string raw) => new()
    {
        Sapisid = "SAP",
        Sid = "SID",
        Hsid = "H",
        Ssid = "S",
        Apisid = "A",
        Secure1Psid = "PSID1",
        Secure3Psid = "PSID3",
        RawCookieHeader = raw,
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(fn(request));
    }

    private static GvCookieRotator Rotator(Func<HttpRequestMessage, HttpResponseMessage> fn)
    {
        var client = new HttpClient(new StubHandler(fn));
        return new GvCookieRotator(client, NullLogger<GvCookieRotator>.Instance);
    }

    [Fact]
    public async Task Rotate_OnSetCookie_ReturnsFreshPsidts()
    {
        var rotator = Rotator(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            };
            resp.Headers.TryAddWithoutValidation("Set-Cookie", "__Secure-1PSIDTS=FRESH1; Path=/; Secure");
            resp.Headers.TryAddWithoutValidation("Set-Cookie", "__Secure-3PSIDTS=FRESH3; Path=/; Secure");
            return resp;
        });

        var result = await rotator.RotateAsync(Cookies("SAPISID=SAP; __Secure-1PSID=PSID1; __Secure-1PSIDTS=OLD1"));

        Assert.True(result.Rotated);
        Assert.Equal("FRESH1", result.Psidts1);
        Assert.Equal("FRESH3", result.Psidts3);
    }

    [Fact]
    public async Task Rotate_On401_ReturnsNotRotated()
    {
        var rotator = Rotator(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await rotator.RotateAsync(Cookies("SAPISID=SAP; __Secure-1PSID=PSID1"));

        Assert.False(result.Rotated);
        Assert.Null(result.Psidts1);
    }

    [Fact]
    public async Task Rotate_OnNetworkError_DoesNotThrow_ReturnsNotRotated()
    {
        var rotator = Rotator(_ => throw new HttpRequestException("boom"));

        var result = await rotator.RotateAsync(Cookies("SAPISID=SAP; __Secure-1PSID=PSID1"));

        Assert.False(result.Rotated);
    }

    [Fact]
    public async Task Rotate_WhenNoSetCookieReturned_ReturnsNotRotated()
    {
        var rotator = Rotator(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]"),
        });

        var result = await rotator.RotateAsync(Cookies("SAPISID=SAP; __Secure-1PSID=PSID1"));

        Assert.False(result.Rotated);
    }
}
