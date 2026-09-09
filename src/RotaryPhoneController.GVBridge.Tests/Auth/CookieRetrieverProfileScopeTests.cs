using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class CookieRetrieverProfileScopeTests
{
    // The two command lines that actually run on `radio`, alongside each other.
    private const string RadioKioskChrome =
        "/usr/bin/google-chrome --user-data-dir=/home/mmack/.config/radio-kiosk-chrome --kiosk https://localhost:5173";
    private const string GvBridgeChrome =
        "/usr/bin/google-chrome --user-data-dir=/home/mmack/.config/gv-bridge-chrome --remote-debugging-port=9224 https://voice.google.com";

    [Fact]
    public void ItNeverMatchesRadioConsolesKioskChrome()
    {
        // ⛔ THIS IS THE WHOLE POINT OF THE CHANGE. The old code killed by PROCESS NAME, so this
        // command line was a match, and `gv-login` — the command an operator runs when the login is
        // already broken — would have taken out the other service's kiosk.
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            RadioKioskChrome, "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile"));

        // And it must not reach the GV bridge's own browser either: gv-login's launch branch owns a
        // DIFFERENT profile, and killing the bridge is how the session under repair gets destroyed.
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            GvBridgeChrome, "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile"));
    }

    [Fact]
    public void ItMatchesOurOwnProfile_InBothQuotedAndBareForms()
    {
        const string dir = "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile";
        Assert.True(CookieRetriever.IsOurDebugProfileProcess(
            $"/usr/bin/google-chrome --remote-debugging-port=9224 --user-data-dir={dir} --no-first-run", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess(
            $"/usr/bin/google-chrome --user-data-dir=\"{dir}\" --no-first-run", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess($"chrome --user-data-dir={dir}", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess($"chrome --user-data-dir={dir}/", dir));
    }

    [Fact]
    public void ItDoesNotMatchAProfileThatMerelySharesAPrefix()
    {
        const string dir = "/home/mmack/.config/gv-bridge-chrome";
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            $"chrome --user-data-dir={dir}-backup", dir));
    }

    [Theory]
    [InlineData(null, "/x")]
    [InlineData("chrome", null)]
    [InlineData("", "/x")]
    [InlineData("chrome", "")]
    public void EmptyInputsMatchNothing(string? cmd, string? dir)
        => Assert.False(CookieRetriever.IsOurDebugProfileProcess(cmd, dir));

    [Fact]
    public void ARootProfileDirMatchesNothing_RatherThanEverything()
    {
        // ⛔ "/" trims to the empty string. Under the old terminator-set logic that built the
        // form "--user-data-dir=" and matched EVERY browser on the box. This predicate decides
        // what gets KILLED, so its degenerate case must be "nothing", never "everything".
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(RadioKioskChrome, "/"));
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(GvBridgeChrome, "/"));
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(GvBridgeChrome, "///"));
    }

    [Fact]
    public void ASubdirectoryOfOurProfileIsNotOurProfile()
    {
        // The old code accepted '/' as a value terminator, so "<ours>/anything" matched <ours>.
        const string dir = "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile";
        Assert.False(CookieRetriever.IsOurDebugProfileProcess($"chrome --user-data-dir={dir}/other", dir));
    }

    [Fact]
    public void ATraversalOutOfOurProfileCannotReachTheKiosk()
    {
        // ⛔ The nastiest form the terminator-set logic allowed: a value that STARTS with our
        // directory, is accepted because the next character is '/', and actually resolves to
        // Radio Console's kiosk profile.
        const string dir = "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile";
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            $"/usr/bin/google-chrome --user-data-dir={dir}/../../../../.config/radio-kiosk-chrome", dir));
    }

    [Fact]
    public void ASecondUserDataDirFlagIsStillExamined()
    {
        // Chrome takes the last --user-data-dir wins; either way, a command line carrying ours
        // anywhere must match, and one carrying only someone else's must not.
        const string dir = "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile";
        Assert.True(CookieRetriever.IsOurDebugProfileProcess(
            $"chrome --user-data-dir=/tmp/other --user-data-dir={dir}", dir));
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            "chrome --user-data-dir=/tmp/other --user-data-dir=/home/mmack/.config/radio-kiosk-chrome", dir));
    }
}
