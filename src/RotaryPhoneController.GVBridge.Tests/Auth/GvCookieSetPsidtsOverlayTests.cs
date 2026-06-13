using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

/// <summary>
/// Tests for overlaying refreshed rotating cookies (__Secure-1PSIDTS / __Secure-3PSIDTS)
/// onto the stored raw cookie header. Today the rotating cookies live ONLY inside
/// RawCookieHeader and are never updated; WithRefreshedPsidts splices fresh values in so
/// ToCookieHeader() stops replaying stale PSIDTS verbatim.
/// </summary>
public class GvCookieSetPsidtsOverlayTests
{
    private static GvCookieSet WithRawHeader(string raw) => new()
    {
        Sapisid = "SAP",
        Sid = "SID",
        Hsid = "HSID",
        Ssid = "SSID",
        Apisid = "APISID",
        Secure1Psid = "PSID1",
        Secure3Psid = "PSID3",
        RawCookieHeader = raw,
    };

    [Fact]
    public void WithRefreshedPsidts_ReplacesExistingTokens()
    {
        var original = WithRawHeader(
            "SAPISID=SAP; __Secure-1PSIDTS=OLD1; SID=SID; __Secure-3PSIDTS=OLD3; NID=abc");

        var updated = original.WithRefreshedPsidts("NEW1", "NEW3");

        var header = updated.ToCookieHeader();
        Assert.Contains("__Secure-1PSIDTS=NEW1", header, StringComparison.Ordinal);
        Assert.Contains("__Secure-3PSIDTS=NEW3", header, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD1", header, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD3", header, StringComparison.Ordinal);
        // Untouched cookies remain.
        Assert.Contains("SAPISID=SAP", header, StringComparison.Ordinal);
        Assert.Contains("NID=abc", header, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRefreshedPsidts_AppendsWhenMissing()
    {
        var original = WithRawHeader("SAPISID=SAP; SID=SID");

        var updated = original.WithRefreshedPsidts("NEW1", "NEW3");

        var header = updated.ToCookieHeader();
        Assert.Contains("__Secure-1PSIDTS=NEW1", header, StringComparison.Ordinal);
        Assert.Contains("__Secure-3PSIDTS=NEW3", header, StringComparison.Ordinal);
        Assert.Contains("SAPISID=SAP", header, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRefreshedPsidts_NullValues_LeaveHeaderUnchanged()
    {
        var original = WithRawHeader("SAPISID=SAP; __Secure-1PSIDTS=OLD1");

        var updated = original.WithRefreshedPsidts(null, null);

        Assert.Equal(original.ToCookieHeader(), updated.ToCookieHeader());
    }

    [Fact]
    public void WithRefreshedPsidts_OnlyOnePartner_UpdatesThatOne()
    {
        var original = WithRawHeader("SAPISID=SAP; __Secure-1PSIDTS=OLD1; __Secure-3PSIDTS=OLD3");

        var updated = original.WithRefreshedPsidts("NEW1", psidts3: null);

        var header = updated.ToCookieHeader();
        Assert.Contains("__Secure-1PSIDTS=NEW1", header, StringComparison.Ordinal);
        Assert.Contains("__Secure-3PSIDTS=OLD3", header, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRefreshedPsidts_BuildsRawHeaderFromFields_WhenNoRawPresent()
    {
        var noRaw = new GvCookieSet
        {
            Sapisid = "SAP",
            Sid = "SID",
            Hsid = "HSID",
            Ssid = "SSID",
            Apisid = "APISID",
            Secure1Psid = "PSID1",
            Secure3Psid = "PSID3",
            RawCookieHeader = null,
        };

        var updated = noRaw.WithRefreshedPsidts("NEW1", "NEW3");

        var header = updated.ToCookieHeader();
        Assert.Contains("__Secure-1PSIDTS=NEW1", header, StringComparison.Ordinal);
        Assert.Contains("__Secure-3PSIDTS=NEW3", header, StringComparison.Ordinal);
        Assert.Contains("SAPISID=SAP", header, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRefreshedPsidts_RoundTripsThroughSerialize()
    {
        var original = WithRawHeader("SAPISID=SAP; __Secure-1PSIDTS=OLD1");
        var updated = original.WithRefreshedPsidts("NEW1", "NEW3");

        var roundTripped = GvCookieSet.Deserialize(updated.Serialize());

        Assert.Equal(updated.ToCookieHeader(), roundTripped.ToCookieHeader());
        Assert.Equal("SAP", roundTripped.Sapisid);
    }
}
