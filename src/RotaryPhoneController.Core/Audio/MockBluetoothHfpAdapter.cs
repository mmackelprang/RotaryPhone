using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Mock implementation of Bluetooth HFP adapter for testing and development
/// This will be replaced with actual HFP implementation in the future
/// </summary>
public class MockBluetoothHfpAdapter : IBluetoothHfpAdapter
{
    private readonly ILogger<MockBluetoothHfpAdapter> _logger;
    private bool _isConnected = true;
    private AudioRoute _currentRoute = AudioRoute.RotaryPhone;

    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnsweredOnCellPhone;
    public event Action? OnCallEnded;
    public event Action<AudioRoute>? OnAudioRouteChanged;

    public bool IsConnected => _isConnected;
    public string? ConnectedDeviceAddress => _isConnected ? "00:11:22:33:44:55" : null;

    public MockBluetoothHfpAdapter(ILogger<MockBluetoothHfpAdapter> logger)
    {
        _logger = logger;
        _logger.LogInformation("MockBluetoothHfpAdapter initialized - simulating connected Bluetooth device");
    }

    public Task<bool> InitiateCallAsync(string phoneNumber)
    {
        _logger.LogInformation("Mock: Initiating call to {PhoneNumber} via Bluetooth HFP", phoneNumber);
        _logger.LogInformation("Mock: Call would be initiated on paired mobile phone");
        
        // In real implementation, this would use Bluetooth HFP AT commands to dial
        // For now, we simulate success
        return Task.FromResult(true);
    }

    public Task<bool> AnswerCallAsync(AudioRoute routeAudio)
    {
        _logger.LogInformation("Mock: Answering call with audio route: {Route}", routeAudio);
        _currentRoute = routeAudio;
        
        // Simulate audio routing based on where call is answered
        if (routeAudio == AudioRoute.RotaryPhone)
        {
            _logger.LogInformation("Mock: Audio routing to rotary phone (handset microphone and speaker)");
        }
        else
        {
            _logger.LogInformation("Mock: Audio routing to cell phone (Bluetooth audio)");
        }
        
        OnAudioRouteChanged?.Invoke(routeAudio);
        return Task.FromResult(true);
    }

    public Task<bool> TerminateCallAsync()
    {
        _logger.LogInformation("Mock: Terminating call via Bluetooth HFP");
        OnCallEnded?.Invoke();
        return Task.FromResult(true);
    }

    public Task<bool> SetAudioRouteAsync(AudioRoute route)
    {
        _logger.LogInformation("Mock: Changing audio route from {CurrentRoute} to {NewRoute}", 
            _currentRoute, route);
        
        _currentRoute = route;
        
        if (route == AudioRoute.RotaryPhone)
        {
            _logger.LogInformation("Mock: Audio now routed through rotary phone");
        }
        else
        {
            _logger.LogInformation("Mock: Audio now routed through cell phone");
        }
        
        OnAudioRouteChanged?.Invoke(route);
        return Task.FromResult(true);
    }

    // Methods for testing purposes
    public void SimulateIncomingCall(string phoneNumber)
    {
        _logger.LogInformation("Mock: Simulating incoming call from {PhoneNumber}", phoneNumber);
        OnIncomingCall?.Invoke(phoneNumber);
    }

    public void SimulateCallAnsweredOnCellPhone()
    {
        _logger.LogInformation("Mock: Simulating call answered on cell phone device");
        _currentRoute = AudioRoute.CellPhone;
        OnCallAnsweredOnCellPhone?.Invoke();
        OnAudioRouteChanged?.Invoke(AudioRoute.CellPhone);
    }

    public void SimulateCallEnded()
    {
        _logger.LogInformation("Mock: Simulating call ended");
        OnCallEnded?.Invoke();
    }
}
