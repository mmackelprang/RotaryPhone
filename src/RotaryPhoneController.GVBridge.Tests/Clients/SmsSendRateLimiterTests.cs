using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class SmsSendRateLimiterTests
{
    [Fact]
    public void AllowsUpToLimit_WithinWindow()
    {
        var now = DateTime.UtcNow;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => now);
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void RejectsOverLimit_WithinWindow()
    {
        var now = DateTime.UtcNow;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => now);
        limiter.TryAcquire(); limiter.TryAcquire(); limiter.TryAcquire();
        Assert.False(limiter.TryAcquire());   // 4th in the same window → reject (→ 429)
    }

    [Fact]
    public void AllowsAgain_AfterWindowSlides()
    {
        var now = DateTime.UtcNow;
        var clock = now;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => clock);
        limiter.TryAcquire(); limiter.TryAcquire(); limiter.TryAcquire();
        Assert.False(limiter.TryAcquire());
        clock = now.AddSeconds(11);            // window has passed
        Assert.True(limiter.TryAcquire());
    }
}
