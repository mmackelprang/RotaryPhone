namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// RTP audio stream bridge interface for connecting SIP/RTP audio with Bluetooth HFP audio
/// </summary>
public interface IRtpAudioBridge
{
    /// <summary>
    /// Fired when the bridge is established and audio is flowing
    /// </summary>
    event Action? OnBridgeEstablished;
    
    /// <summary>
    /// Fired when the bridge is terminated
    /// </summary>
    event Action? OnBridgeTerminated;
    
    /// <summary>
    /// Fired when there's an error in audio bridging
    /// </summary>
    event Action<string>? OnBridgeError;
    
    /// <summary>
    /// Start bridging audio between RTP (from HT801) and Bluetooth HFP
    /// </summary>
    /// <param name="rtpEndpoint">RTP endpoint for SIP audio</param>
    /// <param name="audioRoute">Where to route the audio</param>
    /// <returns>True if bridge started successfully</returns>
    Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute audioRoute);
    
    /// <summary>
    /// Stop the audio bridge
    /// </summary>
    /// <returns>True if bridge stopped successfully</returns>
    Task<bool> StopBridgeAsync();
    
    /// <summary>
    /// Change audio routing while bridge is active
    /// </summary>
    /// <param name="newRoute">New audio route</param>
    /// <returns>True if routing changed successfully</returns>
    Task<bool> ChangeAudioRouteAsync(AudioRoute newRoute);
    
    /// <summary>
    /// Check if the bridge is currently active
    /// </summary>
    bool IsActive { get; }
    
    /// <summary>
    /// Get current audio route
    /// </summary>
    AudioRoute CurrentRoute { get; }
}
