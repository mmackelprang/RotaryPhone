using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

/// <summary>
/// Task 1 of docs/plans/gv-auth-first-refresh-anchor-and-cookie-lineage.md: the cookie lineage
/// timestamps travel WITH the cookies, so they survive SaveAsync -> restart -> LoadAsync. Without
/// that, a fresh process cannot know the age of the credential it inherited — which is the
/// 2026-09-08 outage.
/// </summary>
public class GvCookieSetLineageTests
{
    [Fact]
    public void WithRefreshedPsidts_PreservesEveryPersistedField_AndStampsTheMint()
    {
        var validatedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var mintedAt = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);
        var original = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            RawCookieHeader = "SAPISID=s; __Secure-1PSIDTS=old",
            BrowserSessionValidatedAtUtc = validatedAt,
        };

        var rotated = original.WithRefreshedPsidts("new1", "new3", mintedAt);

        Assert.Equal(mintedAt, rotated.PsidtsMintedAtUtc);
        Assert.Equal(validatedAt, rotated.BrowserSessionValidatedAtUtc);   // must NOT be dropped
        Assert.Contains("__Secure-1PSIDTS=new1", rotated.RawCookieHeader);
    }

    [Fact]
    public void WithRefreshedPsidts_WithNoExplicitMintTime_StampsNow()
    {
        // The production call site passes no mint time — a rotation mints "now" by definition. If
        // this ever stopped stamping, ComputeFirstRefreshDelayMs would see null forever and every
        // activation would rotate at the 5 s floor.
        var before = DateTime.UtcNow;
        var rotated = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
        }.WithRefreshedPsidts("new1", "new3");

        Assert.NotNull(rotated.PsidtsMintedAtUtc);
        Assert.InRange(rotated.PsidtsMintedAtUtc!.Value, before, DateTime.UtcNow);
    }

    [Fact]
    public void WithBrowserSessionValidatedAt_StampsTheBrowserSession_AndKeepsTheMint()
    {
        var minted = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);
        var validated = new DateTime(2026, 9, 8, 18, 30, 0, DateTimeKind.Utc);
        var original = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            RawCookieHeader = "SAPISID=s",
            PsidtsMintedAtUtc = minted,
        };

        var stamped = original.WithBrowserSessionValidatedAt(validated);

        Assert.Equal(validated, stamped.BrowserSessionValidatedAtUtc);
        Assert.Equal(minted, stamped.PsidtsMintedAtUtc);        // must NOT be dropped
        Assert.Equal("SAPISID=s", stamped.RawCookieHeader);
    }

    [Fact]
    public void CopyWith_CoversEveryProperty()
    {
        // TRIPWIRE. If this fails, a property was added to GvCookieSet and not added to CopyWith — in
        // which case it is silently reset on every rotation and no test of the rotation's OUTPUT would
        // notice. Update CopyWith, then update this list.
        var props = typeof(GvCookieSet)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "Apisid", "BrowserSessionValidatedAtUtc", "Hsid", "PsidtsMintedAtUtc", "RawCookieHeader",
            "Sapisid", "Secure1Psid", "Secure3Psid", "Sid", "Ssid",
        }, props);
    }

    [Fact]
    public void LineageTimestamps_RoundTripThroughSerializeAndDeserialize()
    {
        // The persistence guarantee at the type level: GvCookieStore is only Serialize -> AES -> file,
        // so if the timestamps survive this they survive a restart.
        var minted = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);
        var validated = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var original = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            PsidtsMintedAtUtc = minted, BrowserSessionValidatedAtUtc = validated,
        };

        var round = GvCookieSet.Deserialize(original.Serialize());

        Assert.Equal(minted, round.PsidtsMintedAtUtc);
        Assert.Equal(validated, round.BrowserSessionValidatedAtUtc);
    }

    [Fact]
    public void Deserialize_JsonWithoutTheNewFields_YieldsNulls_NotAnException()
    {
        // Backward compatibility at the type level. If System.Text.Json ever started rejecting unmapped
        // members, GvCookieStore.LoadAsync would swallow the JsonException into a null and the adapter
        // would go unavailable with no diagnostic. This pins that it does not.
        const string legacy =
            """{"Sapisid":"s","Sid":"sid","Hsid":"h","Ssid":"ss","Apisid":"a","RawCookieHeader":null}""";

        var parsed = GvCookieSet.Deserialize(legacy);

        Assert.Equal("s", parsed.Sapisid);
        Assert.Null(parsed.PsidtsMintedAtUtc);
        Assert.Null(parsed.BrowserSessionValidatedAtUtc);
    }
}
