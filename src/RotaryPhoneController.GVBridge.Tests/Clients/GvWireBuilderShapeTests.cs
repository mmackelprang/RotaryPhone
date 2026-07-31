using System.Text.Json;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

/// <summary>
/// Keeps <see cref="GvWireBuilder"/> honest against the live captures.
///
/// Scenario-specific tests build their JSON through the builder rather than by hand. That is only
/// safe if the builder emits the shape Google actually sends — otherwise it becomes a second,
/// centralized way to write self-confirming fixtures. These tests compare the builder's output to
/// the captured responses structurally: same root envelope, same thread layout, same message layout,
/// same types at the same positions.
/// </summary>
public class GvWireBuilderShapeTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonValueKind[] Kinds(JsonElement array) =>
        array.EnumerateArray().Select(e => e.ValueKind).ToArray();

    [Fact]
    public void BuiltRoot_HasTheSameEnvelopeAsACapturedResponse()
    {
        var built = Parse(GvWireBuilder.VoicemailResponse(
            "c.thread", "m.1", "+15551234567", 1700000000000, 20, isRead: 1, "hello there", "https://x/y"));
        var captured = CapturedFixture.Response(CapturedFixture.Voicemail);

        Assert.Equal(captured.ValueKind, built.ValueKind);
        Assert.Equal(captured.GetArrayLength(), built.GetArrayLength());
        Assert.Equal(Kinds(captured), Kinds(built));
    }

    [Fact]
    public void BuiltVoicemailThread_HasTheSameLayoutAsACapturedVoicemailThread()
    {
        var built = Parse(GvWireBuilder.VoicemailResponse(
            "c.thread", "m.1", "+15551234567", 1700000000000, 20, isRead: 1, "hello there", "https://x/y"))[0][0];
        var captured = CapturedFixture.Threads(CapturedFixture.Voicemail)[0];

        Assert.Equal(captured.GetArrayLength(), built.GetArrayLength());
        Assert.Equal(Kinds(captured), Kinds(built));
    }

    [Fact]
    public void BuiltVoicemailMessage_HasTheSameLayoutAsACapturedVoicemailMessage()
    {
        var built = Parse(GvWireBuilder.VoicemailResponse(
            "c.thread", "m.1", "+15551234567", 1700000000000, 20, isRead: 1, "hello there", "https://x/y"))[0][0][2][0];

        // Compare against a captured voicemail that HAS a transcript, so index 6 is non-null on both.
        var captured = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .First(m => m[6].ValueKind != JsonValueKind.Null);

        Assert.Equal(captured.GetArrayLength(), built.GetArrayLength());
        Assert.Equal(Kinds(captured), Kinds(built));
    }

    [Fact]
    public void BuiltSmsMessage_HasTheSameLayoutAsACapturedSmsMessage()
    {
        var built = Parse(GvWireBuilder.SmsResponse(
            "t.+15551234567", "m.1", "+15551234567", 1700000000000, "hi", isRead: 1))[0][0][2][0];
        var captured = CapturedFixture.Messages_(CapturedFixture.Messages)
            .First(m => m[14].ValueKind == JsonValueKind.Null && m[17].ValueKind == JsonValueKind.Null);

        Assert.Equal(captured.GetArrayLength(), built.GetArrayLength());
        Assert.Equal(Kinds(captured), Kinds(built));
    }

    [Fact]
    public void BuiltPendingTranscript_MatchesACapturedPendingVoicemail()
    {
        var built = Parse(GvWireBuilder.VoicemailResponse(
            "c.thread", "m.1", "+15551234567", 1700000000000, 20, isRead: 0, transcript: null, "https://x/y"))[0][0][2][0];
        var captured = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .First(m => m[6].ValueKind == JsonValueKind.Null);

        Assert.Equal(Kinds(captured), Kinds(built));
    }

    [Fact]
    public void BuiltTranscript_HasTheCapturedConfidencePlusWordsShape()
    {
        var built = Parse(GvWireBuilder.VoicemailResponse(
            "c.thread", "m.1", "+15551234567", 1700000000000, 20, 1, "one two three", "https://x/y"))[0][0][2][0][6];
        var captured = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .First(m => m[6].ValueKind != JsonValueKind.Null)[6];

        Assert.Equal(captured.GetArrayLength(), built.GetArrayLength());
        Assert.Equal(Kinds(captured), Kinds(built));
        Assert.Equal(3, built[1].GetArrayLength());                       // three words
        Assert.Equal(Kinds(captured[1][0]), Kinds(built[1][0]));          // [word, null, null, conf]
    }

    [Fact]
    public void BuiltFolderValues_MatchTheCapturedFolders()
    {
        var vm = Parse(GvWireBuilder.VoicemailResponse(
            "c.1", "m.1", "+15551234567", 1, 1, 1, null, "https://x"))[0][0][5];
        var sms = Parse(GvWireBuilder.SmsResponse(
            "t.1", "m.1", "+15551234567", 1, "hi", 1))[0][0][5];

        Assert.Equal(CapturedFixture.Threads(CapturedFixture.Voicemail)[0][5].GetRawText(), vm.GetRawText());
        Assert.Equal(CapturedFixture.Threads(CapturedFixture.Messages)[0][5].GetRawText(), sms.GetRawText());
    }

    [Fact]
    public void BuiltEmptyResponse_IsAnEmptyThreadArray_NotAParseFailure()
    {
        var built = Parse(GvWireBuilder.EmptyResponse());

        Assert.Equal(JsonValueKind.Array, built.ValueKind);
        Assert.Equal(3, built.GetArrayLength());
        Assert.Equal(0, built[0].GetArrayLength());
    }
}
