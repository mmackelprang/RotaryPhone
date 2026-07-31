using System.Text;

namespace RotaryPhoneController.GVBridge.Tests.Support;

/// <summary>
/// Builds api2thread/list response bodies in the REAL captured wire shape, for tests that need a
/// specific scenario (a known id, a controlled timestamp) rather than the bulk captures.
///
/// This type exists so that no test hand-writes positional JSON again. Previously every test built
/// its own approximation of the shape, each one agreeing with the parser and none with Google —
/// which is how a completely broken parser kept a fully green suite. There is now exactly ONE place
/// in the test project that encodes the wire layout, and <c>GvWireBuilderShapeTests</c> asserts that
/// place still matches the live captures.
///
/// If Google changes the shape: fix the captures first, then this builder. Never the other way.
/// </summary>
public static class GvWireBuilder
{
    /// <summary>Message type wire values (VERIFIED).</summary>
    public const int TypeVoicemail = 2;
    public const int TypeCall = 1;
    public const int TypeSmsInbound = 10;
    public const int TypeSmsOutbound = 11;

    /// <summary>The account owner's own number — message index 2 on every message.</summary>
    public const string AccountOwner = "+15550001001";

    /// <summary>
    /// A full response envelope: <c>[threads, "numeric", "versionCursor"]</c>.
    /// </summary>
    public static string Response(params string[] threads) =>
        $"[[{string.Join(',', threads)}],\"1638764347570\",\"v1-1-1785332793946724\"]";

    /// <summary>
    /// A thread node in the captured layout (11 elements; SMS threads add a 12th, which the parser
    /// must tolerate but never requires).
    /// <code>
    /// [ threadId, isRead, messages[], null, participants[], [1, folder], null, null, [cp], null, 0 ]
    /// </code>
    /// </summary>
    /// <param name="isRead">Wire polarity: 1 = READ, 0 = UNREAD.</param>
    public static string Thread(
        string threadId, int folder, int isRead, string counterparty, params string[] messages) =>
        "[" + string.Join(',', [
            Json(threadId),
            isRead.ToString(),
            $"[{string.Join(',', messages)}]",
            "null",
            $"[[{Json(counterparty)},{Json(counterparty)},null,null,null,null,0]]",
            $"[1,{folder}]",
            "null",
            "null",
            $"[{Json(counterparty)}]",
            "null",
            "0"
        ]) + "]";

    /// <summary>
    /// A message node in the captured layout (19 elements).
    /// <code>
    /// [ msgId, epochMs, accountOwner, [cp x7], type, isRead, transcript, null, durationSec,
    ///   smsText, null, mediaUrl, int, int, null, counterparty, 0, null, [null, n] ]
    /// </code>
    /// </summary>
    /// <param name="isRead">Wire polarity: 1 = READ, 0 = UNREAD.</param>
    public static string Message(
        string messageId,
        long epochMs,
        string counterparty,
        int type,
        int isRead,
        int? durationSeconds = null,
        string? smsText = null,
        string? mediaUrl = null,
        string? transcript = null)
    {
        var transcriptJson = transcript is null
            ? "null"
            // [confidence, [[word, null, null, conf], ...]] — the captured transcript shape.
            : $"[0.95,[{string.Join(',', transcript.Split(' ')
                .Select(w => $"[{Json(w)},null,null,0.95]"))}]]";

        return "[" + string.Join(',', [
            Json(messageId),
            epochMs.ToString(),
            Json(AccountOwner),
            $"[{Json(counterparty)},{Json(counterparty)},null,null,null,null,0]",
            type.ToString(),
            isRead.ToString(),
            transcriptJson,
            "null",
            durationSeconds?.ToString() ?? "null",
            Json(smsText ?? ""),
            "null",
            mediaUrl is null ? "null" : Json(mediaUrl),
            "3",
            "1",
            "null",
            Json(counterparty),
            "0",
            "null",
            "[null,2]"
        ]) + "]";
    }

    /// <summary>A single-voicemail response — the most common test scenario.</summary>
    public static string VoicemailResponse(
        string threadId, string messageId, string counterparty, long epochMs,
        int durationSeconds, int isRead, string? transcript, string mediaUrl) =>
        Response(Thread(threadId, folder: 4, isRead, counterparty,
            Message(messageId, epochMs, counterparty, TypeVoicemail, isRead,
                durationSeconds: durationSeconds, mediaUrl: mediaUrl, transcript: transcript)));

    /// <summary>A single-SMS response.</summary>
    public static string SmsResponse(
        string threadId, string messageId, string counterparty, long epochMs,
        string text, int isRead, bool outbound = false) =>
        Response(Thread(threadId, folder: 2, isRead, counterparty,
            Message(messageId, epochMs, counterparty,
                outbound ? TypeSmsOutbound : TypeSmsInbound, isRead, smsText: text)));

    /// <summary>An empty folder — a genuinely empty result, distinct from a parse failure.</summary>
    public static string EmptyResponse() => Response();

    private static string Json(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString()
            });
        }
        return sb.Append('"').ToString();
    }
}
