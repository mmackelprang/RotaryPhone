using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Manages cookie lifecycle for the GV API adapter: status queries,
/// saving new cookies, and triggering adapter reload.
/// </summary>
public interface IGvCookieManager
{
  Task<bool> SetCookiesAsync(GvCookieSet cookies, CancellationToken ct = default);
  GvCookieStatusDto GetStatus();
}

public class GvCookieManager : IGvCookieManager
{
  private readonly GVBridgeConfig _config;
  private readonly GVApiAdapter _adapter;
  private readonly ICallAdapterRegistry _registry;
  private readonly ILogger<GvCookieManager> _logger;

  public GvCookieManager(
    IOptions<GVBridgeConfig> config,
    GVApiAdapter adapter,
    ICallAdapterRegistry registry,
    ILogger<GvCookieManager> logger)
  {
    _config = config.Value;
    _adapter = adapter;
    _registry = registry;
    _logger = logger;
  }

  public GvCookieStatusDto GetStatus()
  {
    var cookieFileExists = File.Exists(_config.CookieFilePath);
    var currentCookies = _adapter.CurrentCookieSet;
    int? cookieCount = null;
    string? sapisidPrefix = null;

    if (currentCookies != null)
    {
      // Count cookies from RawCookieHeader if present, otherwise count individual fields
      if (!string.IsNullOrEmpty(currentCookies.RawCookieHeader))
      {
        cookieCount = currentCookies.RawCookieHeader
          .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Length;
      }
      else
      {
        cookieCount = 5; // Sapisid, Sid, Hsid, Ssid, Apisid (always present)
        if (currentCookies.Secure1Psid != null) cookieCount++;
        if (currentCookies.Secure3Psid != null) cookieCount++;
      }

      if (!string.IsNullOrEmpty(currentCookies.Sapisid))
      {
        sapisidPrefix = currentCookies.Sapisid.Length > 8
          ? currentCookies.Sapisid[..8]
          : currentCookies.Sapisid;
      }
    }

    return new GvCookieStatusDto(
      CookiesPresent: cookieFileExists,
      CookiesValid: _adapter.AreCookiesValid,
      LastValidatedAt: _adapter.LastValidatedAt,
      LoadedAt: _adapter.LoadedAt,
      CookieCount: cookieCount,
      SapisidPrefix: sapisidPrefix);
  }

  public async Task<bool> SetCookiesAsync(GvCookieSet cookies, CancellationToken ct = default)
  {
    // HOT PATH — the adapter is live and holding credentials that may still be good. Prove the incoming
    // set before it is allowed anywhere near disk. The box-side cron drives this every 20 minutes, and
    // from 2026-09-06 to 2026-09-08 it spent two days overwriting a working set with a dead one and
    // reporting success, because the old code saved first and returned true if nothing threw.
    // Both are set together in ActivateCoreAsync and cleared together in teardown, so this is one
    // condition expressed twice — checked explicitly anyway, because TryAdoptAndPersistCookiesAsync
    // returns false for "never activated" as well as for "rejected", and conflating those two would
    // send a cold start down the hot path and silently refuse to seed.
    if (_adapter.CurrentCookieSet != null && _adapter.CookieStore != null)
    {
      var adopted = await _adapter.TryAdoptAndPersistCookiesAsync(cookies, "refresh-from-browser", ct);
      if (!adopted)
      {
        _logger.LogWarning(
          "Rejected an incoming cookie set: it failed a live health probe. Existing credentials kept, "
          + "{Path} not overwritten.", _config.CookieFilePath);
      }
      return adopted;
    }

    // COLD PATH — no validated credentials exist to protect (first boot, or the adapter never activated).
    // Saving an unproven set is acceptable here precisely because there is nothing better to lose.
    var keyBase64 = await EnsureEncryptionKeyAsync();
    var store = new GvCookieStore(_config.CookieFilePath, keyBase64);
    await store.SaveAsync(cookies);
    _logger.LogInformation(
      "Cookies saved to {Path} (cold start — no validated set to protect)", _config.CookieFilePath);

    try
    {
      await _registry.SwitchModeAsync(CallAdapterMode.GVApi, ct);
      // Report whether the cookies actually WORK, not merely that activation did not throw.
      // ActivateCoreAsync handles a failed probe with SetAvailable(false) and a plain return, so the
      // old `return true` here reported success through every dead-cookie activation.
      if (!_adapter.AreCookiesValid)
      {
        _logger.LogError(
          "GV adapter re-activated but Google rejected the new cookies — the browser session is dead. "
          + "ACTION: re-login at voice.google.com.");
        return false;
      }
      _logger.LogInformation("GV adapter re-activated with new, validated cookies");
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to re-activate GV adapter after cookie update");
      return false;
    }
  }

  /// <summary>
  /// Ensure the encryption key file exists. If not, generate a new random
  /// AES-256 key and write it to disk so GVApiAdapter.ActivateAsync can find it.
  /// </summary>
  private async Task<string> EnsureEncryptionKeyAsync()
  {
    var keyFilePath = _config.CookieKeyFilePath;

    if (!string.IsNullOrEmpty(keyFilePath) && File.Exists(keyFilePath))
    {
      var keyBytes = await File.ReadAllBytesAsync(keyFilePath);
      return Convert.ToBase64String(keyBytes);
    }

    // Fallback: check if an inline key is configured
    if (!string.IsNullOrEmpty(_config.CookieEncryptionKey))
      return _config.CookieEncryptionKey;

    // Generate a new key file
    _logger.LogInformation("No encryption key found; generating new key at {Path}", keyFilePath);
    var newKey = new byte[32];
    RandomNumberGenerator.Fill(newKey);

    var dir = Path.GetDirectoryName(keyFilePath);
    if (!string.IsNullOrEmpty(dir))
      Directory.CreateDirectory(dir);

    await File.WriteAllBytesAsync(keyFilePath!, newKey);
    return Convert.ToBase64String(newKey);
  }
}
