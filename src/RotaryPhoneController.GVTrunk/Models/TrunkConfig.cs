namespace RotaryPhoneController.GVTrunk.Models;

public class TrunkConfig
{
    public string SipServer { get; set; } = "sip.voip.ms";
    public int SipPort { get; set; } = 5060;
    public string SipUsername { get; set; } = "";
    public string SipPassword { get; set; } = "";
    public int LocalSipPort { get; set; } = 5061;
    public string LocalIp { get; set; } = "0.0.0.0";
    public string GoogleVoiceForwardingNumber { get; set; } = "";
    public string OutboundCallerId { get; set; } = "";
    public int RegisterIntervalSeconds { get; set; } = 60;
    public int GmailPollIntervalSeconds { get; set; } = 30;
    public string GmailCredentialsPath { get; set; } = "";
    public string CallLogDbPath { get; set; } = "calllog.db";
}
