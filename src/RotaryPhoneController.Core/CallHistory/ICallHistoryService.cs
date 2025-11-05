namespace RotaryPhoneController.Core.CallHistory;

/// <summary>
/// Service interface for managing call history
/// </summary>
public interface ICallHistoryService
{
    /// <summary>
    /// Fired when a new call history entry is added
    /// </summary>
    event Action<CallHistoryEntry>? OnCallHistoryAdded;
    
    /// <summary>
    /// Add a new call to the history
    /// </summary>
    /// <param name="entry">Call history entry to add</param>
    void AddCallHistory(CallHistoryEntry entry);
    
    /// <summary>
    /// Update an existing call history entry
    /// </summary>
    /// <param name="entry">Call history entry to update</param>
    void UpdateCallHistory(CallHistoryEntry entry);
    
    /// <summary>
    /// Get all call history entries
    /// </summary>
    /// <param name="maxEntries">Maximum number of entries to return (0 = all)</param>
    /// <returns>List of call history entries, ordered by start time descending</returns>
    IEnumerable<CallHistoryEntry> GetCallHistory(int maxEntries = 0);
    
    /// <summary>
    /// Get call history for a specific phone
    /// </summary>
    /// <param name="phoneId">Phone ID to filter by</param>
    /// <param name="maxEntries">Maximum number of entries to return (0 = all)</param>
    /// <returns>List of call history entries for the specified phone</returns>
    IEnumerable<CallHistoryEntry> GetCallHistoryForPhone(string phoneId, int maxEntries = 0);
    
    /// <summary>
    /// Clear all call history
    /// </summary>
    void ClearHistory();
    
    /// <summary>
    /// Get total number of calls in history
    /// </summary>
    int GetCallCount();
}
