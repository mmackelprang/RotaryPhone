using System.Text.Json.Serialization;

namespace RotaryPhoneController.GVBridge.Auth;

public class GvCookieJar
{
    [JsonPropertyName("SAPISID")]
    public string Sapisid { get; set; } = "";

    [JsonPropertyName("SID")]
    public string Sid { get; set; } = "";

    [JsonPropertyName("HSID")]
    public string Hsid { get; set; } = "";

    [JsonPropertyName("SSID")]
    public string Ssid { get; set; } = "";

    [JsonPropertyName("APISID")]
    public string Apisid { get; set; } = "";

    [JsonPropertyName("__Secure-1PSID")]
    public string Secure1Psid { get; set; } = "";

    [JsonPropertyName("__Secure-3PSID")]
    public string Secure3Psid { get; set; } = "";

    public bool IsComplete =>
        !string.IsNullOrEmpty(Sapisid) &&
        !string.IsNullOrEmpty(Sid) &&
        !string.IsNullOrEmpty(Hsid) &&
        !string.IsNullOrEmpty(Ssid) &&
        !string.IsNullOrEmpty(Apisid) &&
        !string.IsNullOrEmpty(Secure1Psid) &&
        !string.IsNullOrEmpty(Secure3Psid);

    public string ToCookieHeader() =>
        $"SID={Sid}; HSID={Hsid}; SSID={Ssid}; APISID={Apisid}; SAPISID={Sapisid}; __Secure-1PSID={Secure1Psid}; __Secure-3PSID={Secure3Psid}";
}
