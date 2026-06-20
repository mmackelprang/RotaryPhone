using RotaryPhoneController.Server.Auth;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Auth;

public class InterServiceAuthValidatorTests
{
    [Fact]
    public void EmptyConfiguredKey_GateDisabled_AlwaysAllows()
    {
        var v = new InterServiceAuthValidator(configuredKey: "");
        Assert.False(v.IsEnabled);
        Assert.True(v.IsAuthorized(null));        // no header, gate off → allow (today's LAN behavior)
        Assert.True(v.IsAuthorized("anything"));
    }

    [Fact]
    public void ConfiguredKey_CorrectHeader_Authorized()
    {
        var v = new InterServiceAuthValidator(configuredKey: "s3cret-key");
        Assert.True(v.IsEnabled);
        Assert.True(v.IsAuthorized("s3cret-key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("s3cret-ke")]      // prefix
    [InlineData("s3cret-key ")]    // trailing space
    [InlineData("S3CRET-KEY")]     // case-sensitive
    public void ConfiguredKey_MissingOrWrongHeader_NotAuthorized(string? header)
    {
        var v = new InterServiceAuthValidator(configuredKey: "s3cret-key");
        Assert.False(v.IsAuthorized(header));
    }
}
