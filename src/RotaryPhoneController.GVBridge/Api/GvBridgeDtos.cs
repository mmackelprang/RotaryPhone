using System.Text.Json.Serialization;

namespace RotaryPhoneController.GVBridge.Api;

/// <summary>
/// Read-only status of the currently-loaded cookie set. Never exposes actual cookie values.
/// </summary>
public record GvCookieStatusDto(
  bool CookiesPresent,
  bool CookiesValid,
  DateTime? LastValidatedAt,
  DateTime? LoadedAt,
  int? CookieCount,
  string? SapisidPrefix);

/// <summary>
/// Typed response for GET /api/gvbridge/status. Serializes to camelCase JSON.
/// The first four field NAMES (available, activeMode, sipRegistered, cookiesValid)
/// are part of the established contract and must not be renamed. WsConnected and
/// LastConnectedAt were added by the keep-alive/reconnect work so the endpoint
/// reflects real socket freshness rather than stale flags.
/// </summary>
public record GvBridgeStatusDto(
  [property: JsonPropertyName("available")] bool Available,
  [property: JsonPropertyName("activeMode")] string ActiveMode,
  [property: JsonPropertyName("sipRegistered")] bool SipRegistered,
  [property: JsonPropertyName("wsConnected")] bool WsConnected,
  [property: JsonPropertyName("lastConnectedAt")] DateTime? LastConnectedAt,
  [property: JsonPropertyName("cookiesValid")] bool CookiesValid,
  // Added by the registration-resilience watchdog: degraded = NOT (cookies valid AND registered);
  // lastHealthyAt = last time both held. Appended to preserve the existing field contract.
  [property: JsonPropertyName("degraded")] bool Degraded = false,
  [property: JsonPropertyName("lastHealthyAt")] DateTime? LastHealthyAt = null,
  // Added by the 603/403 throttle-cooldown fix: while a cooldown is active the transport sends
  // NO REGISTER (so Google's account-level throttle can cool). throttledUntil = when it ends;
  // throttleReason = why. Both null when not throttled. Appended to preserve the field contract.
  [property: JsonPropertyName("throttledUntil")] DateTime? ThrottledUntil = null,
  [property: JsonPropertyName("throttleReason")] string? ThrottleReason = null,
  // Added by the B2 auth-blackout fix: honest data-plane health. cookiesValid/degraded now also
  // reflect these. authBlackout is the field RadioConsole's reconnecting banner should bind to —
  // `available` deliberately stays true (it gates GetAuthenticatedClient() internally; flipping it
  // would make the adapter refuse its own recovery retry). See spec §4.3.
  [property: JsonPropertyName("authBlackout")] bool AuthBlackout = false,
  [property: JsonPropertyName("lastApiSuccessAt")] DateTime? LastApiSuccessAt = null,
  [property: JsonPropertyName("lastApiAuthFailureAt")] DateTime? LastApiAuthFailureAt = null,
  // Added by the 2026-09-08 first-refresh-anchor work.
  //
  // psidtsMintedAtUtc REPLACES psidtsAgeSeconds, which was REMOVED on 2026-09-08 — do not restore it.
  // It was named for credential age but stamped on every cookie LOAD, not on every mint, so it reset
  // to ~0 on each restart and reload and read reassuringly low for a credential that was days old —
  // which is how a two-day Google session death went unnoticed from 2026-09-06 to 2026-09-08. It was
  // first frozen and deprecated rather than deleted because Radio Console consumed it as a published
  // blackout clock; once Radio Console retracted those bands and no consumer remained, the field was
  // removed outright so its misleading name could not re-seed the doctrine.
  //
  // A TIMESTAMP rather than an age, deliberately: an age is computed at serialisation time and is only
  // true at the instant of the response, while a mint time cannot be faked by a reload — precisely the
  // defect being corrected. Consumers derive whatever precision each surface needs with no server
  // change. NULL MEANS UNKNOWN, not fresh: a cookie file written before the field existed, a
  // hand-pasted set, or one extracted from the browser, whose jar carries no readable issue time.
  //
  // browserSessionAgeSeconds is the signal whose absence let a dead Chrome session run unnoticed for
  // two days: the service can regenerate its own PSIDTS lineage indefinitely while the browser it
  // bootstraps from is dead. browserSessionStale distinguishes "Chrome is up but signed out" from
  // "we could not reach Chrome at all".
  //
  // Appended with defaults, which is what preserves the existing field contract.
  [property: JsonPropertyName("psidtsMintedAtUtc")] DateTime? PsidtsMintedAtUtc = null,
  [property: JsonPropertyName("browserSessionValidatedAt")] DateTime? BrowserSessionValidatedAt = null,
  [property: JsonPropertyName("browserSessionAgeSeconds")] long? BrowserSessionAgeSeconds = null,
  [property: JsonPropertyName("browserSessionStale")] bool BrowserSessionStale = false);

/// <summary>
/// Payload for POST /api/gvbridge/cookies. Accepts individual fields
/// and/or a raw Cookie header from browser DevTools.
/// When RawCookieHeader is present it is preferred (most reliable).
/// </summary>
public record SetCookiesRequest(
  string? Sapisid,
  string? Sid,
  string? Hsid,
  string? Ssid,
  string? Apisid,
  string? Secure1Psid,
  string? Secure3Psid,
  string? RawCookieHeader);

/// <summary>
/// Payload for POST /api/gvbridge/cookies/refresh-from-browser.
/// All parameters are optional with sensible defaults.
/// </summary>
public record RefreshFromBrowserRequest(
  int CdpPort = 9224,
  string? TargetUrl = null);

/// <summary>
/// Response from POST /api/gvbridge/cookies/refresh-from-browser on success.
/// </summary>
public record RefreshFromBrowserResponse(
  bool Refreshed,
  int CookieCount,
  string? SapisidPrefix);
