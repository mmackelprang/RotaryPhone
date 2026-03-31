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

    public async Task SaveAsync(GvCookieSet cookies)
    {
        var json = cookies.Serialize();
        var encrypted = TokenEncryption.Encrypt(json, _key);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(_filePath, encrypted);
    }

    public async Task<GvCookieSet?> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(_filePath);
            var json = TokenEncryption.Decrypt(encrypted, _key);
            return GvCookieSet.Deserialize(json);
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
