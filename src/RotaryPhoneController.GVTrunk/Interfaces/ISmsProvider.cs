using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ISmsProvider
{
    event Action<SmsNotification> OnSmsReceived;
    event Action<SmsNotification> OnMissedCallReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendSmsAsync(string toNumber, string body) => throw new NotSupportedException("SMS sending not implemented");
}
