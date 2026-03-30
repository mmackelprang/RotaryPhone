using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// AES-256 encrypt/decrypt helpers. The IV (16 bytes) is prepended to the ciphertext.
/// </summary>
public static class TokenEncryption
{
    private const int IvSize = 16;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> with AES-256 using <paramref name="key"/>.
    /// A fresh random IV is generated per call; the result is IV || ciphertext.
    /// </summary>
    public static byte[] Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.GenerateIV();

        var iv = aes.IV;
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        var result = new byte[IvSize + encrypted.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IvSize);
        Buffer.BlockCopy(encrypted, 0, result, IvSize, encrypted.Length);
        return result;
    }

    /// <summary>
    /// Decrypts a byte array produced by <see cref="Encrypt"/>. Extracts the IV from
    /// the first 16 bytes and decrypts the remainder.
    /// </summary>
    public static string Decrypt(byte[] ciphertext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);

        if (ciphertext.Length < IvSize)
        {
            throw new CryptographicException("Ciphertext is too short to contain an IV.");
        }

        var iv = new byte[IvSize];
        Buffer.BlockCopy(ciphertext, 0, iv, 0, IvSize);

        var encryptedBytes = new byte[ciphertext.Length - IvSize];
        Buffer.BlockCopy(ciphertext, IvSize, encryptedBytes, 0, encryptedBytes.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
        return Encoding.UTF8.GetString(plaintext);
    }
}
