namespace RotaryPhoneController.GVBridge.Models;

public class GVBridgeConfig
{
    // RTP audio bridging to HT801
    public int LocalRtpPort { get; set; } = 5070;
    public string LocalIp { get; set; } = "0.0.0.0";
    public string HT801Ip { get; set; } = "192.168.86.22";
    public int HT801RtpPort { get; set; } = 5004;

    // Google Voice API
    public string GvApiBaseUrl { get; set; } = "https://clients6.google.com/voice/v1/voiceclient";
    public string GvApiKey { get; set; } = "AIzaSyDTYc1N4xiODyrQYK0Kl6g_y279LjYkrBg";

    // Cookie management
    public string CookieFilePath { get; set; } = "data/gv-cookies.enc";
    public string CookieKeyFilePath { get; set; } = "data/gv-key.bin";
    public string CookieEncryptionKey { get; set; } = "";
    public int CookieHealthCheckIntervalMinutes { get; set; } = 30;
    public int CookieRefreshIntervalMinutes { get; set; } = 5;

    // Signaler (kept for potential future use)
    public string SignalerBaseUrl { get; set; } = "https://signaler-pa.clients6.google.com";

    // Call adapter
    public string DefaultMode { get; set; } = "GVApi";
    public string CallLogDbPath { get; set; } = "data/gvbridge-calllog.db";
}
