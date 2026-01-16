namespace RotaryPhoneController.Core;

public interface ISipAdapter
{
    event Action<bool>? OnHookChange;
    event Action<string>? OnDigitsReceived;
    event Action? OnIncomingCall;

    /// <summary>
    /// Gets whether the SIP server is currently listening for connections
    /// </summary>
    bool IsListening { get; }

    void SendInviteToHT801(string extensionToRing, string targetIP);
}
