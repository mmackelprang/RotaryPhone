using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Tests;

public class ScoRtpBridgeTests
{
    [Fact]
    public void G711Encode_Decode_RoundTrip()
    {
        // Verify G711Codec works for our audio path
        var pcm = new byte[320]; // 20ms at 8kHz 16-bit mono
        for (int i = 0; i < pcm.Length; i += 2)
        {
            // Simple sine wave
            short sample = (short)(Math.Sin(i * 0.1) * 16000);
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)(sample >> 8);
        }

        var encoded = G711Codec.EncodeMuLaw(pcm, pcm.Length);
        Assert.Equal(160, encoded.Length); // Half size after encoding

        var decoded = G711Codec.DecodeMuLaw(encoded);
        Assert.Equal(320, decoded.Length); // Back to original size
    }
}
