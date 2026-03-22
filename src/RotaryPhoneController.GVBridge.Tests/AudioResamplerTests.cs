using RotaryPhoneController.GVBridge.Audio;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests;

public class AudioResamplerTests
{
    [Fact]
    public void Resample16kTo8k_HalvesSampleCount()
    {
        var input = new byte[640]; // 320 samples × 2 bytes
        for (int i = 0; i < 320; i++)
        {
            var value = (short)(i * 100);
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }
        var result = AudioResampler.Resample16kTo8k(input);
        Assert.Equal(320, result.Length); // 160 samples × 2 bytes
    }

    [Fact]
    public void Resample8kTo16k_DoublesSampleCount()
    {
        var input = new byte[320]; // 160 samples × 2 bytes
        for (int i = 0; i < 160; i++)
        {
            var value = (short)(i * 200);
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }
        var result = AudioResampler.Resample8kTo16k(input);
        Assert.Equal(640, result.Length); // 320 samples × 2 bytes
    }

    [Fact]
    public void Resample16kTo8k_PreservesSignalShape()
    {
        var input = new byte[640];
        for (int i = 0; i < 320; i++)
        {
            var value = (short)(short.MaxValue * Math.Sin(2 * Math.PI * 1000 * i / 16000.0));
            input[i * 2] = (byte)(value & 0xFF);
            input[i * 2 + 1] = (byte)(value >> 8);
        }
        var result = AudioResampler.Resample16kTo8k(input);
        short maxAbs = 0;
        for (int i = 0; i < result.Length / 2; i++)
        {
            var sample = (short)(result[i * 2] | (result[i * 2 + 1] << 8));
            maxAbs = Math.Max(maxAbs, Math.Abs(sample));
        }
        Assert.True(maxAbs > short.MaxValue / 2, $"Signal amplitude too low: {maxAbs}");
    }

    [Fact]
    public void Resample_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(AudioResampler.Resample16kTo8k(Array.Empty<byte>()));
        Assert.Empty(AudioResampler.Resample8kTo16k(Array.Empty<byte>()));
    }
}
