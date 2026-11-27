using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Re-runs the bridge Chrome's refresh-from-browser a few times, minutes apart, after Google rejected
/// what it handed over — so a short-lived rejection heals in minutes instead of waiting for the box
/// cron's next 20-minute fire.
/// </summary>
/// <remarks>
/// ⭐ MEASURED, not assumed. Five <c>browser_stale</c> incidents (2026-10-16, 11-13, 11-15, 11-18, 11-26)
/// each closed at the next cron fire with no sign-in — the owner confirmed none of them needed one. The
/// 11-26 one is fully journalled: refreshes passed at 19:40 and 20:00 EST, the box rebooted at 20:17, the
/// 20:20 fire was REJECTED, and the 20:40 fire passed. The 11-15 one opened at 03:16, just after the
/// weekly 03:00 reboot. So the rejection is transient and the cure is simply asking again.
/// <para>
/// ⛔ IT ONLY ASKS AGAIN. Each attempt is exactly what the cron does — extract from the bridge Chrome and
/// hand the set to <see cref="IGvCookieManager.SetCookiesAsync"/>, which proves it against Google before
/// it goes anywhere near disk. It never navigates or reloads Chrome, never signs in, and never touches a
/// session that is genuinely dead beyond the bounded attempts below. A real sign-out stays Stale (or
/// becomes SignedOut) after the chain, and the alarm escalates it exactly as before.
/// </para>
/// <para>
/// Bounded on every axis: one chain at a time (a second rejection while one runs is a no-op), a fixed
/// short list of delays, and the chain STOPS as soon as the outcome is no longer Stale for any reason —
/// the cron healed it, Chrome went away, Google is throttling us — so it never competes with another
/// path. Each attempt costs one CDP read and one live probe, the same as one cron fire.
/// </para>
/// </remarks>
public interface IStaleBrowserRetryScheduler
{
    /// <summary>
    /// Start a retry chain after a refresh-from-browser of the BRIDGE Chrome was rejected by Google.
    /// Returns true if a chain was started; false if one is already running or retries are disabled.
    /// </summary>
    bool ScheduleAfterRejection();
}

public sealed class StaleBrowserRetryScheduler : IStaleBrowserRetryScheduler, IDisposable
{
    internal const string Source = "stale-browser retry";

    private readonly GVApiAdapter _adapter;
    private readonly IGvCookieManager _cookieManager;
    private readonly ICdpCookieExtractor _extractor;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<StaleBrowserRetryScheduler> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private int _running;

    /// <summary>Test seam: the chain most recently started, so a test can await its end.</summary>
    internal Task? CurrentChain { get; private set; }

    public StaleBrowserRetryScheduler(
        GVApiAdapter adapter,
        IGvCookieManager cookieManager,
        ICdpCookieExtractor extractor,
        IOptions<GVBridgeConfig> config,
        ILogger<StaleBrowserRetryScheduler> logger)
        : this(adapter, cookieManager, extractor, config, logger, TimeProvider.System, delay: null)
    {
    }

    /// <param name="delay">Test seam for the waits between attempts. Defaults to a real
    /// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on <paramref name="time"/>; a test
    /// passes one that records the requested delay and returns at once, so the suite asserts the
    /// schedule exactly without waiting it out.</param>
    internal StaleBrowserRetryScheduler(
        GVApiAdapter adapter,
        IGvCookieManager cookieManager,
        ICdpCookieExtractor extractor,
        IOptions<GVBridgeConfig> config,
        ILogger<StaleBrowserRetryScheduler> logger,
        TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _adapter = adapter;
        _cookieManager = cookieManager;
        _extractor = extractor;
        _config = config.Value;
        _logger = logger;
        _time = time;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
    }

