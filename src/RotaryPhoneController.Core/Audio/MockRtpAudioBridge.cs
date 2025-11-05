using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Mock implementation of RTP audio bridge for testing and development
/// This will be replaced with actual RTP bridging implementation in the future
/// </summary>
public class MockRtpAudioBridge : IRtpAudioBridge
{
    private readonly ILogger<MockRtpAudioBridge> _logger;
    private bool _isActive = false;
    private AudioRoute _currentRoute = AudioRoute.RotaryPhone;

    public event Action? OnBridgeEstablished;
    public event Action? OnBridgeTerminated;
    public event Action<string>? OnBridgeError;

    public bool IsActive => _isActive;
    public AudioRoute CurrentRoute => _currentRoute;

    public MockRtpAudioBridge(ILogger<MockRtpAudioBridge> logger)
    {
        _logger = logger;
        _logger.LogInformation("MockRtpAudioBridge initialized");
    }

    public Task<bool> StartBridgeAsync(string rtpEndpoint, AudioRoute audioRoute)
    {
        _logger.LogInformation("Mock: Starting RTP audio bridge");
        _logger.LogInformation("Mock: RTP endpoint: {Endpoint}", rtpEndpoint);
        _logger.LogInformation("Mock: Audio route: {Route}", audioRoute);
        
        _isActive = true;
        _currentRoute = audioRoute;
        
        if (audioRoute == AudioRoute.RotaryPhone)
        {
            _logger.LogInformation("Mock: Bridging RTP audio from HT801 to rotary phone handset");
            _logger.LogInformation("Mock: Microphone from handset → RTP → Bluetooth");
            _logger.LogInformation("Mock: Bluetooth → RTP → Handset speaker");
        }
        else
        {
            _logger.LogInformation("Mock: Bridging RTP audio from HT801 to cell phone");
            _logger.LogInformation("Mock: Cell phone microphone → Bluetooth → RTP → HT801");
            _logger.LogInformation("Mock: HT801 → RTP → Bluetooth → Cell phone speaker");
        }
        
        OnBridgeEstablished?.Invoke();
        return Task.FromResult(true);
    }

    public Task<bool> StopBridgeAsync()
    {
        _logger.LogInformation("Mock: Stopping RTP audio bridge");
        _isActive = false;
        OnBridgeTerminated?.Invoke();
        return Task.FromResult(true);
    }

    public Task<bool> ChangeAudioRouteAsync(AudioRoute newRoute)
    {
        _logger.LogInformation("Mock: Changing audio route from {CurrentRoute} to {NewRoute}", 
            _currentRoute, newRoute);
        
        var oldRoute = _currentRoute;
        _currentRoute = newRoute;
        
        if (newRoute == AudioRoute.RotaryPhone)
        {
            _logger.LogInformation("Mock: Re-routing audio to rotary phone handset");
        }
        else
        {
            _logger.LogInformation("Mock: Re-routing audio to cell phone");
        }
        
        return Task.FromResult(true);
    }
}
