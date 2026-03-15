namespace RotaryPhoneController.GVBridge.Models;

public class GVBridgeConfig
{
    public int WebSocketPort { get; set; } = 8765;
    public string WebSocketHost { get; set; } = "127.0.0.1";
    public int LocalRtpPort { get; set; } = 5070;
    public string LocalIp { get; set; } = "0.0.0.0";
    public string HT801Ip { get; set; } = "192.168.86.250";
    public int HT801RtpPort { get; set; } = 5004;
    public int AudioSampleRateHz { get; set; } = 16000;
    public int AudioChannels { get; set; } = 1;
    public int PcmFrameMs { get; set; } = 20;
    public int ExtensionConnectTimeoutSeconds { get; set; } = 30;
    public string CallLogDbPath { get; set; } = "data/gvbridge-calllog.db";
    public string DefaultMode { get; set; } = "GVBrowser";
}
