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
}
