namespace RotaryPhoneController.GVBridge.Audio;

/// <summary>
/// Simple linear interpolation resampler for 16kHz ↔ 8kHz PCM (16-bit mono).
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
}
