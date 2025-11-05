using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.CallHistory;

/// <summary>
/// In-memory implementation of call history service
/// </summary>
public class CallHistoryService : ICallHistoryService
{
    private readonly ILogger<CallHistoryService> _logger;
    private readonly List<CallHistoryEntry> _callHistory = new();
    private readonly int _maxEntries;
    private readonly object _lock = new();

    public event Action<CallHistoryEntry>? OnCallHistoryAdded;

    public CallHistoryService(ILogger<CallHistoryService> logger, int maxEntries = 100)
    {
        _logger = logger;
        _maxEntries = maxEntries;
        _logger.LogInformation("CallHistoryService initialized with max entries: {MaxEntries}", maxEntries);
    }

    public void AddCallHistory(CallHistoryEntry entry)
    {
        lock (_lock)
        {
            _callHistory.Add(entry);
            _logger.LogInformation(
                "Call history added: {Direction} call {Number} on {PhoneId} at {Time}", 
                entry.Direction, 
                entry.PhoneNumber, 
                entry.PhoneId,
                entry.StartTime);

            // Trim history if needed
            if (_maxEntries > 0 && _callHistory.Count > _maxEntries)
            {
                var toRemove = _callHistory.Count - _maxEntries;
                _callHistory.RemoveRange(0, toRemove);
                _logger.LogDebug("Trimmed {Count} old entries from call history", toRemove);
            }

            OnCallHistoryAdded?.Invoke(entry);
        }
    }

    public void UpdateCallHistory(CallHistoryEntry entry)
    {
        lock (_lock)
        {
            var existing = _callHistory.FirstOrDefault(e => e.Id == entry.Id);
            if (existing != null)
            {
                var index = _callHistory.IndexOf(existing);
                _callHistory[index] = entry;
                _logger.LogInformation(
                    "Call history updated: {Id} - Duration: {Duration}", 
                    entry.Id, 
                    entry.Duration);
            }
            else
            {
                _logger.LogWarning("Attempted to update non-existent call history entry: {Id}", entry.Id);
            }
        }
    }

    public IEnumerable<CallHistoryEntry> GetCallHistory(int maxEntries = 0)
    {
        lock (_lock)
        {
            var ordered = _callHistory.OrderByDescending(e => e.StartTime);
            return maxEntries > 0 
                ? ordered.Take(maxEntries).ToList() 
                : ordered.ToList();
        }
    }

    public IEnumerable<CallHistoryEntry> GetCallHistoryForPhone(string phoneId, int maxEntries = 0)
    {
        lock (_lock)
        {
            var filtered = _callHistory
                .Where(e => e.PhoneId == phoneId)
                .OrderByDescending(e => e.StartTime);
            
            return maxEntries > 0 
                ? filtered.Take(maxEntries).ToList() 
                : filtered.ToList();
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            var count = _callHistory.Count;
            _callHistory.Clear();
            _logger.LogInformation("Cleared {Count} entries from call history", count);
        }
    }

    public int GetCallCount()
    {
        lock (_lock)
        {
            return _callHistory.Count;
        }
    }
}
