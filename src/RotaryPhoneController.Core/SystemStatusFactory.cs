using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Platform;

namespace RotaryPhoneController.Core;

/// <summary>
/// The ONE projection of live state into a <see cref="SystemStatus"/>, shared by both transports
/// that publish one: <c>GET /api/phone/system-status</c> and the <c>SystemStatusChanged</c> hub
/// event.
///
/// <para>
/// <b>This exists so the two transports cannot drift apart again.</b> Until 2026-09 they filled the
/// three HT801 fields from two different mechanisms with two different meanings — SignalR from a
/// 30-second background probe of the RESOLVED registrar binding, REST from a synchronous ping of the
/// CONFIGURED address stamped with DateTime.UtcNow. RadioConsole polls the REST path and derived a
/// predictive-degrade rule from it, so their input was a ping of the one address that stayed green
/// throughout the 2026-07 outage, carrying a timestamp that could never look stale.
/// </para>
///
/// <para>
/// That was fixed by pointing both call sites at the same probe cache — but as TWO code paths that
/// happened to agree, which is a property a future edit can quietly remove. One shared factory makes
/// it structural instead: there is a single place where a field's value is decided, so the two
/// payloads are identical by construction rather than by inspection. This is the code-level form of
/// the ratified decision "one probe, one meaning, whichever transport you read it over".
/// </para>
///
/// <para>
/// Static and pure on purpose. It holds nothing, decides nothing about WHEN to publish, and performs
/// no I/O — in particular it never probes. The probe result arrives as an already-taken
/// <see cref="Ht801ProbeSnapshot"/>, so a caller cannot accidentally reintroduce the synchronous
/// in-request ping this work removed.
/// </para>
/// </summary>
public static class SystemStatusFactory
{
    /// <summary>
    /// Builds the status payload from the four sources both transports read.
    /// </summary>
    /// <param name="config">Configured Bluetooth/SIP settings.</param>
    /// <param name="bluetoothAdapter">Live Bluetooth connection state.</param>
    /// <param name="sipAdapter">Live SIP listener state.</param>
    /// <param name="probe">
    /// A snapshot ALREADY read from <see cref="IHt801ReachabilityCache"/>. Callers must read the
    /// cache once into a local and pass that: reading <c>Current</c> three times could straddle a
    /// probe and build a status out of two different ones. Taking the whole record as one parameter
    /// is what makes that mistake impossible here.
    /// </param>
    public static SystemStatus Create(
        AppConfiguration config,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        Ht801ProbeSnapshot probe) =>
        new()
        {
            Platform = PlatformDetector.CurrentPlatform.ToString(),
            IsRaspberryPi = PlatformDetector.IsRaspberryPi,
            BluetoothEnabled = config.UseActualBluetoothHfp,
            BluetoothConnected = bluetoothAdapter.IsConnected,
            BluetoothDeviceAddress = bluetoothAdapter.ConnectedDeviceAddress,
            SipListening = sipAdapter.IsListening,
            SipListenAddress = config.SipListenAddress,
            SipPort = config.SipPort,

            // The three fields the convergence was about. Read from the cached background probe of
            // the RESOLVED address — never probed here, or every status broadcast would block on a
            // network timeout and every REST caller would pay a 3-second ping.
            Ht801IpAddress = probe.ProbedAddress,
            Ht801Reachable = probe.Reachable,
            Ht801LastCheckedUtc = probe.LastCheckedUtc
        };
}
