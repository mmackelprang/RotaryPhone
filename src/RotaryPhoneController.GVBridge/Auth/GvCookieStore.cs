using System.Security.Cryptography;
using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Auth;

public class GvCookieStore
{
    private readonly string _filePath;
    private readonly byte[] _key;

    public GvCookieStore(string filePath, string base64Key)
    {
        _filePath = filePath;
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (AES-256).");
    }

    public async Task SaveAsync(GvCookieJar cookies)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(cookies);
        var encrypted = Encrypt(json);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(_filePath, encrypted);
    }

    public async Task<GvCookieJar?> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(_filePath);
            var json = Decrypt(encrypted);
            return JsonSerializer.Deserialize<GvCookieJar>(json);
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);
        return result;
    }

    private byte[] Decrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = encrypted[..16];
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, 16, encrypted.Length - 16);
    }
}
