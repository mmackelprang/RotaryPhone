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

    // ------------- reading the rotating PSIDTS back off the wire, for the mint-time carry-forward

    private static GvCookieSet WithHeader(string raw) => new()
    {
        Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a", RawCookieHeader = raw,
    };

    [Theory]
    // The collision that matters: __Secure-1PSID is a DIFFERENT, long-lived cookie, and it is a strict
    // prefix of __Secure-1PSIDTS. Reading either must never return the other, in either order.
    [InlineData("__Secure-1PSID=long; __Secure-1PSIDTS=short", "__Secure-1PSID", "long")]
    [InlineData("__Secure-1PSID=long; __Secure-1PSIDTS=short", "__Secure-1PSIDTS", "short")]
    [InlineData("__Secure-1PSIDTS=short; __Secure-1PSID=long", "__Secure-1PSID", "long")]
    [InlineData("__Secure-1PSIDTS=short; __Secure-1PSID=long", "__Secure-1PSIDTS", "short")]
    [InlineData("SAPISID=s;__Secure-3PSIDTS=v3", "__Secure-3PSIDTS", "v3")]      // no space after ';'
    [InlineData("SAPISID=s", "__Secure-1PSIDTS", null)]                          // absent
    [InlineData("", "__Secure-1PSIDTS", null)]                                   // empty header
    public void ReadCookie_MatchesAtATokenBoundary(string header, string name, string? expected)
    {
        Assert.Equal(expected, GvCookieSet.ReadCookie(header, name));
    }

    [Fact]
    public void RotatingPsidts_ReadsFromTheRenderedHeader_NotTheTypedFields()
    {
        // The PSIDTS live ONLY inside RawCookieHeader. Secure1Psid is the long-lived PSID and must not
        // be mistaken for one.
        var set = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            Secure1Psid = "the-long-lived-psid",
            RawCookieHeader = "SAPISID=s; __Secure-1PSID=the-long-lived-psid; "
                            + "__Secure-1PSIDTS=v1; __Secure-3PSIDTS=v3",
        };

        Assert.Equal(("v1", "v3"), set.RotatingPsidts());

        // A set with no raw header carries no PSIDTS at all, however many typed fields it has.
        var typedOnly = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            Secure1Psid = "p1", Secure3Psid = "p3",
        };
        Assert.Equal((null, null), typedOnly.RotatingPsidts());
    }

    [Fact]
    public void CarriesTheSamePsidtsAs_IsTrueOnlyForByteIdenticalRotatingCookies()
    {
        var held = WithHeader("SAPISID=s; __Secure-1PSIDTS=v1; __Secure-3PSIDTS=v3");

        // Same values, different surrounding cookies and different order — still the same credential.
        Assert.True(held.CarriesTheSamePsidtsAs(
            WithHeader("NID=other; __Secure-3PSIDTS=v3; SAPISID=s; __Secure-1PSIDTS=v1")));

        // One value rotated: a different credential, minted at a time we cannot read.
        Assert.False(held.CarriesTheSamePsidtsAs(
            WithHeader("SAPISID=s; __Secure-1PSIDTS=v1-NEW; __Secure-3PSIDTS=v3")));

        // Partner missing entirely is also a difference, not a match.
        Assert.False(held.CarriesTheSamePsidtsAs(WithHeader("SAPISID=s; __Secure-1PSIDTS=v1")));
    }

    [Fact]
    public void CarriesTheSamePsidtsAs_IsFalseWhenNeitherSetHasAnyPsidts()
    {
        // ⚠ Vacuously "unchanged" must NOT count as a match: with no rotating credential present there
        // is nothing for a mint time to describe, and carrying one forward would be an invented fact.
        // Unknown then flows through to ComputeFirstRefreshDelayMs, which treats it as "refresh at the
        // floor" rather than "brand new" — the conservative direction, deliberately.
        var a = WithHeader("SAPISID=s; SID=sid");
        var b = WithHeader("SAPISID=s; SID=sid");

        Assert.False(a.CarriesTheSamePsidtsAs(b));
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