    /// <summary>
    /// Parse the configured delay list. Non-positive or unparseable entries are dropped (and logged by
    /// the caller as "disabled" if nothing is left) rather than thrown on: a typo in a box-side settings
    /// file must not take the endpoint that the cron hits every 20 minutes down with it.
    /// </summary>
    internal static IReadOnlyList<int> ParseDelays(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var m) ? m : 0)
                .Where(m => m > 0)
                .ToArray();

    public bool ScheduleAfterRejection()
    {
        var delays = ParseDelays(_config.StaleBrowserRetryDelaysMinutes);
        if (delays.Count == 0)
        {
            _logger.LogInformation(
                "GVApi: stale-browser retries are disabled (StaleBrowserRetryDelaysMinutes is empty) — "
                + "the next box cron fire is the only retry");
            return false;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _logger.LogDebug("GVApi: stale-browser retry chain already running; not starting another");
            return false;
        }

        _logger.LogInformation(
            "GVApi: refresh-from-browser was rejected; retrying in-process after {Delays} min",
            string.Join(", ", delays));
        CurrentChain = Task.Run(() => RunChainAsync(delays, _shutdown.Token));
        return true;
    }

    private enum Next { Continue, Stop }

    private async Task RunChainAsync(IReadOnlyList<int> delays, CancellationToken ct)
    {
        try
        {
            var elapsedMin = 0;
            for (var i = 0; i < delays.Count; i++)
            {
                await _delay(TimeSpan.FromMinutes(delays[i]), ct);
                elapsedMin += delays[i];

                if (await AttemptAsync(i + 1, delays.Count, elapsedMin, ct) == Next.Stop)
                    return;
            }

            // Deliberately WARN, not ERROR: the service is still running on its own credential, the cron
            // keeps trying every 20 minutes, and the alarm owns escalation. Log what was tested.
            _logger.LogWarning(
                "GVApi: the bridge Chrome's cookies were still REJECTED by Google after {Count} in-process "
                + "retries over {Minutes} min. The box cron keeps retrying; if this persists the session "
                + "alarm escalates it and a re-login at voice.google.com is likely needed.",
                delays.Count, elapsedMin);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // Background chain: nothing may escape as an unobserved fault.
            _logger.LogWarning(ex, "GVApi: stale-browser retry chain failed unexpectedly; the box cron remains");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<Next> AttemptAsync(int attempt, int of, int elapsedMin, CancellationToken ct)
    {
        // Something else moved the outcome — most likely the cron already healed it. Re-probing would
        // only duplicate that work, and on any non-Stale state (SignedOut, Unreachable) asking Google
        // again cannot help.
        var current = _adapter.BrowserRefreshOutcomeName;
        if (current != nameof(GVApiAdapter.BrowserRefreshOutcome.Stale))
        {
            _logger.LogInformation(
                "GVApi: stale-browser retry {Attempt}/{Of} not needed — browserRefreshOutcome is now {Outcome}",
                attempt, of, current);
            return Next.Stop;
        }

        // Never talk to Google during a 603/403 account cooldown — same rule as every other refresh path.
        if (_adapter.ThrottledUntil is { } until && until > _time.GetUtcNow().UtcDateTime)
        {
            _logger.LogInformation(
                "GVApi: stale-browser retries stopped — throttle cooldown until {Until:O}; the box cron remains",
                until);
            return Next.Stop;
        }

        var extraction = await _extractor.ExtractAsync(
            _config.ChromeCdpPort, GVApiAdapter.BridgeChromeTargetUrl, ct);

        if (!extraction.Success)
        {
            // Same status write the endpoint makes for the same failure, so the alarm sees the truth.
            _adapter.RecordFailedBrowserExtraction(extraction, Source, debounceUnreachable: true);
            _logger.LogWarning(
                "GVApi: stale-browser retry {Attempt}/{Of} could not extract from Chrome ({Status}); stopping",
                attempt, of, extraction.Status);
            return Next.Stop;
        }

        _adapter.NoteBrowserExtractionSucceeded();
        var outcome = await _cookieManager.SetCookiesAsync(extraction.Cookies!, ct);

        switch (outcome)
        {
            case SetCookiesOutcome.RejectedByGoogle:
                _logger.LogWarning(
                    "GVApi: stale-browser retry {Attempt}/{Of} (+{Minutes} min) still REJECTED by Google",
                    attempt, of, elapsedMin);
                return Next.Continue;

            case SetCookiesOutcome.Adopted:
            case SetCookiesOutcome.AdoptedButActivationFailed:
            case SetCookiesOutcome.AdoptedButNotPersisted:
                _logger.LogInformation(
                    "GVApi: stale browser session HEALED on in-process retry {Attempt}/{Of}, {Minutes} min "
                    + "after the rejection, with no sign-in ({Outcome})",
                    attempt, of, elapsedMin, outcome);
                return Next.Stop;

            default:
                _logger.LogWarning(
                    "GVApi: stale-browser retry {Attempt}/{Of} ended {Outcome}; stopping — nothing here "
                    + "tested the Google login", attempt, of, outcome);
                return Next.Stop;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
