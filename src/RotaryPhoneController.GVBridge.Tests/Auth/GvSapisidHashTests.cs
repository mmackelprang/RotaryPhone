using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class GvSapisidHashTests
{
    [Fact]
    public void Compute_ReturnsTimestampUnderscoreHexSha1()
    {
        var result = GvSapisidHash.Compute(
            sapisid: "ABCDEF1234567890",
            origin: "https://voice.google.com",
            timestampSeconds: 1711500000);

        Assert.StartsWith("1711500000_", result);
        Assert.Matches(@"^\d+_[0-9a-f]{40}$", result);
    }

    [Fact]
    public void Compute_DifferentTimestamps_ProduceDifferentHashes()
    {
        var a = GvSapisidHash.Compute("SAPISID", "https://voice.google.com", 1000);
        var b = GvSapisidHash.Compute("SAPISID", "https://voice.google.com", 2000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCurrent_UsesCurrentTimestamp()
    {
        var result = GvSapisidHash.ComputeCurrent("SAPISID", "https://voice.google.com");
        Assert.Matches(@"^\d+_[0-9a-f]{40}$", result);
        var ts = long.Parse(result.Split('_')[0]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(ts, now - 5, now + 5);
    }
}
