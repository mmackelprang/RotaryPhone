using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvVoicemailCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly GVBridgeConfig _config;

    public GvVoicemailCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vm-cache-{Guid.NewGuid():N}");
        _config = new GVBridgeConfig
        {
            VoicemailCacheDir = _dir,
            VoicemailCacheRetentionDays = 7,
            VoicemailCacheMaxBytes = 1024
        };
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class StubFetcher(byte[] bytes) : IGvRecordingFetcher
    {
        public int Calls { get; private set; }
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new GvRecordingFetchResult(true, bytes, "audio/mpeg"));
        }
    }

    private GvVoicemailCache NewCache(IGvRecordingFetcher fetcher)
        => new(fetcher, Options.Create(_config), NullLogger<GvVoicemailCache>.Instance);

    [Fact]
    public async Task GetOrFetch_FirstCall_FetchesAndWritesFile()
    {
        var fetcher = new StubFetcher(new byte[] { 1, 2, 3, 4 });
        var cache = NewCache(fetcher);

        var path = await cache.GetOrFetchAsync("vm.1", "media-1");

        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path!));
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task GetOrFetch_SecondCall_ServesFromCacheWithoutRefetch()
    {
        var fetcher = new StubFetcher(new byte[] { 1, 2, 3, 4 });
        var cache = NewCache(fetcher);

        await cache.GetOrFetchAsync("vm.1", "media-1");
        await cache.GetOrFetchAsync("vm.1", "media-1");

        Assert.Equal(1, fetcher.Calls); // second served from disk
    }

    [Fact]
    public async Task GetOrFetch_OnFetchFailure_ReturnsNull()
    {
        var failing = new FailingFetcher();
        var cache = NewCache(failing);
        var path = await cache.GetOrFetchAsync("vm.1", "media-1");
        Assert.Null(path);
    }

    [Fact]
    public async Task Evict_RemovesOldestWhenOverMaxBytes()
    {
        // MaxBytes=1024; write three 500-byte entries → total 1500 > 1024 → oldest evicted.
        var fetcher = new StubFetcher(new byte[500]);
        var cache = NewCache(fetcher);

        var p1 = await cache.GetOrFetchAsync("vm.1", "m1");
        await Task.Delay(10);
        await cache.GetOrFetchAsync("vm.2", "m2");
        await Task.Delay(10);
        await cache.GetOrFetchAsync("vm.3", "m3");

        cache.Evict();

        Assert.False(File.Exists(p1!)); // oldest gone
    }

    [Fact]
    public async Task GetOrFetch_WithRelativeCacheDir_ReturnsAbsolutePath()
    {
        // Regression: the config default is a RELATIVE dir, but PhysicalFileResult requires a rooted
        // path and throws NotSupportedException at request time on a relative one. The cache must
        // resolve the dir to absolute so the served path is always rooted.
        var relDir = Path.Combine("data", $"vm-cache-rel-{Guid.NewGuid():N}");
        var config = new GVBridgeConfig { VoicemailCacheDir = relDir };
        var cache = new GvVoicemailCache(new StubFetcher(new byte[] { 1, 2 }),
            Options.Create(config), NullLogger<GvVoicemailCache>.Instance);
        try
        {
            var path = await cache.GetOrFetchAsync("vm.rel", "media-rel");
            Assert.NotNull(path);
            Assert.True(Path.IsPathRooted(path!), $"expected rooted path, got '{path}'");
            Assert.True(File.Exists(path!));
        }
        finally
        {
            var abs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relDir));
            if (Directory.Exists(abs)) Directory.Delete(abs, true);
        }
    }

    [Fact]
    public async Task GetOrFetch_EmptyId_DoesNotProduceBareDotBin()
    {
        var cache = NewCache(new StubFetcher(new byte[] { 9 }));
        var path = await cache.GetOrFetchAsync("", "media-x");
        Assert.NotNull(path);
        Assert.True(Path.IsPathRooted(path!));
        Assert.NotEqual(".bin", Path.GetFileName(path!));
        Assert.True(File.Exists(path!));
    }

    private sealed class FailingFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(false, null, "audio/mpeg"));
    }
}
