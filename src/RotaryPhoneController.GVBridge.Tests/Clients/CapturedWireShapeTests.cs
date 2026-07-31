using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

/// <summary>
/// Pins the GV api2thread/list wire contract against LIVE CAPTURED responses.
///
/// These assertions read the raw JSON directly — deliberately NOT through the parser — so they
/// describe what Google sends rather than what our code believes. If Google moves a field, this
/// class fails independently of <see cref="PositionalGvThreadParser"/>, which is the protection the
/// old synthetic fixtures could never provide: those were written to agree with the parser, so the
/// suite stayed green while the feature returned nothing for weeks.
///
/// If one of these fails, RE-CAPTURE. Do not edit the fixture to match the code.
/// </summary>
public class CapturedWireShapeTests
{
    // ---- root envelope ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Root_IsArrayOfThree_NotAnObject(string folder)
    {
        var root = CapturedFixture.Response(folder);

        // The original parser required an object with a "threads" property. No real response is an
        // object at all — this is the single assertion that would have caught the whole defect.
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(3, root.GetArrayLength());
        Assert.Equal(JsonValueKind.Array, root[0].ValueKind);   // threads
        Assert.Equal(JsonValueKind.String, root[1].ValueKind);  // numeric string
        Assert.Equal(JsonValueKind.String, root[2].ValueKind);  // version cursor
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Root_HasNoThreadsProperty_AndNoNextPageToken(string folder)
    {
        var root = CapturedFixture.Response(folder);
        // An array has no properties at all; assert the kind so the intent is explicit.
        Assert.NotEqual(JsonValueKind.Object, root.ValueKind);
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Root_SecondElement_IsNumericString(string folder)
    {
        // Redaction preserves length but not digits, so assert the shape we can still see.
        var value = CapturedFixture.Response(folder)[1].GetString();
        Assert.False(string.IsNullOrEmpty(value));
    }

    // ---- request body ----

    [Theory]
    [InlineData(CapturedFixture.Voicemail, GvThreadFolder.Voicemail)]
    [InlineData(CapturedFixture.Messages, GvThreadFolder.Sms)]
    [InlineData(CapturedFixture.Calls, GvThreadFolder.Calls)]
    public void BuiltRequestBody_MatchesCapturedRequest_ByteForByte(
        string folder, GvThreadFolder enumValue)
    {
        // The captured requests all used count=20; build the same call and compare literally.
        var built = RotaryPhoneController.GVBridge.Protocol.GvProtobuf.BuildArray(
            enumValue.ToWireValue(), 20, 15, null, null, new object?[] { null, 1, 1, 1 });

        Assert.Equal(CapturedFixture.RequestBody(folder), built);
    }

    [Theory]
    [InlineData(CapturedFixture.Voicemail, 4)]
    [InlineData(CapturedFixture.Messages, 2)]
    [InlineData(CapturedFixture.Calls, 3)]
    public void CapturedRequest_FolderIsAtIndex0_AndCountAtIndex1(string folder, int expectedWire)
    {
        using var doc = JsonDocument.Parse(CapturedFixture.RequestBody(folder));
        var req = doc.RootElement;

        Assert.Equal(6, req.GetArrayLength());
        Assert.Equal(expectedWire, req[0].GetInt32());   // folder — index 0
        Assert.Equal(20, req[1].GetInt32());             // count  — index 1, NOT index 2
        Assert.Equal(15, req[2].GetInt32());
        Assert.Equal(JsonValueKind.Null, req[3].ValueKind);
        Assert.Equal(JsonValueKind.Null, req[4].ValueKind);
        Assert.Equal("[null,1,1,1]", req[5].GetRawText());
    }

    // ---- folder enum ----

    [Theory]
    [InlineData(CapturedFixture.Voicemail, GvThreadFolder.Voicemail)]
    [InlineData(CapturedFixture.Messages, GvThreadFolder.Sms)]
    [InlineData(CapturedFixture.Calls, GvThreadFolder.Calls)]
    public void EveryThread_EchoesTheRequestedFolder_AtIndex5(string folder, GvThreadFolder enumValue)
    {
        // thread[5] = [1, folder]. This is a genuine round-trip: the folder we asked for comes back
        // on every thread, so a wrong ToWireValue() cannot pass this test.
        var expected = enumValue.ToWireValue();
        foreach (var thread in CapturedFixture.Threads(folder))
        {
            Assert.Equal(2, thread[5].GetArrayLength());
            Assert.Equal(1, thread[5][0].GetInt32());
            Assert.Equal(expected, thread[5][1].GetInt32());
        }
    }

    [Fact]
    public void VoicemailAndCalls_AreDistinctFolders()
    {
        // The old code mapped Voicemail to 3, which is Calls — a corrected parser with the old enum
        // would have silently returned call records for voicemail queries.
        Assert.NotEqual(GvThreadFolder.Calls.ToWireValue(), GvThreadFolder.Voicemail.ToWireValue());
        Assert.Equal(3, GvThreadFolder.Calls.ToWireValue());
        Assert.Equal(4, GvThreadFolder.Voicemail.ToWireValue());
        Assert.Equal(2, GvThreadFolder.Sms.ToWireValue());
    }

    [Fact]
    public void AllFolder_HasNoWireValue_AndThrowsRatherThanGuessing()
    {
        // UNVERIFIED — never captured. Guessing an integer here returns another folder's records
        // under a 200 OK, which is indistinguishable from success at every layer above.
        Assert.False(GvThreadFolder.All.IsVerified());
        var ex = Assert.Throws<NotSupportedException>(() => GvThreadFolder.All.ToWireValue());
        Assert.Contains("no verified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(GvThreadFolder.Sms)]
    [InlineData(GvThreadFolder.Calls)]
    [InlineData(GvThreadFolder.Voicemail)]
    public void CapturedFolders_AreMarkedVerified(GvThreadFolder folder) =>
        Assert.True(folder.IsVerified());

    // ---- thread node ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void EveryCapture_Has20Threads(string folder) =>
        Assert.Equal(20, CapturedFixture.Threads(folder).Count);

    [Theory]
    [InlineData(CapturedFixture.Voicemail, 11)]
    [InlineData(CapturedFixture.Calls, 11)]
    [InlineData(CapturedFixture.Messages, 12)]
    public void ThreadLength_VariesByFolder_SoNothingMayHardCodeIt(string folder, int expectedLength)
    {
        // SMS threads carry a 12th element. Any parser that assumed a fixed length would break on
        // one of these folders; every accessor must be bounds-checked instead.
        foreach (var thread in CapturedFixture.Threads(folder))
            Assert.Equal(expectedLength, thread.GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Thread_Index0_IsThreadId_Index1_IsReadInt_Index2_IsMessages(string folder)
    {
        foreach (var thread in CapturedFixture.Threads(folder))
        {
            Assert.Equal(JsonValueKind.String, thread[0].ValueKind);
            Assert.Equal(JsonValueKind.Number, thread[1].ValueKind);
            Assert.InRange(thread[1].GetInt32(), 0, 1);
            Assert.Equal(JsonValueKind.Array, thread[2].ValueKind);
            Assert.True(thread[2].GetArrayLength() > 0);
        }
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Thread_Index8_IsCounterpartyIdentifierArray(string folder)
    {
        // Identifiers, not necessarily phone numbers — see Counterparty_IsNotAlwaysAPhoneNumber.
        foreach (var thread in CapturedFixture.Threads(folder))
        {
            Assert.Equal(JsonValueKind.Array, thread[8].ValueKind);
            Assert.NotEqual(0, thread[8].GetArrayLength());
            foreach (var n in thread[8].EnumerateArray())
                Assert.False(string.IsNullOrEmpty(n.GetString()));
        }
    }

    // ---- read-state polarity ----

    [Fact]
    public void ThreadReadState_Wire1IsRead_Wire0IsUnread_MessagesFolder()
    {
        // GROUND TRUTH: at capture time the GV UI reported exactly "Messages: 2 unread".
        // The SMS capture has thread[1] == 0 on exactly 2 threads and == 1 on 18.
        // That is what fixes the polarity: 0 = UNREAD, 1 = READ. The field is isRead, not hasUnread.
        var threads = CapturedFixture.Threads(CapturedFixture.Messages);
        Assert.Equal(2, threads.Count(t => t[1].GetInt32() == 0));
        Assert.Equal(18, threads.Count(t => t[1].GetInt32() == 1));
    }

    [Fact]
    public void VoicemailReadState_HasSixUnread_MatchingMessageLevelFlags()
    {
        // Thread-level and message-level read flags agree, cross-validating both positions.
        var threads = CapturedFixture.Threads(CapturedFixture.Voicemail);
        var messages = CapturedFixture.Messages_(CapturedFixture.Voicemail);

        Assert.Equal(6, threads.Count(t => t[1].GetInt32() == 0));
        Assert.Equal(6, messages.Count(m => m[5].GetInt32() == 0));
        Assert.Equal(14, messages.Count(m => m[5].GetInt32() == 1));
    }

    // ---- message node ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void MessageLength_IsAtLeast19_ButNotFixed(string folder)
    {
        // The calls capture contains one 22-element message. A fixed-length assumption breaks.
        foreach (var msg in CapturedFixture.Messages_(folder))
            Assert.True(msg.GetArrayLength() >= 19, $"message length {msg.GetArrayLength()} < 19");
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void MessageIndex2_IsAlwaysTheAccountOwner_NotTheSender(string folder)
    {
        // THE TRAP: index 2 is OUR number on every message in every folder. The synthetic contract
        // read it as "from", which would have shown every voicemail as being from ourselves.
        var messages = CapturedFixture.Messages_(folder);
        var distinct = messages.Select(m => m[2].GetString()).Distinct().ToList();

        Assert.Single(distinct);
        // ...and it is never the counterparty.
        Assert.All(messages, m => Assert.NotEqual(m[2].GetString(), m[15].GetString()));
    }

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void MessageIndex15_IsTheCounterparty_AgreeingWithIndex3Array(string folder)
    {
        foreach (var msg in CapturedFixture.Messages_(folder))
        {
            Assert.Equal(JsonValueKind.String, msg[15].ValueKind);
            Assert.False(string.IsNullOrEmpty(msg[15].GetString()));
            Assert.Equal(msg[3][0].GetString(), msg[15].GetString());
        }
    }

    [Fact]
    public void Counterparty_IsNotAlwaysAPhoneNumber()
    {
        // IMPORTANT and easy to get wrong: the SMS capture's counterparty field holds THREE distinct
        // identifier forms — E.164 (+1XXXXXXXXXX), bare numeric SHORT CODES (5-6 digits, e.g. 2FA
        // senders), and 36-char opaque sender tokens (all inbound). Anything downstream that assumes
        // a leading '+' or a dialable number will mishandle roughly a third of real SMS traffic.
        // The parser therefore passes the value through verbatim and never normalizes it.
        var counterparties = CapturedFixture.Messages_(CapturedFixture.Messages)
            .Select(m => m[15].GetString()!)
            .ToList();

        Assert.Contains(counterparties, c => c.StartsWith('+'));          // E.164
        Assert.Contains(counterparties, c => !c.StartsWith('+') && c.Length <= 6);   // short code
        Assert.Contains(counterparties, c => c.Length == 36);             // opaque sender id
        Assert.All(counterparties, c => Assert.False(string.IsNullOrEmpty(c)));
    }

    [Fact]
    public void MessageType_Index4_SeparatesVoicemailFromSmsFromCalls()
    {
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Voicemail),
            m => Assert.Equal(2, m[4].GetInt32()));

        // SMS is 10 (inbound) or 11 (outbound) — both present in the capture.
        var sms = CapturedFixture.Messages_(CapturedFixture.Messages).Select(m => m[4].GetInt32()).ToList();
        Assert.All(sms, t => Assert.Contains(t, new[] { 10, 11 }));
        Assert.Contains(10, sms);
        Assert.Contains(11, sms);

        // Calls use a wider set (1, 14, 0) — deliberately asserted so a future "calls type == 1"
        // assumption cannot creep in unchallenged.
        var calls = CapturedFixture.Messages_(CapturedFixture.Calls).Select(m => m[4].GetInt32()).Distinct();
        Assert.True(calls.Count() > 1);
    }

    [Fact]
    public void Duration_Index8_IsPresentForVoicemailAndCalls_NullForSms()
    {
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Voicemail),
            m => Assert.Equal(JsonValueKind.Number, m[8].ValueKind));
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Calls),
            m => Assert.Equal(JsonValueKind.Number, m[8].ValueKind));
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Messages),
            m => Assert.Equal(JsonValueKind.Null, m[8].ValueKind));
    }

