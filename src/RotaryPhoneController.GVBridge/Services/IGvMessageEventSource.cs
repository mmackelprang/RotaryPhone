using RotaryPhoneController.GVBridge.Api;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// The stable seam through which new inbound GV messages reach RadioConsole (ADR §5.2, §6.3, §9).
/// The poller (or, later, a cracked signaler — PR6) raises these; a Server-side bridge forwards them
/// to RotaryHub as "SmsReceived"/"VoicemailReceived", mirroring "IncomingCall". Swapping the producer
/// behind this interface is invisible to RadioConsole — that is the whole point of the seam.
/// </summary>
public interface IGvMessageEventSource
{
    /// <summary>Raised once per newly-detected inbound SMS.</summary>
    event Action<SmsMessageDto>? OnSmsReceived;

    /// <summary>Raised once per newly-detected voicemail.</summary>
    event Action<VoicemailItemDto>? OnVoicemailReceived;
}
