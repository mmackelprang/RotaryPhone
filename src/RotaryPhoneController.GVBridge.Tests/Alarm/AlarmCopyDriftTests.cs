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

    /// <summary>
    /// What the alarm's auto-relogin track and the auto-relogin breaker must agree on. The alarm
    /// QUOTES the breaker's own reason text rather than composing one — which is the only reason
    /// "THIS SCRIPT DETECTS NOTHING" survives auto-relogin — so the words themselves cannot drift:
    /// they are read from the breaker's state file at run time. What CAN drift is everything the
    /// alarm assumes in order to find them: the field names, the two state words, and the command
    /// it tells a human to run.
    /// </summary>
    /// <remarks>
    /// ⚠ DELIBERATELY NOT THE PLAN'S LIST. docs/plans/gv-auto-relogin.md Task 14 proposed guarding
    /// four sentences from the actuator's reason texts. The alarm reproduces none of them statically
    /// (it quotes whatever the file says), so that guard would have pinned sentences to a file with
    /// nothing on the other side of the quotation — and three of the four live in actuator code that
    /// is not written until after the attended spike. Renaming BREAKER_REASON_TEXT, by contrast,
    /// would leave the alarm posting "(the breaker recorded no reason text)" at the moment a human
    /// most needs the reason; renaming TRIPPED would mute the relogin track entirely. Those are
    /// the drifts guarded here.
    /// </remarks>
    [Fact]
    public void TheAlarmReadsTheBreakerFileInTheBreakersOwnVocabulary()
    {
        var root = RepoRoot();
        var alarmPath = Path.Combine(root, "deploy", "gv-session-alarm.sh");
        var breakerPath = Path.Combine(root, "deploy", "gv-auto-relogin-breaker.sh");

        // ⛔ PRESENCE, not absence — the same rule as the test above. If the breaker is ever
        // removed this guard must fail and be deleted deliberately, not pass by having nothing
        // to compare.
        Assert.True(File.Exists(alarmPath), $"alarm script not found at {alarmPath}");
        Assert.True(File.Exists(breakerPath), $"breaker not found at {breakerPath}");

        var alarm = File.ReadAllText(alarmPath);
        var breaker = File.ReadAllText(breakerPath);
        var missing = new List<string>();

        // 1. Every field the alarm reads is one the breaker writes, in the %q form the alarm's
        //    subshell-source parse depends on.
        foreach (var key in new[] { "BREAKER_STATE", "BREAKER_TRIPPED_AT", "BREAKER_REASON_TEXT" })
        {
            if (!alarm.Contains("$" + key, StringComparison.Ordinal))
                missing.Add($"the alarm no longer reads {key} — update this guard deliberately if that is intended");
            if (!breaker.Contains($"printf '{key}=%q\\n'", StringComparison.Ordinal))
                missing.Add($"the breaker no longer writes {key} with printf %q — the alarm reads it by name");
        }

        // 1b. Both find the file at the same default path. If either default moved alone,
        //     the alarm would read a file that never exists, call it "not installed", and
        //     every trip would be silent.
        //     Whole assignment lines, not the path alone: a path in a comment must not
        //     satisfy this, and the alarm's fall-back through the breaker's own override
        //     (GV_RELOGIN_STATE_FILE) is part of the link.
        const string alarmLine =
            "RELOGIN_STATE_FILE=\"${GV_ALARM_RELOGIN_STATE_FILE:-${GV_RELOGIN_STATE_FILE:-${HOME}/.local/state/gv-auto-relogin.state}}\"";
        const string breakerLine =
            "BREAKER_STATE_FILE=\"${GV_RELOGIN_STATE_FILE:-${HOME}/.local/state/gv-auto-relogin.state}\"";
        bool HasLine(string text, string line) =>
            text.Replace("\r", "").Split('\n').Any(l => l.Trim() == line);
        if (!HasLine(alarm, alarmLine))
            missing.Add($"the alarm no longer finds the breaker file with: {alarmLine}");
        if (!HasLine(breaker, breakerLine))
            missing.Add($"the breaker no longer places its state file with: {breakerLine} — the alarm reads it there");

        // 2. The two state words the alarm branches on are the breaker's own.
        foreach (var word in new[] { "TRIPPED", "ARMED" })
        {
            if (!alarm.Contains($"[ \"$relogin_state\" = \"{word}\" ]", StringComparison.Ordinal))
                missing.Add($"the alarm no longer branches on the state word {word}");
            if (!breaker.Contains($"BREAKER_STATE=\"{word}\"", StringComparison.Ordinal))
                missing.Add($"the breaker no longer assigns the state word {word} — the alarm compares against it");
        }

        // 3. The command the alarm tells a human to run is the one the breaker's own reason texts
        //    name, and the breaker still answers the flag.
        const string resetCommand = "gv-auto-relogin.sh --reset";
        if (!Flatten(alarm).Contains(resetCommand, StringComparison.Ordinal))
            missing.Add($"the alarm no longer names '{resetCommand}'");
        if (!Flatten(breaker).Contains(resetCommand, StringComparison.Ordinal))
            missing.Add($"the breaker's reason texts no longer name '{resetCommand}' — the alarm tells a human to run it");
        foreach (var flag in new[] { "--status)", "--reset)" })
            if (!breaker.Contains(flag, StringComparison.Ordinal))
                missing.Add($"the breaker no longer answers {flag.TrimEnd(')')} — the alarm's ACTION names it");

        Assert.True(missing.Count == 0,
            "The alarm's auto-relogin track and the breaker have drifted apart. The alarm would "
            + "quote nothing, branch on nothing, or send a human to a command that does not exist:"
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
