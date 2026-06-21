namespace RotaryPhoneController.GVBridge.Sip;

/// <summary>
/// Tunable timing for keep-alive and auto-reconnect. Defaults are production values;
/// unit tests inject tiny intervals/backoff so the timing-sensitive paths run fast and
/// deterministically. Internal + injected via the test-only constructor overload
/// (see [InternalsVisibleTo] in the GVBridge csproj).
/// </summary>
internal sealed class ReconnectOptions
{
    /// <summary>
    /// Fallback keep-alive interval (seconds) when the REGISTER 200-OK Via has no
    /// usable <c>keep=</c> parameter. RFC 6223 default behavior; the bug brief's "~120s".
    /// </summary>
    public int DefaultKeepAliveSeconds { get; init; } = 120;

    /// <summary>
    /// Floor for the keep-alive send period. We ping at <c>max(KeepAliveFloorSeconds, keep/2)</c>
    /// to stay safely inside RFC 6223's 80–100% guidance while avoiding pathological tiny values.
    /// </summary>
    public int KeepAliveFloorSeconds { get; init; } = 15;

    /// <summary>
    /// Reconnect backoff schedule (seconds): capped exponential 1,2,4,8,16,30. The last
    /// entry is the cap and is reused for all further attempts. ±20% jitter is applied per step.
    /// </summary>
    public IReadOnlyList<int> BackoffScheduleSeconds { get; init; } = [1, 2, 4, 8, 16, 30];

    /// <summary>Jitter fraction applied to each backoff delay (±20%).</summary>
    public double BackoffJitterFraction { get; init; } = 0.20;

    /// <summary>
    /// REGISTER handshake timeout. Production value mirrors the original inline 10s.
    /// </summary>
    public TimeSpan RegisterTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hard floor (seconds) between the START of consecutive REGISTER attempts — a
    /// belt-and-suspenders cap so no code path (concurrent reconnect loops, a future bug)
    /// can hammer Google's SIP proxy the way the 2026-06-19 incident did (~7 REGISTERs/sec,
    /// ~600k/day). The legitimate backoff in ReconnectLoopAsync is the primary control; this
    /// is the absolute ceiling enforced at the attempt site. Tests set 0 to keep timing fast.
    /// </summary>
    public double MinRegisterIntervalSeconds { get; init; } = 2.0;

    /// <summary>
    /// Escalating cooldown (seconds) after consecutive REGISTER throttles (603 Declined / 403,
    /// or an auth-failure whose cookie refresh was a no-op). During a cooldown window NO REGISTER
    /// is attempted at all, so Google's account-level throttle can cool. The Nth consecutive
    /// throttle uses entry min(N-1, count-1); the last entry is the cap. Reset on a 200-OK REGISTER.
    ///
    /// Production default 60s -&gt; 5min -&gt; 15min -&gt; 30min cap. The 2026-06-19 incident showed a
    /// pure rate-floor (MinRegisterIntervalSeconds) only caps the RATE — it never STOPS the loop,
    /// and Google's account throttle only eases after ~10–20 min of ZERO REGISTERs. This schedule
    /// guarantees those zero-REGISTER windows. Tests inject tiny values.
    /// </summary>
    public IReadOnlyList<int> ThrottleCooldownScheduleSeconds { get; init; } = [60, 300, 900, 1800];

    public static ReconnectOptions Default { get; } = new();
}
