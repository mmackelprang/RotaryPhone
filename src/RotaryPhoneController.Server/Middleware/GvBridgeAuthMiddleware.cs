using RotaryPhoneController.Server.Auth;

namespace RotaryPhoneController.Server.Middleware;

/// <summary>
/// Gates every /api/gvbridge/* REST endpoint behind X-RotaryPhone-Auth when a key is configured
/// (ADR §6.5). Default-off: with no key, this is a pass-through and today's LAN behavior is unchanged.
///
/// There are NO exemptions. Every /api/gvbridge/* path — status, adapter/mode, cookies, voicemail,
/// sms, sms/send, mark-read — is gated uniformly: "one gate, applied consistently" (ADR §6.5).
///
/// ⚠ HISTORY (2026-09-09): /api/gvbridge/event used to be exempt, for a browser-extension
/// content-script callback. That relay was deleted by design in March 2026 (see the migration spec's
/// "What Gets Deleted": "Service worker HTTP relay for call events — no longer needed"), and no route
/// for it has existed since. The exemption outlived the endpoint, leaving a permanent hole in the
/// gate for a path that did not exist — so a route later added at /api/gvbridge/event would have been
/// born unauthenticated, silently. Do not reintroduce a carve-out here without a live endpoint that
/// genuinely cannot carry the header; GvBridgeAuthMiddlewareTests pins the uniformity.
/// </summary>
public class GvBridgeAuthMiddleware
{
    public const string HeaderName = "X-RotaryPhone-Auth";
    private readonly RequestDelegate _next;
    private readonly InterServiceAuthValidator _validator;

    public GvBridgeAuthMiddleware(RequestDelegate next, InterServiceAuthValidator validator)
    {
        _next = next;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_validator.IsEnabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isGvBridge = path.StartsWith("/api/gvbridge", StringComparison.OrdinalIgnoreCase);

        if (isGvBridge)
        {
            var header = context.Request.Headers[HeaderName].ToString();
            if (!_validator.IsAuthorized(string.IsNullOrEmpty(header) ? null : header))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"Missing or invalid X-RotaryPhone-Auth header"}""");
                return;
            }
        }

        await _next(context);
    }
}
