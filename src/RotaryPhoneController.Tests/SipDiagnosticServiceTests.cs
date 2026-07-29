using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Diagnostics;
using Xunit;

namespace RotaryPhoneController.Tests;

public class SipDiagnosticServiceTests
{
    private readonly SipDiagnosticService _service;

    public SipDiagnosticServiceTests()
    {
        _service = new SipDiagnosticService(Mock.Of<ILogger<SipDiagnosticService>>());
    }

    /// <summary>
    /// A sent INVITE. Timestamp is backdated by default because _pendingInvites stores the entry's own
    /// timestamp — backdating makes timeout tests deterministic without sleeping past the 5s InviteTimeout.
    /// </summary>
    private static SipMessageEntry SentInvite(string callId, string? note = null) =>
        new(DateTime.UtcNow.AddSeconds(-30), SipDirection.Sent, "INVITE",
            "192.0.2.1:5060", "192.0.2.250:5060", null, null, note, callId);

    /// <summary>A response to an INVITE. Responses are logged under their CSeq method, hence "INVITE".</summary>
    private static SipMessageEntry InviteResponse(string callId, int code, string statusText) =>
        new(DateTime.UtcNow, SipDirection.Received, "INVITE",
            "192.0.2.250:5060", "192.0.2.1:5060", code, statusText, null, callId);

    /// <summary>Captures every bell-failure and bell-success signal the service raises.</summary>
    private sealed record Signals(
        List<(string CallId, BellFailureReason Reason, string? Target, string? Detail)> Failures,
        List<string> Successes);

    private Signals CaptureSignals()
    {
        var signals = new Signals([], []);
        _service.OnSentInviteFailed += (callId, reason, target, detail) =>
            signals.Failures.Add((callId, reason, target, detail));
        _service.OnSentInviteSucceeded += callId => signals.Successes.Add(callId);
        return signals;
    }

    [Fact]
    public void HandleSipMessage_AddsToLog()
    {
        var entry = new SipMessageEntry(DateTime.UtcNow, SipDirection.Received, "REGISTER",
            "192.0.2.250:5060", "0.0.0.0:5060", 200, "OK", null, "call-1");
        _service.HandleSipMessage(entry);
        var log = _service.GetRecentMessages(10);
        Assert.Single(log);
        Assert.Equal("REGISTER", log[0].Method);
    }

