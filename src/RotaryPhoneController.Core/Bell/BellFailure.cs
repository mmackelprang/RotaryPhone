namespace RotaryPhoneController.Core.Bell;

/// <summary>
/// Why a bell ring attempt failed. Closed enum — Radio.Web renders a fixed copy string per
/// value and treats an unrecognised value as <see cref="Unknown"/>.
/// </summary>
public enum BellFailureReason
{
  /// <summary>INVITE was sent but the HT801 never responded (no 180/200 within the diagnostic timeout).</summary>
  Timeout,
  /// <summary>The INVITE could not be put on the wire (socket error).</summary>
  Unreachable,
  /// <summary>The HT801 answered the INVITE with a 4xx/5xx/6xx.</summary>
  Rejected,
  /// <summary>The HT801 has never registered, or its registration is stale.</summary>
  NotRegistered,
  /// <summary>No usable HT801 address or SIP transport — a configuration problem, not a device problem.</summary>
  NotConfigured,
  Unknown
}

/// <summary>
/// The most recent bell-ring failure for one phone. Survives the 60-second ringing window and a
/// browser reload (served from GET /api/phone/status) — the original bug was that nobody was
/// looking at the screen during the only 60 seconds the failure was visible.
/// </summary>
public sealed record BellFailureRecord(
  DateTime OccurredAtUtc,
  BellFailureReason Reason,
  string? CallerNumber,
  string? CallId,
  string? Target,
  string? Detail,
  int FailureCount,
  bool Acknowledged);
