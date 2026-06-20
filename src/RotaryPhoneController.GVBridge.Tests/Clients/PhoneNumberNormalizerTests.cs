using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+19195551234", "+19195551234")]   // already E.164
    [InlineData("9195551234", "+19195551234")]      // bare 10-digit NANP
    [InlineData("19195551234", "+19195551234")]     // 11-digit with country code
    [InlineData("(919) 555-1234", "+19195551234")]  // formatted
    [InlineData("919-555-1234", "+19195551234")]
    [InlineData(" 919.555.1234 ", "+19195551234")]  // punctuation + whitespace
    public void Normalize_ValidUsNumbers_ReturnsE164(string input, string expected)
    {
        Assert.True(PhoneNumberNormalizer.TryNormalize(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("12345")]            // too short
    [InlineData("555-1234")]         // 7-digit, no area code
    [InlineData("+44 20 7946 0958")] // non-NANP: out of scope v1, reject rather than guess
    [InlineData("abcdefghij")]       // non-numeric
    [InlineData("+1234567890123456")]// too long
    public void Normalize_InvalidOrUnsupported_ReturnsFalse(string? input)
    {
        Assert.False(PhoneNumberNormalizer.TryNormalize(input, out var result));
        Assert.Null(result);
    }
}
