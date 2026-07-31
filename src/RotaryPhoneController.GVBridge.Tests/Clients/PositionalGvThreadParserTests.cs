using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

/// <summary>
/// Parser behavior against the LIVE CAPTURED responses.
///
/// Every expectation below is cross-checked against the raw JSON rather than restated as a literal,
/// so these tests cannot drift into agreeing with the parser the way the old synthetic-fixture tests
/// did. Hand-written JSON appears only in the malformed-input tests at the bottom, which assert
/// defensive behavior rather than the wire contract.
/// </summary>
public class PositionalGvThreadParserTests
{
    private readonly PositionalGvThreadParser _parser = new();

    // ---- threads ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void ParseThreadList_ReturnsOneNodePerRawThread(string folder)
    {
        var root = CapturedFixture.Response(folder);
        var threads = _parser.ParseThreadList(root);

        // The count comes from the fixture, not a hard-coded number.
        Assert.Equal(CapturedFixture.Threads(folder).Count, threads.Count);
        Assert.NotEmpty(threads);
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void ParseThreadList_ThreadIdsMatchRawIndex0(string folder)
    {
        var expected = CapturedFixture.Threads(folder).Select(t => t[0].GetString()).ToList();
        var actual = _parser.ParseThreadList(CapturedFixture.Response(folder))
            .Select(t => t.ThreadId).ToList();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void ParseThreadList_CounterpartyIsPresent_AndNeverTheAccountOwner(string folder)
    {
        // Counterparty is an identifier, not necessarily a dialable number: real SMS traffic
        // includes numeric short codes and opaque sender ids. The parser passes it through verbatim.
        var accountOwner = CapturedFixture.Messages_(folder)[0][2].GetString();

        foreach (var thread in _parser.ParseThreadList(CapturedFixture.Response(folder)))
        {
            Assert.False(string.IsNullOrEmpty(thread.CounterpartyNumber));
            Assert.NotEqual(accountOwner, thread.CounterpartyNumber);
        }
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void ParseThreadList_CounterpartyMatchesRawParticipant(string folder)
    {
        var expected = CapturedFixture.Threads(folder).Select(t => t[4][0][0].GetString()).ToList();
        var actual = _parser.ParseThreadList(CapturedFixture.Response(folder))
            .Select(t => t.CounterpartyNumber).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseThreadList_HasUnread_IsTheNegationOfTheIsReadFlag()
    {
        // Wire 1 = READ -> HasUnread false. Wire 0 = UNREAD -> HasUnread true.
        // Ground truth: the UI reported exactly "Messages: 2 unread" at capture time.
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var raw = CapturedFixture.Threads(CapturedFixture.Messages);
        var parsed = _parser.ParseThreadList(root);

        for (var i = 0; i < raw.Count; i++)
            Assert.Equal(raw[i][1].GetInt32() == 0, parsed[i].HasUnread);

        Assert.Equal(2, parsed.Count(t => t.HasUnread == true));
    }

    [Fact]
    public void ParseThreadList_CounterpartyName_IsAlwaysNull_BecauseNoNameExistsOnTheWire()
    {
        // GV returns E.164 only. Display names must come from the consumer's contact resolution.
        var threads = _parser.ParseThreadList(CapturedFixture.Response(CapturedFixture.Messages));
        Assert.All(threads, t => Assert.Null(t.CounterpartyName));
    }

    [Fact]
    public void ParseThreadList_PreviewComesFromNewestMessage()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var raw = CapturedFixture.Threads(CapturedFixture.Messages);
        var parsed = _parser.ParseThreadList(root);

        for (var i = 0; i < raw.Count; i++)
        {
            var newestText = raw[i][2].EnumerateArray()
                .OrderByDescending(m => m[1].GetInt64())
                .First()[9].GetString();
            Assert.Equal(newestText, parsed[i].LastMessagePreview);
        }
    }

    [Fact]
    public void ParseThreadList_LastMessageEpoch_IsTheMaxTimestampInTheThread()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var raw = CapturedFixture.Threads(CapturedFixture.Messages);
        var parsed = _parser.ParseThreadList(root);

        for (var i = 0; i < raw.Count; i++)
        {
            var max = raw[i][2].EnumerateArray().Max(m => m[1].GetInt64());
            Assert.Equal(max, parsed[i].LastMessageEpochMs);
        }
    }

    // ---- voicemail ----

    [Fact]
    public void ParseVoicemailList_ReturnsOneNodePerRawMessage()
    {
        var expected = CapturedFixture.Messages_(CapturedFixture.Voicemail).Count;
        var actual = _parser.ParseVoicemailList(CapturedFixture.Response(CapturedFixture.Voicemail));

        Assert.Equal(expected, actual.Count);
        Assert.NotEmpty(actual);
    }

    [Fact]
    public void ParseVoicemailList_PropagatesThreadIdFromTheParentThread()
    {
        // The message node carries no thread id. Losing it breaks the poller's high-water mark and
        // every per-thread filter, so it must be carried down from thread[0].
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var expected = CapturedFixture.Threads(CapturedFixture.Voicemail)
            .SelectMany(t => t[2].EnumerateArray().Select(_ => t[0].GetString()))
            .ToList();

        var parsed = _parser.ParseVoicemailList(root);
        Assert.Equal(expected, parsed.Select(v => v.ThreadId).ToList());
        Assert.All(parsed, v => Assert.NotNull(v.ThreadId));
    }

    [Fact]
    public void ParseVoicemailList_MediaIdIsTheAbsoluteUrlAtIndex11()
    {
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var expected = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .Select(m => m[11].GetString()).ToList();

        var actual = _parser.ParseVoicemailList(root).Select(v => v.MediaId).ToList();

        Assert.Equal(expected, actual);
        Assert.All(actual, u => Assert.StartsWith("https://www.google.com/voice/media/svm/", u));
    }

    [Fact]
    public void ParseVoicemailList_FromNumberIsTheCounterparty_NotUs()
    {
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var accountOwner = CapturedFixture.Messages_(CapturedFixture.Voicemail)[0][2].GetString();
        var expected = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .Select(m => m[15].GetString()).ToList();

        var actual = _parser.ParseVoicemailList(root).Select(v => v.FromNumber).ToList();

        Assert.Equal(expected, actual);
        Assert.All(actual, n => Assert.NotEqual(accountOwner, n));
    }

    [Fact]
    public void ParseVoicemailList_DurationMatchesIndex8()
    {
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var expected = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .Select(m => (int?)m[8].GetInt32()).ToList();

        Assert.Equal(expected, _parser.ParseVoicemailList(root).Select(v => v.DurationSeconds).ToList());
    }

    [Fact]
    public void ParseVoicemailList_IsRead_UsesWire1AsRead()
    {
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var expected = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .Select(m => (bool?)(m[5].GetInt32() == 1)).ToList();

        var actual = _parser.ParseVoicemailList(root).Select(v => v.IsRead).ToList();

        Assert.Equal(expected, actual);
        Assert.Equal(6, actual.Count(r => r == false));   // 6 unread in the capture
    }

    [Fact]
    public void ParseVoicemailList_TranscriptIsFlattenedText_NullWhenPending()
    {
        var root = CapturedFixture.Response(CapturedFixture.Voicemail);
        var raw = CapturedFixture.Messages_(CapturedFixture.Voicemail);
        var parsed = _parser.ParseVoicemailList(root);

        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i][6].ValueKind == JsonValueKind.Null)
            {
                Assert.Null(parsed[i].Transcript);
                continue;
            }
            // Empty word slots are skipped so the joined text never contains double spaces.
            var words = raw[i][6][1].EnumerateArray()
                .Select(w => w[0].GetString()!)
                .Where(w => !string.IsNullOrEmpty(w));
            Assert.Equal(string.Join(' ', words), parsed[i].Transcript);
        }
        Assert.Equal(2, parsed.Count(v => v.Transcript is null));
        Assert.All(parsed.Where(v => v.Transcript is not null),
            v => Assert.DoesNotContain("  ", v.Transcript!));
    }

    [Fact]
    public void ParseVoicemailList_FromNameIsAlwaysNull_NoNameExistsOnTheWire()
    {
        var vms = _parser.ParseVoicemailList(CapturedFixture.Response(CapturedFixture.Voicemail));
        Assert.All(vms, v => Assert.Null(v.FromName));
    }

    [Fact]
    public void ParseVoicemailList_AgainstTheRealPayload_IsNotEmpty()
    {
        // The regression this whole change exists for: the previous parser returned ZERO items for
        // every real response because it required an object root with a "threads" property.
        Assert.NotEmpty(_parser.ParseVoicemailList(CapturedFixture.Response(CapturedFixture.Voicemail)));
    }

    // ---- sms ----

    [Fact]
    public void ParseSmsMessages_ReturnsOneNodePerRawMessage()
    {
        var expected = CapturedFixture.Messages_(CapturedFixture.Messages).Count;
        Assert.Equal(expected,
            _parser.ParseSmsMessages(CapturedFixture.Response(CapturedFixture.Messages)).Count);
    }

    [Fact]
    public void ParseSmsMessages_DirectionComesFromTypeIndex4()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var raw = CapturedFixture.Messages_(CapturedFixture.Messages);
        var parsed = _parser.ParseSmsMessages(root);

        for (var i = 0; i < raw.Count; i++)
        {
            var expected = raw[i][4].GetInt32() switch { 10 => "Inbound", 11 => "Outbound", _ => null };
            Assert.Equal(expected, parsed[i].Direction);
        }
        Assert.Contains(parsed, m => m.Direction == "Inbound");
        Assert.Contains(parsed, m => m.Direction == "Outbound");
        Assert.DoesNotContain(parsed, m => m.Direction is null);
    }

    [Fact]
    public void ParseSmsMessages_TextMatchesIndex9()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var expected = CapturedFixture.Messages_(CapturedFixture.Messages)
            .Select(m => m[9].GetString()).ToList();

        Assert.Equal(expected, _parser.ParseSmsMessages(root).Select(m => m.Text).ToList());
    }

    [Fact]
    public void ParseSmsMessages_PropagatesThreadId_SoPerThreadFilteringWorks()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var parsed = _parser.ParseSmsMessages(root);
        Assert.All(parsed, m => Assert.NotNull(m.ThreadId));

        // Filtering by a real thread id must select exactly that thread's messages — this is the
        // operation GvSmsClient.ListMessagesAsync performs.
        var target = CapturedFixture.Threads(CapturedFixture.Messages)[0];
        var expectedCount = target[2].GetArrayLength();
        Assert.Equal(expectedCount, parsed.Count(m => m.ThreadId == target[0].GetString()));
    }

    [Fact]
    public void ParseSmsMessages_IsRead_UsesWire1AsRead()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var expected = CapturedFixture.Messages_(CapturedFixture.Messages)
            .Select(m => (bool?)(m[5].GetInt32() == 1)).ToList();

        Assert.Equal(expected, _parser.ParseSmsMessages(root).Select(m => m.IsRead).ToList());
    }

    [Fact]
    public void ParseSmsMessages_TimestampMatchesIndex1()
    {
        var root = CapturedFixture.Response(CapturedFixture.Messages);
        var expected = CapturedFixture.Messages_(CapturedFixture.Messages)
            .Select(m => (long?)m[1].GetInt64()).ToList();

        Assert.Equal(expected, _parser.ParseSmsMessages(root).Select(m => m.SentEpochMs).ToList());
    }

    // ---- drift detection ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void CountThreads_MatchesRawThreadCount(string folder) =>
        Assert.Equal(CapturedFixture.Threads(folder).Count,
            _parser.CountThreads(CapturedFixture.Response(folder)));

    [Fact]
    public void CountThreads_IsZero_ForAGenuinelyEmptyFolder()
    {
        using var doc = JsonDocument.Parse("""[[],"0","v1-0-0"]""");
        Assert.Equal(0, _parser.CountThreads(doc.RootElement));
    }

    [Fact]
    public void CountThreads_IsNonZero_EvenWhenThreadBodiesAreUnparseable()
    {
        // The drift signal: a well-formed envelope with unparseable thread bodies still counts as
        // "GV sent us data", which is what lets callers reject it instead of reporting empty.
        using var doc = JsonDocument.Parse("""[["not-a-thread",42],"0","v1-0-0"]""");
        Assert.Equal(2, _parser.CountThreads(doc.RootElement));
        Assert.Empty(_parser.ParseThreadList(doc.RootElement));
    }

    // ---- malformed input (hand-written by design: defensive behavior, not the wire contract) ----

    [Fact]
    public void ParseThreadList_OnObjectRoot_ReturnsEmpty()
    {
        // The shape the ORIGINAL parser required. Real GV never sends this.
        using var doc = JsonDocument.Parse("""{"threads":[],"nextPageToken":null}""");
        Assert.Empty(_parser.ParseThreadList(doc.RootElement));
        Assert.Equal(0, _parser.CountThreads(doc.RootElement));
    }

    [Fact]
    public void ParseThreadList_OnScalarRoot_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("\"not-an-array\"");
        Assert.Empty(_parser.ParseThreadList(doc.RootElement));
    }

    [Fact]
    public void ParseThreadList_OnEmptyRootArray_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("[]");
        Assert.Empty(_parser.ParseThreadList(doc.RootElement));
        Assert.Equal(0, _parser.CountThreads(doc.RootElement));
    }

    [Fact]
    public void ParseVoicemailList_OnTruncatedMessage_YieldsNullsRatherThanThrowing()
    {
        // A short message array must not throw — every accessor is bounds-checked.
        using var doc = JsonDocument.Parse("""[[["t.1",1,[["m.1",123]],null,[],[1,4]]],"0","v1-0-0"]""");
        var vms = _parser.ParseVoicemailList(doc.RootElement);

        var vm = Assert.Single(vms);
        Assert.Equal("m.1", vm.MessageId);
        Assert.Equal("t.1", vm.ThreadId);
        Assert.Equal(123, vm.ReceivedEpochMs);
        Assert.Null(vm.MediaId);
        Assert.Null(vm.DurationSeconds);
        Assert.Null(vm.IsRead);
        Assert.Null(vm.Transcript);
    }

    [Fact]
    public void ParseSmsMessages_OnUnknownType_LeavesDirectionNull()
    {
        // 99 is not a known SMS type — must stay null rather than defaulting to Inbound.
        var msg = string.Join(',', Enumerable.Range(0, 19).Select(i => i switch
        {
            0 => "\"m.1\"",
            1 => "123",
            4 => "99",
            9 => "\"hi\"",
            _ => "null"
        }));
        using var doc = JsonDocument.Parse($"""[[["t.1",1,[[{msg}]],null,[],[1,2]]],"0","v1-0-0"]""");

        var sms = Assert.Single(_parser.ParseSmsMessages(doc.RootElement));
        Assert.Null(sms.Direction);
        Assert.Equal("hi", sms.Text);
    }
}
