using System.Net.Http.Headers;

namespace RotaryPhoneController.GVBridge.Auth;

public class GvHttpClientHandler : DelegatingHandler
{
    private readonly Func<Task<GvCookieSet>> _getCookies;

    public GvHttpClientHandler(Func<Task<GvCookieSet>> getCookies, HttpMessageHandler inner)
        : base(inner)
    {
        _getCookies = getCookies;
    }

    public GvHttpClientHandler(Func<Task<GvCookieSet>> getCookies)
    {
        _getCookies = getCookies;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookies = await _getCookies();
        var hash = GvSapisidHash.ComputeCurrent(cookies.Sapisid);

        request.Headers.Authorization = new AuthenticationHeaderValue("SAPISIDHASH", hash);
        request.Headers.TryAddWithoutValidation("Cookie", cookies.ToCookieHeader());
        request.Headers.TryAddWithoutValidation("Origin", "https://voice.google.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://voice.google.com/");
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");

        return await base.SendAsync(request, cancellationToken);
    }
}
