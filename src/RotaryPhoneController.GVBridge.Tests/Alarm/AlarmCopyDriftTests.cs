using System.Text.RegularExpressions;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Alarm;

/// <summary>
/// The GV session alarm QUOTES the service's own alert wording rather than inventing new copy.
/// That means the same sentences exist twice, in two languages, with nothing connecting them.
/// This is the thing that connects them.
/// </summary>
/// <remarks>
/// ⛔ A quotation that has silently stopped matching its source is worse than a paraphrase: it
/// attributes words to the service that the service does not say, inside a message an operator will
/// act on at the worst possible moment.
/// <para>
/// ⚠ WHY THIS IS A C# TEST AND NOT THE MSBuild Exec THE PLAN SPECIFIED. The plan wired
/// deploy/tests/check-alarm-copy-drift.sh into the build behind
/// <c>Condition="'$([MSBuild]::IsOSPlatform(Linux))' == 'true'"</c>, reasoning that "CI and the box
/// both run Linux". Measured 2026-09-09: this repo has NO CI (no .github at all), the owner builds on
/// Windows, and the box runs a self-contained publish with no SDK. So that guard would have run
/// NOWHERE — a check that cannot fail, which is the first of the boundary doc's four neighbours and
/// exactly the failure class this arc exists to correct. The shell script is kept as the Linux
/// convenience copy; this test is the enforcement, because it runs wherever `dotnet test` runs.
/// </para>
/// </remarks>
public class AlarmCopyDriftTests
{
    /// <summary>
    /// Sentences that must appear in BOTH the shell alarm and the C# that emits them.
    /// Keep identical to the QUOTES heredoc in deploy/tests/check-alarm-copy-drift.sh.
    /// </summary>
    private static readonly string[] Quotes =
    [
        "Google refused it. The working on-disk set was NOT overwritten.",
        "ACTION: re-login at voice.google.com.",
        "CHROME WAS UNREACHABLE on CDP port",
        "so the Google login was never tested.",
        "the browser was NEVER CONSULTED",
    ];

    [Fact]
    public void TheAlarmScriptQuotesTheServiceVerbatim_AndBothCopiesStillAgree()
    {
        var root = RepoRoot();
        var shellPath = Path.Combine(root, "deploy", "gv-session-alarm.sh");
        var csharpPath = Path.Combine(root, "src", "RotaryPhoneController.GVBridge",
                                      "Adapters", "GVApiAdapter.cs");

        // ⛔ Test for PRESENCE, not absence. If either file cannot be found the guard must FAIL,
        // not quietly pass — a check that goes silent when its subject is missing is the defect
        // this repo corrected in its deploy gate on the same day this was written.
        Assert.True(File.Exists(shellPath), $"alarm script not found at {shellPath}");
        Assert.True(File.Exists(csharpPath), $"adapter not found at {csharpPath}");

        var shell = Flatten(File.ReadAllText(shellPath));
        // Strip C# string-concatenation seams (`" + "`) so a sentence split across source lines
        // still reads as one sentence. The shell has no such seams.
        var csharp = Regex.Replace(Flatten(File.ReadAllText(csharpPath)), "\" *\\+ *\"", "");

        var missing = new List<string>();
        foreach (var q in Quotes)
        {
            if (!shell.Contains(q, StringComparison.Ordinal))
                missing.Add($"MISSING FROM deploy/gv-session-alarm.sh: {q}");
            if (!csharp.Contains(q, StringComparison.Ordinal))
                missing.Add($"MISSING FROM GVApiAdapter.cs: {q}");
        }

        Assert.True(missing.Count == 0,
            "The alarm's quotation of the service has drifted from the service's own wording. "
            + "Edit BOTH copies, or the alarm will attribute words to the service that it does not say:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>Collapse newlines and runs of spaces, so line wrapping is not a difference.</summary>
    private static string Flatten(string s)
        => Regex.Replace(s.Replace("\r", "").Replace("\n", ""), " +", " ");

    /// <summary>
    /// Walk up from the test assembly until the solution file appears. Not a fixed number of
    /// "..\\" segments, which silently breaks the moment the output path changes.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RotaryPhoneController.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
