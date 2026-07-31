using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Tests.Support;

/// <summary>
/// Loads the live-captured GV api2thread/list fixtures (see Fixtures/README.md).
///
/// These are REAL Google responses with leaf values redacted to identical-length placeholders —
/// structure, nesting, array lengths and null-vs-present are all authentic. They are the only
/// independent evidence in this test suite of what Google actually sends, which is precisely why
/// they must never be hand-edited to make an assertion pass.
/// </summary>
public static class CapturedFixture
{
    public const string Voicemail = "voicemail";
    public const string Messages = "messages";
    public const string Calls = "calls";

    /// <summary>All three captured folders, for theory-driven "every folder" assertions.</summary>
    public static IEnumerable<object[]> AllFolders() =>
        [[Voicemail], [Messages], [Calls]];

    private static string Path(string relative) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "capture", relative);

    /// <summary>The redacted response body for a folder, as a detached JsonElement.</summary>
    public static JsonElement Response(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path($"{folder}.response.json")));
        return doc.RootElement.Clone();   // Clone so it outlives the JsonDocument.
    }

    /// <summary>The raw redacted response text (for feeding a fake HTTP handler).</summary>
    public static string ResponseText(string folder) =>
        File.ReadAllText(Path($"{folder}.response.json"));

    /// <summary>
    /// The VERBATIM request body Chrome sent for this folder. Not redacted — it contains no PII.
    /// This is the ground truth our built request body is compared against byte-for-byte.
    /// </summary>
    public static string RequestBody(string folder) =>
        File.ReadAllText(Path($"{folder}.request.json")).Trim();

    /// <summary>Raw thread nodes: root[0].</summary>
    public static IReadOnlyList<JsonElement> Threads(string folder) =>
        Response(folder)[0].EnumerateArray().ToList();

    /// <summary>Raw message nodes across all threads: root[0][*][2][*].</summary>
    public static IReadOnlyList<JsonElement> Messages_(string folder) =>
        Threads(folder).SelectMany(t => t[2].EnumerateArray()).ToList();
}
