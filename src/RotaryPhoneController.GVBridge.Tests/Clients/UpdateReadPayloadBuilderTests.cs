using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class UpdateReadPayloadBuilderTests
{
    private readonly IUpdateReadPayloadBuilder _builder = new UpdateReadPayloadBuilder();

    [Fact]
    public void BuildVoicemail_ProducesArray_CarryingIdAndReadBool()
    {
        var payload = _builder.BuildVoicemail(messageId: "vm.1", threadId: "t.+19195551234", isRead: true);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var json = doc.RootElement.GetRawText();
        Assert.Contains("vm.1", json);                  // the id is in the payload
        Assert.Contains("true", json);                  // the read bool is in the payload
    }

    [Fact]
    public void BuildSmsThread_PerThreadMark_ProducesAtLeastOnePayload_CarryingThreadIdAndReadBool()
    {
        // Per-thread grain (ADR §4.2 Q4): a thread mark covers every message in the thread. If GV's native
        // grain is per-message (UNVERIFIED — §11 step 8) this yields one payload per id; if thread-level,
        // one. Either way each payload carries the thread id + the read bool.
        var payloads = _builder.BuildSmsThread(
            threadId: "t.abc", messageIds: new[] { "m.1", "m.2" }, isRead: true);

        Assert.NotEmpty(payloads);
        foreach (var p in payloads)
        {
            using var doc = JsonDocument.Parse(p);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Contains("t.abc", doc.RootElement.GetRawText());
            Assert.Contains("true", doc.RootElement.GetRawText());
        }
    }

    [Fact]
    public void BuildSmsThread_NoMessageIds_StillProducesThreadLevelPayload()
    {
        // A thread with no enumerated message ids still produces one thread-level updateread payload
        // (the default impl's thread-level fallback), so the route can mark an empty/preview-only thread.
        var payloads = _builder.BuildSmsThread("t.abc", messageIds: Array.Empty<string>(), isRead: true);
        Assert.Single(payloads);
        Assert.Contains("t.abc", JsonDocument.Parse(payloads[0]).RootElement.GetRawText());
    }
}
