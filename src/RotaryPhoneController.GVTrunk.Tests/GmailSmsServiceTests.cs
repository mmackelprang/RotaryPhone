using Xunit;
using RotaryPhoneController.GVTrunk.Services;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Tests;

public class GmailSmsServiceTests
{
    [Theory]
    [InlineData("New text message from +15551234567", SmsType.Sms, "+15551234567")]
    [InlineData("New text message from (555) 123-4567", SmsType.Sms, "(555) 123-4567")]
    [InlineData("Missed call from +15559876543", SmsType.MissedCall, "+15559876543")]
    [InlineData("Missed call from (555) 987-6543", SmsType.MissedCall, "(555) 987-6543")]
    public void ParseSubject_ExtractsTypeAndNumber(string subject, SmsType expectedType, string expectedNumber)
    {
        var result = GmailSmsService.ParseGvSubject(subject);
        Assert.NotNull(result);
        Assert.Equal(expectedType, result.Value.type);
        Assert.Equal(expectedNumber, result.Value.number);
    }

    [Theory]
    [InlineData("Re: Your order confirmation")]
    [InlineData("")]
    [InlineData("Google Voice notification")]
    public void ParseSubject_ReturnsNull_ForNonGvSubjects(string subject)
    {
        var result = GmailSmsService.ParseGvSubject(subject);
        Assert.Null(result);
    }
}
