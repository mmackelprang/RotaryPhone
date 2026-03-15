namespace RotaryPhoneController.GVTrunk.Models;

public record CallLogEntry(
    int Id,
    DateTime StartedAt,
    DateTime? EndedAt,
    string Direction,
    string RemoteNumber,
    string Status,
    int? DurationSeconds,
    string? Notes = null
);
