using RotaryPhoneController.Server.Auth;

namespace RotaryPhoneController.Server.Middleware;

/// <summary>
/// Gates every /api/gvbridge/* REST endpoint behind X-RotaryPhone-Auth when a key is configured
/// (ADR §6.5). Default-off: with no key, this is a pass-through and today's LAN behavior is unchanged.
/// EXCEPTION: /api/gvbridge/event stays open — it is the browser-extension content-script callback
/// (CORS-handled in Program.cs), not a RadioConsole consumer endpoint, and gating it would break the
/// extension. All other /api/gvbridge/* paths (status, adapter/mode, cookies, voicemail, sms, sms/send)
/// are gated uniformly — "one gate, applied consistently" (ADR §6.5).
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
        // Exempt ONLY the exact /api/gvbridge/event segment (and any sub-path of it), not a substring —
        // a Contains("/gvbridge/event") match would wrongly exempt a hypothetical future sibling like
        // /api/gvbridge/eventlog from the auth gate (review MEDIUM-1). Anchor to a segment boundary.
        var isExtensionEvent =
            path.Equals("/api/gvbridge/event", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/gvbridge/event/", StringComparison.OrdinalIgnoreCase);

        if (isGvBridge && !isExtensionEvent)
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
