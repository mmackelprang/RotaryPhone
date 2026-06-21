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
/// are part of the established contract and must not be renamed. WsConnected,
/// LastConnectedAt, and PsidtsAgeSeconds were added by the keep-alive/reconnect work
/// so the endpoint reflects real socket + cookie freshness rather than stale flags.
/// </summary>
public record GvBridgeStatusDto(
  [property: JsonPropertyName("available")] bool Available,
  [property: JsonPropertyName("activeMode")] string ActiveMode,
  [property: JsonPropertyName("sipRegistered")] bool SipRegistered,
  [property: JsonPropertyName("wsConnected")] bool WsConnected,
  [property: JsonPropertyName("lastConnectedAt")] DateTime? LastConnectedAt,
  [property: JsonPropertyName("cookiesValid")] bool CookiesValid,
  [property: JsonPropertyName("psidtsAgeSeconds")] long? PsidtsAgeSeconds,
  // Added by the registration-resilience watchdog: degraded = NOT (cookies valid AND registered);
  // lastHealthyAt = last time both held. Appended to preserve the existing field contract.
  [property: JsonPropertyName("degraded")] bool Degraded = false,
  [property: JsonPropertyName("lastHealthyAt")] DateTime? LastHealthyAt = null,
  // Added by the 603/403 throttle-cooldown fix: while a cooldown is active the transport sends
  // NO REGISTER (so Google's account-level throttle can cool). throttledUntil = when it ends;
  // throttleReason = why. Both null when not throttled. Appended to preserve the field contract.
  [property: JsonPropertyName("throttledUntil")] DateTime? ThrottledUntil = null,
  [property: JsonPropertyName("throttleReason")] string? ThrottleReason = null);

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
