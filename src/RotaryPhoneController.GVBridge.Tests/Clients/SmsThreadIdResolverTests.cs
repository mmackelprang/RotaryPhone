using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class SmsThreadIdResolverTests
{
    private readonly ISmsThreadIdResolver _resolver = new SmsThreadIdResolver();

    [Fact]
    public void Reply_WithExistingThreadId_UsesItVerbatim()
    {
        // ADR §4.2 #1: reply uses Google's real assigned id (may be an opaque t.<hash>).
        var id = _resolver.Resolve("+19195551234", explicitThreadId: "t.abc123hash");
        Assert.Equal("t.abc123hash", id);
    }

    [Fact]
    public void NewConversation_NullThreadId_SynthesizesTPlusE164()
    {
        // ADR §4.1 form for a NEW 1:1 thread. UNVERIFIED pending ADR §11 step 4.
        var id = _resolver.Resolve("+19195551234", explicitThreadId: null);
        Assert.Equal("t.+19195551234", id);
    }

    [Fact]
    public void NewConversation_EmptyOrWhitespaceThreadId_SynthesizesTPlusE164()
    {
        Assert.Equal("t.+19195551234", _resolver.Resolve("+19195551234", ""));
        Assert.Equal("t.+19195551234", _resolver.Resolve("+19195551234", "   "));
    }
}
