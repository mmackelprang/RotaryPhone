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
