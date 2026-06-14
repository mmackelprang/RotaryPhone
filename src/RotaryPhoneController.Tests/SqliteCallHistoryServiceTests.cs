using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core.CallHistory;

namespace RotaryPhoneController.Tests;

public class SqliteCallHistoryServiceTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteCallHistoryService? _service;

    public SqliteCallHistoryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"call-history-test-{Guid.NewGuid():N}.db");
    }

    private SqliteCallHistoryService NewService(int max = 100) =>
        new(new Mock<ILogger<SqliteCallHistoryService>>().Object, _dbPath, max);

    [Fact]
    public void Persistence_SurvivesNewInstance_OverSameFile()
    {
        var entries = new[]
        {
            new CallHistoryEntry { PhoneNumber = "111", StartTime = DateTime.Now.AddMinutes(1) },
            new CallHistoryEntry { PhoneNumber = "222", StartTime = DateTime.Now.AddMinutes(2) },
            new CallHistoryEntry { PhoneNumber = "333", StartTime = DateTime.Now.AddMinutes(3) }
        };

        var serviceA = NewService();
        foreach (var e in entries)
            serviceA.AddCallHistory(e);
        serviceA.Dispose();

        _service = NewService();
        var read = _service.GetCallHistory().ToList();

        Assert.Equal(3, read.Count);
        var numbers = read.Select(e => e.PhoneNumber).ToHashSet();
        Assert.Contains("111", numbers);
        Assert.Contains("222", numbers);
        Assert.Contains("333", numbers);
        Assert.Equal(1, read.Count(e => e.PhoneNumber == "111"));
        Assert.Equal(1, read.Count(e => e.PhoneNumber == "222"));
        Assert.Equal(1, read.Count(e => e.PhoneNumber == "333"));
    }

    [Fact]
    public void AddCallHistory_TrimsOldestWhenOverMax()
    {
        _service = NewService(max: 3);
        var baseTime = DateTime.Now;
        for (var i = 0; i < 4; i++)
        {
            _service.AddCallHistory(new CallHistoryEntry
            {
                PhoneNumber = $"num-{i}",
                StartTime = baseTime.AddMinutes(i)
            });
        }

        var read = _service.GetCallHistory().ToList();

        Assert.Equal(3, read.Count);
        Assert.Equal(3, _service.GetCallCount());
        // Oldest (num-0) trimmed, newest (num-3) present
        var numbers = read.Select(e => e.PhoneNumber).ToHashSet();
        Assert.DoesNotContain("num-0", numbers);
        Assert.Contains("num-3", numbers);
    }

    [Fact]
    public void UpdateCallHistory_ById_ReflectedInFreshRead()
    {
        var entry = new CallHistoryEntry
        {
            PhoneNumber = "555",
            StartTime = DateTime.Now,
            AnsweredOn = CallAnsweredOn.NotAnswered
        };

        var serviceA = NewService();
        serviceA.AddCallHistory(entry);

        var endTime = entry.StartTime.AddMinutes(5);
        var updated = new CallHistoryEntry
        {
            Id = entry.Id,
            PhoneNumber = entry.PhoneNumber,
            StartTime = entry.StartTime,
            EndTime = endTime,
            AnsweredOn = CallAnsweredOn.RotaryPhone
        };
        serviceA.UpdateCallHistory(updated);
        serviceA.Dispose();

        _service = NewService();
        var read = _service.GetCallHistory().Single();

        Assert.Equal(entry.Id, read.Id);
        Assert.Equal(CallAnsweredOn.RotaryPhone, read.AnsweredOn);
        Assert.NotNull(read.EndTime);
        Assert.Equal(endTime.ToString("O"), read.EndTime!.Value.ToString("O"));
    }

    [Fact]
    public void ClearHistory_EmptiesAndGetCallCountZero()
    {
        _service = NewService();
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "1" });
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "2" });

        _service.ClearHistory();

        Assert.Equal(0, _service.GetCallCount());
        Assert.Empty(_service.GetCallHistory());
    }

    [Fact]
    public void GetCallCount_ReturnsCorrectCount()
    {
        _service = NewService();
        const int n = 5;
        for (var i = 0; i < n; i++)
            _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = $"num-{i}" });

        Assert.Equal(n, _service.GetCallCount());
    }

    [Fact]
    public void GetCallHistoryForPhone_FiltersByPhoneId()
    {
        _service = NewService();
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "1", PhoneId = "a" });
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "2", PhoneId = "a" });
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "3", PhoneId = "b" });

        var forA = _service.GetCallHistoryForPhone("a").ToList();

        Assert.Equal(2, forA.Count);
        Assert.All(forA, e => Assert.Equal("a", e.PhoneId));
    }

    [Fact]
    public void OnCallHistoryAdded_FiresOnAdd()
    {
        _service = NewService();
        CallHistoryEntry? received = null;
        _service.OnCallHistoryAdded += e => received = e;

        var entry = new CallHistoryEntry { PhoneNumber = "999" };
        _service.AddCallHistory(entry);

        Assert.NotNull(received);
        Assert.Equal(entry.Id, received!.Id);
    }

    [Fact]
    public void GetCallHistory_OrdersByStartTimeDescending()
    {
        _service = NewService();
        var baseTime = DateTime.Now;
        // Add out of order
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "mid", StartTime = baseTime.AddMinutes(2) });
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "old", StartTime = baseTime.AddMinutes(1) });
        _service.AddCallHistory(new CallHistoryEntry { PhoneNumber = "new", StartTime = baseTime.AddMinutes(3) });

        var read = _service.GetCallHistory().ToList();

        Assert.Equal(3, read.Count);
        Assert.Equal("new", read[0].PhoneNumber);
        Assert.Equal("mid", read[1].PhoneNumber);
        Assert.Equal("old", read[2].PhoneNumber);
    }

    public void Dispose()
    {
        _service?.Dispose();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup; ignore failures
            }
        }
    }
}
