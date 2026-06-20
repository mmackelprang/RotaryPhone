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
}
