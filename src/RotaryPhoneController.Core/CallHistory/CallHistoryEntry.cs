namespace RotaryPhoneController.Core.CallHistory;

/// <summary>
/// Represents the direction of a call
/// </summary>
public enum CallDirection
{
    /// <summary>
    /// Incoming call (from mobile to rotary phone)
    /// </summary>
    Incoming,
    
    /// <summary>
    /// Outgoing call (from rotary phone to mobile)
    /// </summary>
    Outgoing
}

/// <summary>
/// Represents where a call was answered
/// </summary>
public enum CallAnsweredOn
{
    /// <summary>
    /// Call was not answered
    /// </summary>
    NotAnswered,
    
    /// <summary>
    /// Call was answered on the rotary phone (handset lifted)
    /// </summary>
    RotaryPhone,
    
    /// <summary>
    /// Call was answered on the cell phone device
    /// </summary>
    CellPhone
}

/// <summary>
/// Represents a single call history entry
/// </summary>
public class CallHistoryEntry
{
    /// <summary>
    /// Unique identifier for this call
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Phone number involved in the call
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;
    
    /// <summary>
    /// Direction of the call
    /// </summary>
    public CallDirection Direction { get; set; }
    
    /// <summary>
    /// Where the call was answered
    /// </summary>
    public CallAnsweredOn AnsweredOn { get; set; } = CallAnsweredOn.NotAnswered;
    
    /// <summary>
    /// Timestamp when the call started (or rang)
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.Now;
    
    /// <summary>
    /// Timestamp when the call ended
    /// </summary>
    public DateTime? EndTime { get; set; }
    
    /// <summary>
    /// Duration of the call
    /// </summary>
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
    
    /// <summary>
    /// Phone ID that handled this call (for multiple phone support)
    /// </summary>
    public string PhoneId { get; set; } = "default";
}