    [Fact]
    public void HandleSipMessage_RingBufferLimitsTo200()
    {
        for (int i = 0; i < 250; i++)
            _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
                "OPTIONS", "a", "b", 200, "OK", null, $"call-{i}"));
        var log = _service.GetRecentMessages(300);
        Assert.Equal(200, log.Count);
    }

    [Fact]
    public void HandleSipMessage_RegisterUpdatesRegistrationState()
    {
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "192.0.2.250", "0.0.0.0:5060", null, null, null, null));
        var health = _service.GetHt801Health();
        Assert.True(health.IsRegistered);
        Assert.NotNull(health.LastRegisterReceived);
    }

    [Fact]
    public void DetectInviteTimeout_GeneratesDiagnosis()
    {
        string? diagnosisIssue = null;
        _service.OnDiagnosisGenerated += (issue, suggestions) => diagnosisIssue = issue;
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow.AddSeconds(-6), SipDirection.Sent,
            "INVITE", "local", "sip:1000@192.0.2.250", null, null, null, "call-timeout"));
        _service.CheckInviteTimeouts();
        Assert.NotNull(diagnosisIssue);
        Assert.Contains("INVITE", diagnosisIssue);
    }

    [Fact]
    public void GetRecentMessages_FiltersByMethod()
    {
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "a", "b", null, null, null, null));
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Sent,
            "INVITE", "a", "b", null, null, null, null));
        _service.HandleSipMessage(new SipMessageEntry(DateTime.UtcNow, SipDirection.Received,
            "REGISTER", "a", "b", null, null, null, null));
        var invites = _service.GetRecentMessages(10, "INVITE");
        Assert.Single(invites);
    }

    #region Sent-INVITE outcome tracking

    [Fact]
    public void SentInviteWithDiagnosticNote_IsNotTracked_SoTimeoutRaisesNoSecondFailure()
    {
        var signals = CaptureSignals();

        // A note on a SENT invite means the send itself failed. CallManager already reported that
        // failure synchronously with the accurate Unreachable reason; tracking it here would fire a
        // SECOND failure — with the WRONG reason (Timeout) — when the impossible response times out.
        _service.HandleSipMessage(SentInvite("call-send-failed", note: "Send failed: HostUnreachable"));
        _service.CheckInviteTimeouts();

        Assert.Empty(signals.Failures);

        // The log entry itself is still kept — it is useful in the sip-log diagnostics view.
        Assert.Single(_service.GetRecentMessages(10, "INVITE"));
    }

    [Fact]
    public void SentInviteWithoutDiagnosticNote_RaisesExactlyOneTimeoutFailure()
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(SentInvite("call-no-answer"));
        _service.CheckInviteTimeouts();

        var failure = Assert.Single(signals.Failures);
        Assert.Equal("call-no-answer", failure.CallId);
        Assert.Equal(BellFailureReason.Timeout, failure.Reason);

        // A second sweep must not re-report the same failure.
        _service.CheckInviteTimeouts();
        Assert.Single(signals.Failures);
    }

    [Theory]
    [InlineData(180, "Ringing")]
    [InlineData(200, "OK")]
    public void RingingOrAnsweredResponse_RaisesSuccess_AndNoFailure(int code, string statusText)
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(SentInvite("call-rang"));
        _service.HandleSipMessage(InviteResponse("call-rang", code, statusText));

        Assert.Equal("call-rang", Assert.Single(signals.Successes));
        Assert.Empty(signals.Failures);

        // Resolved, so the later timeout sweep must not fire for it.
        _service.CheckInviteTimeouts();
        Assert.Empty(signals.Failures);
    }

    [Fact]
    public void LateRingingResponse_StillRaisesSuccess_EvenAfterTheInviteTimedOut()
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(SentInvite("call-late-180"));
        _service.CheckInviteTimeouts();          // evicts the pending entry, raises the failure
        Assert.Single(signals.Failures);

        // InviteTimeout is 5s, so a 180 arriving at 5.5s finds nothing pending. It is still proof the
        // bell rang, so it MUST clear the alert — otherwise a false failure sticks until some later
        // call happens to answer inside 5s.
        _service.HandleSipMessage(InviteResponse("call-late-180", 180, "Ringing"));

        Assert.Equal("call-late-180", Assert.Single(signals.Successes));
    }

    [Fact]
    public void RingingResponse_ForAnUntrackedCallId_StillRaisesSuccess()
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(InviteResponse("never-tracked", 180, "Ringing"));

        Assert.Equal("never-tracked", Assert.Single(signals.Successes));
        Assert.Empty(signals.Failures);
    }

    [Theory]
    [InlineData(486, "Busy Here", BellFailureReason.Rejected)]
    [InlineData(404, "Not Found", BellFailureReason.NotRegistered)]
    [InlineData(480, "Temporarily Unavailable", BellFailureReason.NotRegistered)]
    public void ErrorResponse_RaisesFailureWithTheMappedReasonAndDetail(
        int code, string statusText, BellFailureReason expected)
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(SentInvite("call-rejected"));
        _service.HandleSipMessage(InviteResponse("call-rejected", code, statusText));

        var failure = Assert.Single(signals.Failures);
        Assert.Equal("call-rejected", failure.CallId);
        Assert.Equal(expected, failure.Reason);
        Assert.Equal($"{code} {statusText}", failure.Detail);
        Assert.Empty(signals.Successes);

        // Target must be where the INVITE was SENT (the HT801), not where the response was
        // received (us). Received messages are logged from=remote, to=LOCAL, so reading the
        // response's ToAddress would report our own endpoint as the bell target — which is now
        // user-visible via the BellInviteFailed hub event and GET /api/phone/status.
        Assert.Equal("192.0.2.250:5060", failure.Target);

        // Resolved, so the timeout sweep must not fire a second failure for it.
        _service.CheckInviteTimeouts();
        Assert.Single(signals.Failures);
    }

    [Fact]
    public void ProvisionalTrying_RaisesNothing_AndLeavesTheInvitePending()
    {
        var signals = CaptureSignals();

        _service.HandleSipMessage(SentInvite("call-trying"));
        _service.HandleSipMessage(InviteResponse("call-trying", 100, "Trying"));

        // 100 Trying resolves nothing — the bell has not rung yet.
        Assert.Empty(signals.Successes);
        Assert.Empty(signals.Failures);

        // The INVITE must still be pending, so the timeout can still catch a call that never rings.
        _service.CheckInviteTimeouts();
        var failure = Assert.Single(signals.Failures);
        Assert.Equal(BellFailureReason.Timeout, failure.Reason);
    }

    #endregion
}
