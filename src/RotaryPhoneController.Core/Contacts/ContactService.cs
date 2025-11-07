using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Contacts;

/// <summary>
/// In-memory implementation of contact service with JSON persistence
/// </summary>
public class ContactService : IContactService
{
    private readonly ILogger<ContactService> _logger;
    private readonly Dictionary<string, Contact> _contacts = new();
    private readonly object _lock = new();
    private readonly string? _storageFilePath;

    public event Action? OnContactsChanged;

    public ContactService(ILogger<ContactService> logger, string? storageFilePath = null)
    {
        _logger = logger;
        _storageFilePath = storageFilePath;
        
        if (!string.IsNullOrEmpty(_storageFilePath) && File.Exists(_storageFilePath))
        {
            LoadFromFile();
        }
        
        _logger.LogInformation("ContactService initialized with {Count} contacts", _contacts.Count);
    }

    public IEnumerable<Contact> GetAllContacts()
    {
        lock (_lock)
        {
            return _contacts.Values.OrderBy(c => c.Name).ToList();
        }
    }

    public Contact? GetContact(string id)
    {
        lock (_lock)
        {
            return _contacts.GetValueOrDefault(id);
        }
    }

    public Contact? FindByPhoneNumber(string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
            return null;
            
        // Extract digits only for comparison
        var digitsOnly = new string(phoneNumber.Where(char.IsDigit).ToArray());
        
        lock (_lock)
        {
            // Try exact match first
            var exact = _contacts.Values.FirstOrDefault(c => 
                new string(c.PhoneNumber.Where(char.IsDigit).ToArray()) == digitsOnly);
            
            if (exact != null)
                return exact;
            
            // Try suffix match (last N digits)
            if (digitsOnly.Length >= 7)
            {
                var suffix = digitsOnly.Substring(digitsOnly.Length - 7);
                return _contacts.Values.FirstOrDefault(c =>
                {
                    var contactDigits = new string(c.PhoneNumber.Where(char.IsDigit).ToArray());
                    return contactDigits.EndsWith(suffix);
                });
            }
            
            return null;
        }
    }

    public void AddContact(Contact contact)
    {
        lock (_lock)
        {
            contact.CreatedAt = DateTime.UtcNow;
            contact.ModifiedAt = DateTime.UtcNow;
            _contacts[contact.Id] = contact;
            
            _logger.LogInformation("Contact added: {Name} ({PhoneNumber})", contact.Name, contact.PhoneNumber);
            SaveToFile();
        }
        
        OnContactsChanged?.Invoke();
    }

    public void UpdateContact(Contact contact)
    {
        lock (_lock)
        {
            if (_contacts.ContainsKey(contact.Id))
            {
                contact.ModifiedAt = DateTime.UtcNow;
                _contacts[contact.Id] = contact;
                
                _logger.LogInformation("Contact updated: {Name} ({PhoneNumber})", contact.Name, contact.PhoneNumber);
                SaveToFile();
            }
            else
            {
                _logger.LogWarning("Attempted to update non-existent contact: {Id}", contact.Id);
            }
        }
        
        OnContactsChanged?.Invoke();
    }

    public void DeleteContact(string id)
    {
        lock (_lock)
        {
            if (_contacts.Remove(id, out var contact))
            {
                _logger.LogInformation("Contact deleted: {Name}", contact.Name);
                SaveToFile();
            }
        }
        
        OnContactsChanged?.Invoke();
    }

    public IEnumerable<Contact> SearchContacts(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllContacts();
            
        var lowerQuery = query.ToLowerInvariant();
        var digitsOnly = new string(query.Where(char.IsDigit).ToArray());
        
        lock (_lock)
        {
            return _contacts.Values.Where(c =>
                c.Name.ToLowerInvariant().Contains(lowerQuery) ||
                c.PhoneNumber.Contains(digitsOnly) ||
                (c.Email?.ToLowerInvariant().Contains(lowerQuery) ?? false)
            ).OrderBy(c => c.Name).ToList();
        }
    }

    public void ImportFromJson(string json)
    {
        try
        {
            var contacts = JsonSerializer.Deserialize<List<Contact>>(json);
            if (contacts != null)
            {
                lock (_lock)
                {
                    foreach (var contact in contacts)
                    {
                        _contacts[contact.Id] = contact;
                    }
                    SaveToFile();
                }
                
                _logger.LogInformation("Imported {Count} contacts from JSON", contacts.Count);
                OnContactsChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import contacts from JSON");
            throw;
        }
    }

    public string ExportToJson()
    {
        lock (_lock)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(_contacts.Values.OrderBy(c => c.Name), options);
        }
    }

    private void LoadFromFile()
    {
        if (string.IsNullOrEmpty(_storageFilePath))
            return;
            
        try
        {
            var json = File.ReadAllText(_storageFilePath);
            var contacts = JsonSerializer.Deserialize<List<Contact>>(json);
            
            if (contacts != null)
            {
                lock (_lock)
                {
                    _contacts.Clear();
                    foreach (var contact in contacts)
                    {
                        _contacts[contact.Id] = contact;
                    }
                }
                _logger.LogInformation("Loaded {Count} contacts from {Path}", contacts.Count, _storageFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load contacts from {Path}", _storageFilePath);
        }
    }

    private void SaveToFile()
    {
        if (string.IsNullOrEmpty(_storageFilePath))
            return;
            
        try
        {
            var json = ExportToJson();
            var directory = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_storageFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save contacts to {Path}", _storageFilePath);
        }
    }
}
