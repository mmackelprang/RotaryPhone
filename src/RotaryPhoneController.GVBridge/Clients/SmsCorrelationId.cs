using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Single source of truth for the outbound-SMS correlation id (id-consistency rule, PR4 Task 1 Step 1b).
/// The SAME formula is used by GvSmsController's echo AND the GvThreadPoller's outbound surface so the UI
/// collapses the optimistic bubble against the re-surfaced copy on an exact Id match. A divergence here
/// silently breaks de-dupe — so both sites call this one method. Form:
/// csid:{threadId}:{sha1(text)[..12]}:{sentEpochMs}.
/// </summary>
public static class SmsCorrelationId
{
    public static string For(string threadId, string text, long sentEpochMs)
    {
        var hash = Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(text ?? "")))
            .ToLowerInvariant()[..12];
        return $"csid:{threadId}:{hash}:{sentEpochMs}";
    }
}
