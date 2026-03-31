using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// AES-256-GCM authenticated encrypt/decrypt helpers.
/// Format: nonce (12 bytes) || tag (16 bytes) || ciphertext.
///
/// NOTE: Changed from AES-CBC (IV || ciphertext) to AES-GCM in March 2026.
/// Existing encrypted cookie files from the old format will be unreadable;
/// users must re-run 'gv-login' to re-encrypt with the new format.
/// </summary>
public static class TokenEncryption
{
    private const int NonceSize = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagSize = 16;   // AesGcm.TagByteSizes.MaxSize

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with AES-256-GCM using <paramref name="key"/>.
    /// A fresh random nonce is generated per call; the result is nonce || tag || ciphertext.
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintextBytes.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: nonce || tag || ciphertext
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    /// <summary>
    /// Decrypts a byte array produced by <see cref="Encrypt"/>. Extracts the nonce and
    /// authentication tag, then decrypts and verifies the ciphertext.
    /// </summary>
    public static string Decrypt(byte[] encrypted, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentNullException.ThrowIfNull(key);

        if (encrypted.Length < NonceSize + TagSize + 1)
            throw new CryptographicException("Data too short to contain nonce, tag, and ciphertext.");

        var nonce = encrypted[..NonceSize];
        var tag = encrypted[NonceSize..(NonceSize + TagSize)];
        var ciphertext = encrypted[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
