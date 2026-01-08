using Xunit;
using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core.Contacts;

namespace RotaryPhoneController.Tests;

public class ContactServiceTests
{
    private readonly Mock<ILogger<ContactService>> _mockLogger;
    private readonly ContactService _service;

    public ContactServiceTests()
    {
        _mockLogger = new Mock<ILogger<ContactService>>();
        // Passing null for storage path to use in-memory only mode (or it will fail file ops gracefully)
        // But the service tries to load/save if path is provided.
        // Let's use a temp file for integration style testing or null for pure unit tests if supported.
        // Looking at code: if path is null/empty, it just logs errors on save but works in memory?
        // Actually code says: if (string.IsNullOrEmpty(_storageFilePath)) return; (skips save)
        // Perfect for testing!
        _service = new ContactService(_mockLogger.Object, storageFilePath: null);
    }

    [Fact]
    public void AddContact_ShouldAdd()
    {
        var contact = new Contact { Name = "Alice", PhoneNumber = "555-0001" };
        _service.AddContact(contact);

        var result = _service.GetContact(contact.Id);
        Assert.NotNull(result);
        Assert.Equal("Alice", result!.Name);
    }

    [Fact]
    public void FindByPhoneNumber_ShouldMatchExact()
    {
        var contact = new Contact { Name = "Bob", PhoneNumber = "555-1234" };
        _service.AddContact(contact);

        var found = _service.FindByPhoneNumber("555-1234");
        Assert.NotNull(found);
        Assert.Equal("Bob", found!.Name);
    }

    [Fact]
    public void FindByPhoneNumber_ShouldMatchSuffix()
    {
        var contact = new Contact { Name = "Charlie", PhoneNumber = "+1-800-555-9999" };
        _service.AddContact(contact);

        // Searching last 7 digits
        var found = _service.FindByPhoneNumber("555-9999");
        Assert.NotNull(found);
        Assert.Equal("Charlie", found!.Name);
    }

    [Fact]
    public void SearchContacts_ShouldFilterByName()
    {
        _service.AddContact(new Contact { Name = "Dave", PhoneNumber = "111" });
        _service.AddContact(new Contact { Name = "Dan", PhoneNumber = "222" });
        _service.AddContact(new Contact { Name = "Eve", PhoneNumber = "333" });

        var results = _service.SearchContacts("Da");
        Assert.Equal(2, results.Count());
        Assert.Contains(results, c => c.Name == "Dave");
        Assert.Contains(results, c => c.Name == "Dan");
        Assert.DoesNotContain(results, c => c.Name == "Eve");
    }

    [Fact]
    public void DeleteContact_ShouldRemove()
    {
        var contact = new Contact { Name = "Frank" };
        _service.AddContact(contact);
        
        Assert.NotNull(_service.GetContact(contact.Id));

        _service.DeleteContact(contact.Id);

        Assert.Null(_service.GetContact(contact.Id));
    }
}
