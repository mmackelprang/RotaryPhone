namespace RotaryPhoneController.GVTrunk.Models;

public record SmsNotification(
    string FromNumber,
    string? Body,
    DateTime ReceivedAt,
    SmsType Type
);

public enum SmsType { Sms, MissedCall }
