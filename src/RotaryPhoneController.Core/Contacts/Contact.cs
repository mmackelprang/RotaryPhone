namespace RotaryPhoneController.Core.Contacts;

/// <summary>
/// Represents a contact entry
/// </summary>
public class Contact
{
    /// <summary>
    /// Unique identifier for this contact
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Contact's name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Contact's phone number (formatted)
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional email address
    /// </summary>
    public string? Email { get; set; }
    
    /// <summary>
    /// Optional notes
    /// </summary>
    public string? Notes { get; set; }
    
    /// <summary>
    /// When the contact was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When the contact was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
