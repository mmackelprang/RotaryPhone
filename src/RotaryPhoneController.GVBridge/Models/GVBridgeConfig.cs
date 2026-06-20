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

    // Chrome DevTools Protocol (CDP) for automated cookie extraction
    public int ChromeCdpPort { get; set; } = 9224;

    // Browser-less rotating-cookie (PSIDTS) refresh via accounts.google.com/RotateCookies.
    // PRIMARY 401-recovery on the headless box; falls back to CDP refresh-from-browser.
    // Enabled by default but best-effort — see GvCookieRotator TODO (request shape unconfirmed).
    public bool EnableCookieRotation { get; set; } = true;

    // Signaler (kept for potential future use)
    public string SignalerBaseUrl { get; set; } = "https://signaler-pa.clients6.google.com";

    // Google Voice phone number (E.164 format, e.g. "+19196706660")
    public string GvPhoneNumber { get; set; } = "";

    // Call adapter
    public string DefaultMode { get; set; } = "GVApi";
    public string CallLogDbPath { get; set; } = "data/gvbridge-calllog.db";

    // Voicemail audio proxy cache (ADR §6.4). Retention is OWNER-ADJUSTABLE (ADR §12 q3):
    // proposed 7 days / 200 MB, whichever first. Cache holds small MP3/AMR recordings on disk so
    // RadioConsole never talks to Google for media.
    public string VoicemailCacheDir { get; set; } = "data/gv-voicemail-cache";
    public int VoicemailCacheRetentionDays { get; set; } = 7;
    public long VoicemailCacheMaxBytes { get; set; } = 200L * 1024 * 1024; // 200 MB

    // SMS/voicemail thread poller (ADR §5.3). Adaptive interval: active vs idle, with backoff on
    // repeated failure. Owner-tunable; defaults match the ADR.
    public bool EnableThreadPoller { get; set; } = true;
    public int ThreadPollActiveSeconds { get; set; } = 15;
    public int ThreadPollIdleSeconds { get; set; } = 60;
    public int ThreadPollBackoffSeconds { get; set; } = 120;
    public int ThreadPollActiveWindowMinutes { get; set; } = 5; // "active" if a poll found new msgs within this window

    // SMS SEND FEATURE FLAG (ADR §12 #1) — DEFAULT FALSE. The account-write path ships DARK: when false,
    // POST /api/gvbridge/sms/send performs NO GV call and returns 409 send_disabled. This lets the
    // irreversible-write code merge + auto-merge safely; the owner flips this to true to go live (after the
    // ADR §11 live capture). This server-side flag is INDEPENDENT of RadioConsole's own EnableSmsSend UI
    // flag — defense in depth: BOTH must be on for a send to leave the building.
    public bool EnableSmsSend { get; set; } = false;

    // SMS send rate limit (ADR §4.2 #4). Reject more than N sends per window → HTTP 429. Owner-tunable;
    // conservative defaults for a single personal account.
    public int SmsSendMaxPerWindow { get; set; } = 5;
    public int SmsSendWindowSeconds { get; set; } = 10;
}
