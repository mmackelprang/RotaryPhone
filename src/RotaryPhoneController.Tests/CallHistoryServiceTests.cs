using Xunit;
using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core.CallHistory;

namespace RotaryPhoneController.Tests;

public class CallHistoryServiceTests
{
    private readonly Mock<ILogger<CallHistoryService>> _mockLogger;
    private readonly CallHistoryService _service;

    public CallHistoryServiceTests()
    {
        _mockLogger = new Mock<ILogger<CallHistoryService>>();
        _service = new CallHistoryService(_mockLogger.Object, maxEntries: 5);
    }

    [Fact]
    public void AddCallHistory_ShouldAddEntry()
    {
        var entry = new CallHistoryEntry
        {
            PhoneNumber = "1234567890",
            Direction = CallDirection.Outgoing,
            PhoneId = "default"
        };

        _service.AddCallHistory(entry);

        Assert.Equal(1, _service.GetCallCount());
        Assert.Contains(entry, _service.GetCallHistory());
    }

    [Fact]
    public void AddCallHistory_ShouldTrimOldEntries_WhenMaxReached()
    {
        // Add 5 entries (max)
        for (int i = 0; i < 5; i++)
        {
            _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = $"Call-{i}" });
        }
        Assert.Equal(5, _service.GetCallCount());

        // Add 6th entry
        var newEntry = new CallHistoryEntry { PhoneNumber = "Call-New" };
        _service.AddCallHistory(newEntry);

        // Should still be 5
        Assert.Equal(5, _service.GetCallCount());
        
        // Should contain new entry
        Assert.Contains(newEntry, _service.GetCallHistory());
        
        // Should NOT contain the oldest entry (Call-0)
        Assert.DoesNotContain(_service.GetCallHistory(), e => e.PhoneNumber == "Call-0");
    }

    [Fact]
    public void ClearHistory_ShouldRemoveAllEntries()
    {
        _service.AddCallHistory(new CallHistoryEntry());
        _service.AddCallHistory(new CallHistoryEntry());
        
        Assert.Equal(2, _service.GetCallCount());

        _service.ClearHistory();

        Assert.Equal(0, _service.GetCallCount());
    }
}
