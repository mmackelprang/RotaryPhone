using Microsoft.AspNetCore.Http;
using RotaryPhoneController.Server.Auth;
using RotaryPhoneController.Server.Middleware;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Middleware;

public class GvBridgeAuthMiddlewareTests
{
    private static async Task<int> Invoke(string path, string? header, string configuredKey)
    {
        var validator = new InterServiceAuthValidator(configuredKey);
        var nextCalled = false;
        var mw = new GvBridgeAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, validator);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (header is not null) ctx.Request.Headers["X-RotaryPhone-Auth"] = header;
        await mw.InvokeAsync(ctx);
        // Encode "passed through" as 200-from-next, else the status the middleware set.
        return nextCalled ? 200 : ctx.Response.StatusCode;
    }

    [Fact] public async Task GateOff_AllowsWithoutHeader()
        => Assert.Equal(200, await Invoke("/api/gvbridge/sms/threads", header: null, configuredKey: ""));

    [Fact] public async Task GateOn_CorrectHeader_PassesThrough()
        => Assert.Equal(200, await Invoke("/api/gvbridge/sms/send", "k", configuredKey: "k"));

    [Fact] public async Task GateOn_MissingHeader_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/cookies", header: null, configuredKey: "k"));

    [Fact] public async Task GateOn_WrongHeader_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/voicemail", "nope", configuredKey: "k"));

    [Fact] public async Task GateOn_NonGvBridgePath_NotGated()
        => Assert.Equal(200, await Invoke("/api/phone/status", header: null, configuredKey: "k"));

    // /api/gvbridge/event was exempt from the gate until 2026-09-09, for a browser-extension relay
    // that had been deleted by design in March 2026. The exemption outlived the endpoint and left a
    // permanent hole in the gate for a path with no route — so a route later added there would have
    // been born unauthenticated. These three pin that the hole is closed and stays closed: /event is
    // now gated exactly like any other /api/gvbridge/* path, with no special case.
    [Fact] public async Task GateOn_GvBridgeEventPath_IsGated_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/event", header: null, configuredKey: "k"));

    [Fact] public async Task GateOn_GvBridgeEventSubPath_IsGated_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/event/status", header: null, configuredKey: "k"));

    // A sibling whose name merely STARTS WITH "event". It was already gated (review MEDIUM-1 anchored
    // the old exemption to a segment boundary), and it stays gated now that no exemption exists at all.
    [Fact] public async Task GateOn_GvBridgeEventSiblingPath_IsGated_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/eventlog", header: null, configuredKey: "k"));

    // A valid header must still pass on the formerly-exempt path — proving it is genuinely gated
    // rather than hard-denied, which a blanket 401 would also satisfy.
    [Fact] public async Task GateOn_GvBridgeEventPath_WithValidHeader_200()
        => Assert.Equal(200, await Invoke("/api/gvbridge/event", header: "k", configuredKey: "k"));

    // The gate is default-off: with no key configured, the formerly-exempt path behaves like every
    // other one and is not gated. Pins that removing the carve-out did not change LAN-mode behaviour.
    [Fact] public async Task GateOff_GvBridgeEventPath_NotGated()
        => Assert.Equal(200, await Invoke("/api/gvbridge/event", header: null, configuredKey: ""));

    // Mark-read routes (ADR §6.2 Q8): no special auth posture — the PR5 prefix gate auto-covers them.
    // These prove a future middleware change can't silently un-gate the GV account-write routes.
    [Theory]
    [InlineData("/api/gvbridge/voicemail/vm.1/read")]
    [InlineData("/api/gvbridge/sms/threads/t.abc/read")]
    public async Task MarkReadRoutes_AreGated_WhenKeySet_NoHeader_Returns401(string path)
        => Assert.Equal(401, await Invoke(path, header: null, configuredKey: "k"));

    [Theory]
    [InlineData("/api/gvbridge/voicemail/vm.1/read")]
    [InlineData("/api/gvbridge/sms/threads/t.abc/read")]
    public async Task MarkReadRoutes_PassGate_WithValidHeader(string path)
        => Assert.Equal(200, await Invoke(path, header: "k", configuredKey: "k"));   // gate let it through to next()
}
