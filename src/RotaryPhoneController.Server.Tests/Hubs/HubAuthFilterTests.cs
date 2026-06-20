using Microsoft.AspNetCore.Http;
using RotaryPhoneController.Server.Auth;
using RotaryPhoneController.Server.Hubs;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Hubs;

public class HubAuthFilterTests
{
    private static HttpContext Ctx(string? header, string? token)
    {
        var c = new DefaultHttpContext();
        if (header is not null) c.Request.Headers["X-RotaryPhone-Auth"] = header;
        if (token is not null) c.Request.QueryString = new QueryString($"?access_token={token}");
        return c;
    }

    [Fact] public void GateOff_AllowsAnyConnection()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator(""), Ctx(null, null)));

    [Fact] public void GateOn_HeaderMatch_Allows()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx("k", null)));

    [Fact] public void GateOn_AccessTokenMatch_Allows()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx(null, "k")));

    [Fact] public void GateOn_NoCredential_Denies()
        => Assert.False(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx(null, null)));

    [Fact] public void GateOn_WrongCredential_Denies()
        => Assert.False(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx("nope", "nope")));
}