    [Fact]
    public void SmsText_Index9_IsPopulatedForSms_AndEmptyForVoicemailAndCalls()
    {
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Messages),
            m => Assert.False(string.IsNullOrEmpty(m[9].GetString())));
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Voicemail),
            m => Assert.Equal("", m[9].GetString()));
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Calls),
            m => Assert.Equal("", m[9].GetString()));
    }

    [Fact]
    public void MediaUrl_Index11_IsAbsoluteOnWwwGoogleCom_ForEveryVoicemail()
    {
        foreach (var msg in CapturedFixture.Messages_(CapturedFixture.Voicemail))
        {
            var url = msg[11].GetString();
            Assert.NotNull(url);
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));

            // A DIFFERENT host from the API base (clients6.google.com) — the reason the bytes must be
            // proxied server-side rather than fetched from the browser.
            Assert.Equal("www.google.com", uri!.Host);
            Assert.StartsWith("/voice/media/svm/", uri.AbsolutePath);
            Assert.Equal("", uri.Query);   // no ?id=&key= query string
        }
    }

    [Fact]
    public void MediaUrl_IsNull_ForSmsAndCalls()
    {
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Messages),
            m => Assert.Equal(JsonValueKind.Null, m[11].ValueKind));
        Assert.All(CapturedFixture.Messages_(CapturedFixture.Calls),
            m => Assert.Equal(JsonValueKind.Null, m[11].ValueKind));
    }

    [Fact]
    public void Transcript_Index6_IsConfidencePlusWordArray_OrNullWhenPending()
    {
        var messages = CapturedFixture.Messages_(CapturedFixture.Voicemail);
        var withTranscript = messages.Where(m => m[6].ValueKind != JsonValueKind.Null).ToList();

        Assert.Equal(18, withTranscript.Count);      // 2 of 20 are pending
        foreach (var msg in withTranscript)
        {
            Assert.Equal(2, msg[6].GetArrayLength());
            Assert.Equal(JsonValueKind.Number, msg[6][0].ValueKind);   // confidence
            Assert.Equal(JsonValueKind.Array, msg[6][1].ValueKind);    // [[word, _, _, conf], ...]
            var firstWord = msg[6][1][0];
            Assert.Equal(4, firstWord.GetArrayLength());
            Assert.Equal(JsonValueKind.String, firstWord[0].ValueKind);
        }
    }

    [Fact]
    public void NoDisplayName_ExistsAnywhereInThePayload()
    {
        // Every string leaf in the voicemail capture is an id, an E.164, a URL, a transcript word or
        // a redaction placeholder — there is no contact/display name at ANY position. FromName can
        // therefore never be populated from this payload; contact resolution is the consumer's job.
        var names = CapturedFixture.Messages_(CapturedFixture.Voicemail)
            .SelectMany(m => m.EnumerateArray())
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => s.Length > 0 && !s.StartsWith('+') && !s.StartsWith("http") && s != "")
            .ToList();

        // All remaining strings are opaque ids / redaction placeholders, never human names.
        Assert.All(names, s => Assert.StartsWith("REDACTED", s));
    }

    // ---- version cursor / paging ----

    [Theory]
    [MemberData(nameof(CapturedFixture.AllFolders), MemberType = typeof(CapturedFixture))]
    public void Root2_IsAVersionCursor_NotTreatedAsAPageToken(string folder)
    {
        var root = CapturedFixture.Response(folder);
        var cursor = PositionalGvThreadParser.ParseVersionCursor(root);

        Assert.False(string.IsNullOrEmpty(cursor));
        // Exposed as a cursor, but deliberately NOT surfaced as a next-page token: paging semantics
        // are UNVERIFIED, and returning it as a token would make callers re-read page 1 forever.
        Assert.Null(new PositionalGvThreadParser().ParseNextPageToken(root));
    }
}
