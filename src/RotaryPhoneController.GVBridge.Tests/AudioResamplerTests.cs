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

    [Fact]
    public void Resample48kTo8k_ReducesSampleCountBySix()
    {
        // 48 samples at 48kHz = 1ms, should produce 8 samples at 8kHz
        var pcm48k = new byte[48 * 2]; // 48 samples * 2 bytes each
        for (int i = 0; i < 48; i++)
        {
            var sample = (short)(i * 100);
            pcm48k[i * 2] = (byte)(sample & 0xFF);
            pcm48k[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var result = AudioResampler.Resample48kTo8k(pcm48k);
        Assert.Equal(8 * 2, result.Length); // 8 samples * 2 bytes
    }

    [Fact]
    public void Resample8kTo48k_IncreasesSampleCountBySix()
    {
        var pcm8k = new byte[8 * 2]; // 8 samples
        for (int i = 0; i < 8; i++)
        {
            var sample = (short)(i * 1000);
            pcm8k[i * 2] = (byte)(sample & 0xFF);
            pcm8k[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var result = AudioResampler.Resample8kTo48k(pcm8k);
        Assert.Equal(48 * 2, result.Length); // 48 samples * 2 bytes
    }

    [Fact]
    public void Resample48kTo8k_PreservesAudioContent()
    {
        // Constant signal should survive resampling
        var pcm48k = new byte[960 * 2]; // 960 samples = 20ms at 48kHz
        for (int i = 0; i < 960; i++)
        {
            short sample = 1000; // constant
            pcm48k[i * 2] = (byte)(sample & 0xFF);
            pcm48k[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var result = AudioResampler.Resample48kTo8k(pcm48k);
        // All output samples should be ~1000
        for (int i = 0; i < result.Length / 2; i++)
        {
            short s = (short)(result[i * 2] | (result[i * 2 + 1] << 8));
            Assert.InRange(s, 990, 1010);
        }
    }
}
