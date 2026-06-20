using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Server.Auth;

namespace RotaryPhoneController.Server.Hubs;

/// <summary>
/// Gates SignalR hub connections behind X-RotaryPhone-Auth when a key is configured (ADR §6.5, §6.3).
/// Accepts the header (non-browser/.NET HubConnection — RadioConsole's case) OR an access_token query
/// param (browser WebSocket, which cannot set headers on the handshake). Default-off: with no key,
/// every connection is allowed (today's behavior). Denied connections are aborted at connect time, so
/// no SMS/voicemail/call event ever reaches an unauthenticated client.
/// </summary>
public class HubAuthFilter : IHubFilter
{
    public const string HeaderName = "X-RotaryPhone-Auth";
    private readonly InterServiceAuthValidator _validator;

    public HubAuthFilter(InterServiceAuthValidator validator) => _validator = validator;

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var http = context.Context.GetHttpContext();
        if (http is null || !IsConnectionAuthorized(_validator, http))
        {
            context.Context.Abort();
            return;
        }
        await next(context);
    }

    /// <summary>Pure decision: header OR access_token must match when the gate is enabled.</summary>
    public static bool IsConnectionAuthorized(InterServiceAuthValidator validator, HttpContext http)
    {
        if (!validator.IsEnabled) return true;
        var header = http.Request.Headers[HeaderName].ToString();
        if (!string.IsNullOrEmpty(header) && validator.IsAuthorized(header)) return true;
        var token = http.Request.Query["access_token"].ToString();
        return !string.IsNullOrEmpty(token) && validator.IsAuthorized(token);
    }
}
