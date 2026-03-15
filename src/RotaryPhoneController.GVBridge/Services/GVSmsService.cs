using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVBridge.Services;

public class GVSmsService : ISmsProvider
{
    private readonly GVBridgeService _bridgeService;
    private readonly ILogger<GVSmsService> _logger;
    private readonly List<SmsNotification> _recentNotifications = new();
    private readonly object _lock = new();

    public event Action<SmsNotification>? OnSmsReceived;
    public event Action<SmsNotification>? OnMissedCallReceived;

    public GVSmsService(GVBridgeService bridgeService, ILogger<GVSmsService> logger)
    {
        _bridgeService = bridgeService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _bridgeService.OnSmsReceived += HandleSmsReceived;
        _bridgeService.OnMissedCall += HandleMissedCall;
        _logger.LogInformation("GVSmsService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVSmsService stopped");
        return Task.CompletedTask;
    }

    public async Task SendSmsAsync(string toNumber, string body)
    {
        await _bridgeService.SendMessageAsync(new SendSmsMessage { To = toNumber, Body = body });
        _logger.LogInformation("SMS sent to {To}", toNumber);
    }

    public IReadOnlyList<SmsNotification> GetRecent(int count = 20)
    {
        lock (_lock)
        {
            return _recentNotifications.TakeLast(count).ToList();
        }
    }

    private void HandleSmsReceived(SmsReceivedMessage msg)
    {
        var notification = new SmsNotification(msg.From, msg.Body, DateTime.UtcNow, SmsType.Sms);
        AddNotification(notification);
        OnSmsReceived?.Invoke(notification);
    }

    private void HandleMissedCall(MissedCallMessage msg)
    {
        var notification = new SmsNotification(msg.From, null, DateTime.UtcNow, SmsType.MissedCall);
        AddNotification(notification);
        OnMissedCallReceived?.Invoke(notification);
    }

    private void AddNotification(SmsNotification notification)
    {
        lock (_lock)
        {
            _recentNotifications.Add(notification);
            if (_recentNotifications.Count > 50)
                _recentNotifications.RemoveAt(0);
        }
    }
}
