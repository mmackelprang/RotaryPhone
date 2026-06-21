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

    [Fact] public async Task GateOn_GvBridgeEventPath_NotGated()  // extension content-script endpoint stays open
        => Assert.Equal(200, await Invoke("/api/gvbridge/event", header: null, configuredKey: "k"));

    [Fact] public async Task GateOn_GvBridgeEventSubPath_NotGated()  // a real sub-path of /event stays open
        => Assert.Equal(200, await Invoke("/api/gvbridge/event/status", header: null, configuredKey: "k"));

    // Regression (review MEDIUM-1): a sibling route whose name merely STARTS WITH "event" is NOT the
    // exempt content-script endpoint — it must still be gated. The old Contains("/gvbridge/event") match
    // would have wrongly exempted it; the segment-anchored check gates it.
    [Fact] public async Task GateOn_GvBridgeEventSiblingPath_IsGated_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/eventlog", header: null, configuredKey: "k"));

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
