using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class PositionalGvThreadParserTests
{
    private static JsonElement LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var json = File.ReadAllText(path);
        // Clone so the element stays valid after the JsonDocument is disposed.
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private readonly PositionalGvThreadParser _parser = new();

    [Fact]
    public void ParseThreadList_ReturnsAllThreads()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var threads = _parser.ParseThreadList(root);

        Assert.Equal(2, threads.Count);
        Assert.Equal("t.+19195551234", threads[0].ThreadId);
        Assert.Equal("+19195551234", threads[0].CounterpartyNumber);
        Assert.Equal("Alice Example", threads[0].CounterpartyName);
        Assert.True(threads[0].HasUnread);
        Assert.Equal("hey are you around?", threads[0].LastMessagePreview);
    }

    [Fact]
    public void ParseThreadList_ToleratesMissingName()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var threads = _parser.ParseThreadList(root);
        Assert.Null(threads[1].CounterpartyName);
    }

    [Fact]
    public void ParseThreadList_OnNonArrayRoot_ReturnsEmpty()
    {
        var root = JsonDocument.Parse("\"not-an-array\"").RootElement;
        Assert.Empty(_parser.ParseThreadList(root));
    }

    [Fact]
    public void ParseVoicemailList_ParsesMediaIdAndTranscript()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        var vms = _parser.ParseVoicemailList(root);

        Assert.Equal(2, vms.Count);
        Assert.Equal("vm.111", vms[0].MessageId);
        Assert.Equal("media-abc-123", vms[0].MediaId);
        Assert.Equal("Hey it's Alice, call me back.", vms[0].Transcript);
        Assert.Equal(23, vms[0].DurationSeconds);
        Assert.False(vms[0].IsRead);
    }

    [Fact]
    public void ParseVoicemailList_NullTranscriptIsPending()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        var vms = _parser.ParseVoicemailList(root);
        Assert.Null(vms[1].Transcript);          // pending/absent — ADR §3.3
        Assert.Equal("media-def-456", vms[1].MediaId);
    }

    [Fact]
    public void ParseSmsMessages_MapsDirection()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var msgs = _parser.ParseSmsMessages(root);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("m.111", msgs[0].MessageId);
        Assert.Equal("Inbound", msgs[0].Direction);   // wire 0 -> Inbound
        Assert.Equal("Outbound", msgs[1].Direction);  // wire 1 -> Outbound
        Assert.Equal("hey are you around?", msgs[0].Text);
    }

    [Fact]
    public void ParseNextPageToken_ReturnsToken()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        Assert.Equal("PAGE2", _parser.ParseNextPageToken(root));
    }

    [Fact]
    public void ParseNextPageToken_NullWhenAbsent()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        Assert.Null(_parser.ParseNextPageToken(root));
    }
}
