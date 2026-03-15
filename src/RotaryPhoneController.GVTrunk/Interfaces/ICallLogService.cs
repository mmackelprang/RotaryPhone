using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ICallLogService
{
    Task<int> AddEntryAsync(CallLogEntry entry);
    Task UpdateEntryAsync(int id, DateTime endedAt, string status, int durationSeconds);
    Task<IReadOnlyList<CallLogEntry>> GetRecentAsync(int count = 50);
}
