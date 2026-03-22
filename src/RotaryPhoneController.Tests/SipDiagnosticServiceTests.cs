using Microsoft.Extensions.Logging;
using Moq;
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

    [Fact]
    public void HandleSipMessage_AddsToLog()
    {
        var entry = new SipMessageEntry(DateTime.UtcNow, SipDirection.Received, "REGISTER",
            "192.168.86.250:5060", "0.0.0.0:5060", 200, "OK", null, "call-1");
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
            "REGISTER", "192.168.86.250", "0.0.0.0:5060", null, null, null, null));
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
            "INVITE", "local", "sip:1000@192.168.86.250", null, null, null, "call-timeout"));
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
}
