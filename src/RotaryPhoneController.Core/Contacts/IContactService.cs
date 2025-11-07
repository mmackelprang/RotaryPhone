namespace RotaryPhoneController.Core.Contacts;

/// <summary>
/// Service interface for managing contacts
/// </summary>
public interface IContactService
{
    /// <summary>
    /// Fired when a contact is added, updated, or deleted
    /// </summary>
    event Action? OnContactsChanged;
    
    /// <summary>
    /// Get all contacts
    /// </summary>
    /// <returns>List of contacts ordered by name</returns>
    IEnumerable<Contact> GetAllContacts();
    
    /// <summary>
    /// Get a contact by ID
    /// </summary>
    /// <param name="id">Contact ID</param>
    /// <returns>Contact if found, null otherwise</returns>
    Contact? GetContact(string id);
    
    /// <summary>
    /// Find contact by phone number (matches any digits in the number)
    /// </summary>
    /// <param name="phoneNumber">Phone number to search</param>
    /// <returns>Contact if found, null otherwise</returns>
    Contact? FindByPhoneNumber(string phoneNumber);
    
    /// <summary>
    /// Add a new contact
    /// </summary>
    /// <param name="contact">Contact to add</param>
    void AddContact(Contact contact);
    
    /// <summary>
    /// Update an existing contact
    /// </summary>
    /// <param name="contact">Contact to update</param>
    void UpdateContact(Contact contact);
    
    /// <summary>
    /// Delete a contact
    /// </summary>
    /// <param name="id">ID of contact to delete</param>
    void DeleteContact(string id);
    
    /// <summary>
    /// Search contacts by name or phone number
    /// </summary>
    /// <param name="query">Search query</param>
    /// <returns>Matching contacts</returns>
    IEnumerable<Contact> SearchContacts(string query);
    
    /// <summary>
    /// Import contacts from JSON
    /// </summary>
    /// <param name="json">JSON string containing contacts</param>
    void ImportFromJson(string json);
    
    /// <summary>
    /// Export contacts to JSON
    /// </summary>
    /// <returns>JSON string containing all contacts</returns>
    string ExportToJson();
}
