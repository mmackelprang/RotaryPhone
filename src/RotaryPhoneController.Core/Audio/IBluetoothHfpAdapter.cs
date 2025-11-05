namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Represents audio routing destination
/// </summary>
public enum AudioRoute
{
    /// <summary>
    /// Audio routed through rotary phone (handset microphone and speaker)
    /// </summary>
    RotaryPhone,
    
    /// <summary>
    /// Audio routed through cell phone (Bluetooth HFP audio)
    /// </summary>
    CellPhone
}

/// <summary>
/// Bluetooth Hands-Free Profile adapter interface
/// </summary>
public interface IBluetoothHfpAdapter
{
    /// <summary>
    /// Fired when an incoming call is detected from the mobile phone
    /// </summary>
    event Action<string>? OnIncomingCall;
    
    /// <summary>
    /// Fired when call is answered on the mobile phone device
    /// </summary>
    event Action? OnCallAnsweredOnCellPhone;
    
    /// <summary>
    /// Fired when call is terminated
    /// </summary>
    event Action? OnCallEnded;
    
    /// <summary>
    /// Fired when audio routing needs to change
    /// </summary>
    event Action<AudioRoute>? OnAudioRouteChanged;
    
    /// <summary>
    /// Initiate an outgoing call to the specified number
    /// </summary>
    /// <param name="phoneNumber">Phone number to dial</param>
    /// <returns>True if call initiation succeeded</returns>
    Task<bool> InitiateCallAsync(string phoneNumber);
    
    /// <summary>
    /// Answer an incoming call on the Bluetooth device
    /// </summary>
    /// <param name="routeAudio">Where to route the audio</param>
    /// <returns>True if call was answered successfully</returns>
    Task<bool> AnswerCallAsync(AudioRoute routeAudio);
    
    /// <summary>
    /// Terminate the current call
    /// </summary>
    /// <returns>True if call was terminated successfully</returns>
    Task<bool> TerminateCallAsync();
    
    /// <summary>
    /// Change audio routing during an active call
    /// </summary>
    /// <param name="route">New audio route</param>
    /// <returns>True if routing was changed successfully</returns>
    Task<bool> SetAudioRouteAsync(AudioRoute route);
    
    /// <summary>
    /// Check if HFP connection is active
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Get the MAC address of the connected device
    /// </summary>
    string? ConnectedDeviceAddress { get; }
}
