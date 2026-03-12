namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// G.711 mu-law (PCMU) codec for encoding/decoding telephony audio.
/// Used by both Windows (RtpAudioBridge) and Linux (PipeWireRtpAudioBridge).
/// </summary>
public static class G711Codec
{
  private static readonly short[] MuLawDecompressTable = GenerateMuLawDecompressTable();

  private static short[] GenerateMuLawDecompressTable()
  {
    var table = new short[256];
    for (int i = 0; i < 256; i++)
    {
      int sign = (i & 0x80) != 0 ? -1 : 1;
      int exponent = (i >> 4) & 0x07;
      int mantissa = i & 0x0F;
      int step = 4 << (exponent + 1);
      int value = sign * ((0x21 << exponent) + step * mantissa + step / 2 - 4 * 33);
      table[i] = (short)value;
    }
    return table;
  }

  /// <summary>
  /// Decode G.711 mu-law encoded bytes to 16-bit PCM.
  /// Output is 2x the input length (1 mu-law byte → 2 PCM bytes).
  /// </summary>
  public static byte[] DecodeMuLaw(byte[] muLawData)
  {
    var pcmData = new byte[muLawData.Length * 2];
    for (int i = 0; i < muLawData.Length; i++)
    {
      short pcmValue = MuLawDecompressTable[muLawData[i]];
      pcmData[i * 2] = (byte)(pcmValue & 0xFF);
      pcmData[i * 2 + 1] = (byte)((pcmValue >> 8) & 0xFF);
    }
    return pcmData;
  }

  /// <summary>
  /// Encode 16-bit PCM bytes to G.711 mu-law.
  /// Output is half the input length (2 PCM bytes → 1 mu-law byte).
  /// </summary>
  public static byte[] EncodeMuLaw(byte[] pcmData, int length)
  {
    var muLawData = new byte[length / 2];
    for (int i = 0; i < length / 2; i++)
    {
      short pcmValue = (short)(pcmData[i * 2] | (pcmData[i * 2 + 1] << 8));
      muLawData[i] = LinearToMuLaw(pcmValue);
    }
    return muLawData;
  }

  private static byte LinearToMuLaw(short pcm)
  {
    const int cClip = 32635;
    const int cBias = 0x84;

    int sign = (pcm < 0) ? 0x80 : 0;
    if (sign != 0)
      pcm = (short)-pcm;
    if (pcm > cClip)
      pcm = cClip;
    pcm += cBias;

    int exponent = 7;
    for (int expMask = 0x4000; (pcm & expMask) == 0 && exponent > 0; exponent--, expMask >>= 1) { }

    int mantissa = (pcm >> (exponent + 3)) & 0x0F;
    int muLaw = ~(sign | (exponent << 4) | mantissa);

    return (byte)muLaw;
  }
}
