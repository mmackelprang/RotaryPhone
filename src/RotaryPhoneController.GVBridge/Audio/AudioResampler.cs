namespace RotaryPhoneController.GVBridge.Audio;

/// <summary>
/// Simple linear interpolation resampler for 16kHz ↔ 8kHz and 48kHz ↔ 8kHz PCM (16-bit mono).
/// G.711 telephony audio doesn't benefit from higher-quality algorithms.
/// </summary>
public static class AudioResampler
{
    public static byte[] Resample16kTo8k(byte[] pcm16k)
    {
        if (pcm16k.Length == 0) return Array.Empty<byte>();
        int sampleCount = pcm16k.Length / 2;
        int outCount = sampleCount / 2;
        var result = new byte[outCount * 2];
        for (int i = 0; i < outCount; i++)
        {
            int srcIdx = i * 2;
            short s0 = (short)(pcm16k[srcIdx * 2] | (pcm16k[srcIdx * 2 + 1] << 8));
            short s1 = (short)(pcm16k[(srcIdx + 1) * 2] | (pcm16k[(srcIdx + 1) * 2 + 1] << 8));
            short avg = (short)((s0 + s1) / 2);
            result[i * 2] = (byte)(avg & 0xFF);
            result[i * 2 + 1] = (byte)(avg >> 8);
        }
        return result;
    }

    public static byte[] Resample8kTo16k(byte[] pcm8k)
    {
        if (pcm8k.Length == 0) return Array.Empty<byte>();
        int sampleCount = pcm8k.Length / 2;
        int outCount = sampleCount * 2;
        var result = new byte[outCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            short current = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
            short next = (i + 1 < sampleCount)
                ? (short)(pcm8k[(i + 1) * 2] | (pcm8k[(i + 1) * 2 + 1] << 8))
                : current;
            short interp = (short)((current + next) / 2);
            int outIdx = i * 2;
            result[outIdx * 2] = (byte)(current & 0xFF);
            result[outIdx * 2 + 1] = (byte)(current >> 8);
            result[(outIdx + 1) * 2] = (byte)(interp & 0xFF);
            result[(outIdx + 1) * 2 + 1] = (byte)(interp >> 8);
        }
        return result;
    }

    /// <summary>
    /// Downsample 48kHz PCM to 8kHz (6:1 ratio). Averages groups of 6 samples.
    /// </summary>
    public static byte[] Resample48kTo8k(byte[] pcm48k)
    {
        int sampleCount = pcm48k.Length / 2;
        int outCount = sampleCount / 6;
        var result = new byte[outCount * 2];

        for (int i = 0; i < outCount; i++)
        {
            long sum = 0;
            for (int j = 0; j < 6; j++)
            {
                int idx = (i * 6 + j) * 2;
                if (idx + 1 < pcm48k.Length)
                    sum += (short)(pcm48k[idx] | (pcm48k[idx + 1] << 8));
            }
            short avg = (short)(sum / 6);
            result[i * 2] = (byte)(avg & 0xFF);
            result[i * 2 + 1] = (byte)((avg >> 8) & 0xFF);
        }

        return result;
    }

    /// <summary>
    /// Upsample 8kHz PCM to 48kHz (1:6 ratio). Linear interpolation between samples.
    /// </summary>
    public static byte[] Resample8kTo48k(byte[] pcm8k)
    {
        int sampleCount = pcm8k.Length / 2;
        int outCount = sampleCount * 6;
        var result = new byte[outCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            short current = (short)(pcm8k[i * 2] | (pcm8k[i * 2 + 1] << 8));
            short next = (i + 1 < sampleCount)
                ? (short)(pcm8k[(i + 1) * 2] | (pcm8k[(i + 1) * 2 + 1] << 8))
                : current;

            for (int j = 0; j < 6; j++)
            {
                short sample = (short)(current + (next - current) * j / 6);
                int outIdx = (i * 6 + j) * 2;
                result[outIdx] = (byte)(sample & 0xFF);
                result[outIdx + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        return result;
    }
}
