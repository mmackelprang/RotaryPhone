using Xunit;
using RotaryPhoneController.GVTrunk.Models;
using RotaryPhoneController.GVTrunk.Services;

namespace RotaryPhoneController.GVTrunk.Tests;

public class CallLogServiceTests : IAsyncLifetime
{
    private CallLogService _service = null!;

    public async Task InitializeAsync()
    {
        _service = new CallLogService(":memory:");
        await _service.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddEntry_ReturnsPositiveId()
    {
        var entry = new CallLogEntry(0, DateTime.UtcNow, null, "Inbound", "+15551234567", "Answered", null);
        var id = await _service.AddEntryAsync(entry);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task GetRecent_ReturnsEntriesInDescendingOrder()
    {
        var now = DateTime.UtcNow;
        await _service.AddEntryAsync(new CallLogEntry(0, now.AddMinutes(-2), null, "Inbound", "+1111", "Missed", null));
        await _service.AddEntryAsync(new CallLogEntry(0, now.AddMinutes(-1), null, "Outbound", "+2222", "Answered", null));
        await _service.AddEntryAsync(new CallLogEntry(0, now, null, "Inbound", "+3333", "Answered", null));

        var recent = await _service.GetRecentAsync(10);

        Assert.Equal(3, recent.Count);
        Assert.Equal("+3333", recent[0].RemoteNumber);
        Assert.Equal("+1111", recent[2].RemoteNumber);
    }

    [Fact]
    public async Task UpdateEntry_SetsEndTimeAndDuration()
    {
        var entry = new CallLogEntry(0, DateTime.UtcNow, null, "Inbound", "+15551234567", "Answered", null);
        var id = await _service.AddEntryAsync(entry);

        var endTime = DateTime.UtcNow.AddMinutes(5);
        await _service.UpdateEntryAsync(id, endTime, "Answered", 300);

        var recent = await _service.GetRecentAsync(1);
        Assert.Equal(300, recent[0].DurationSeconds);
        Assert.NotNull(recent[0].EndedAt);
    }

    [Fact]
    public async Task GetRecent_RespectsCountLimit()
    {
        for (int i = 0; i < 10; i++)
            await _service.AddEntryAsync(new CallLogEntry(0, DateTime.UtcNow.AddMinutes(i), null, "Inbound", $"+{i}", "Missed", null));

        var recent = await _service.GetRecentAsync(3);
        Assert.Equal(3, recent.Count);
    }
}
