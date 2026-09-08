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
/// What actually happened to a cookie set handed to <see cref="IGvCookieManager.SetCookiesAsync"/>.
/// </summary>
/// <remarks>
/// ⚠ A BOOLEAN HERE IS A LIE, and the lie has a history. <c>false</c> was returned for a stale browser
/// session, for an unrelated earlier data-plane 401 (<c>AreCookiesValid</c> is
/// <c>_areCookiesValid &amp;&amp; !AuthBlackout</c>), and for a missing encryption key or an IO error —
/// and the caller then reported all three as "Google rejected them, nothing was overwritten", which on
/// the cold path is doubly wrong because the file WAS overwritten and nothing tested the Google login.
/// That is exactly the sin Task 7 removed from the exhausted-ladder message one file over: STATE ONLY
/// WHAT WAS ACTUALLY TESTED. Each value below names a cause with a DIFFERENT operator action.
/// <para>
/// ⚠ The zero value is deliberately a FAILURE. <c>default(SetCookiesOutcome)</c> — which is what an
/// unstubbed mock returns — must never read as success.
/// </para>
/// </remarks>
public enum SetCookiesOutcome
{
  /// <summary>Re-activation threw. The cookies may be on disk; nothing here tested the Google login.</summary>
  ActivationFailed = 0,

  /// <summary>
  /// A live probe against Google refused the candidate. TESTED, not inferred — and nothing was
  /// overwritten, because the validated set on disk was never touched.
  /// </summary>
  RejectedByGoogle,

  /// <summary>
  /// Cold start: there was no validated set to protect, so the incoming set was written UNPROVEN and
  /// the file WAS overwritten. Afterwards the adapter did not report valid cookies — which may mean
  /// the new set is dead, or merely that an earlier unrelated auth failure is still in effect.
  /// </summary>
  ColdSeedUnvalidated,

  /// <summary>
  /// The cookies passed a live probe and were persisted, but re-activating the adapter failed. The
  /// refresh SUCCEEDED; the call path may still be down. Do not send the operator to re-login for this.
  /// </summary>
  AdoptedButActivationFailed,

  /// <summary>
  /// The cookies passed a live probe and are in use, but writing them to disk failed. The refresh
  /// worked; a restart will revert to the older set. Google is not the problem — the disk is.
  /// </summary>
  AdoptedButNotPersisted,

  /// <summary>Validated against Google, persisted, and in use.</summary>
  Adopted,
}

/// <summary>
/// Manages cookie lifecycle for the GV API adapter: status queries,
/// saving new cookies, and triggering adapter reload.
/// </summary>
public interface IGvCookieManager
{
  Task<SetCookiesOutcome> SetCookiesAsync(GvCookieSet cookies, CancellationToken ct = default);
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

  public async Task<SetCookiesOutcome> SetCookiesAsync(GvCookieSet cookies, CancellationToken ct = default)
  {
    // HOT PATH — the adapter is live and holding credentials that may still be good. Prove the incoming
    // set before it is allowed anywhere near disk. The box-side cron drives this every 20 minutes, and
    // from 2026-09-06 to 2026-09-08 it spent two days overwriting a working set with a dead one and
    // reporting success, because the old code saved first and returned true if nothing threw.
    // Both are set together in ActivateCoreAsync and cleared together in teardown, so this is one
    // condition expressed twice — checked explicitly anyway, because a NotActivated adoption and a
    // rejected one call for opposite handling, and conflating them would send a cold start down the
    // hot path and silently refuse to seed.
    if (_adapter.CurrentCookieSet != null && _adapter.CookieStore != null)
    {
      // ⚠ GUARDED. Nothing in here may escape as an unhandled 500: this is the endpoint the box's cron
      // hits every 20 minutes, and an unhandled fault leaves the caller with a response that describes
      // neither what was tested nor what was written.
      try
      {
        return await AdoptOnHotPathAsync(cookies, ct);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex,
          "Unexpected failure while adopting a cookie set on the hot path. Nothing here tested the "
          + "Google login — do NOT assume it is dead.");
        return SetCookiesOutcome.ActivationFailed;
      }
    }

    return await SeedOnColdPathAsync(cookies, ct);
  }

  /// <summary>
  /// The hot path: the adapter is live and holding credentials that may still be good, so the incoming
  /// set has to be proven before it is allowed anywhere near disk.
  /// </summary>
  private async Task<SetCookiesOutcome> AdoptOnHotPathAsync(GvCookieSet cookies, CancellationToken ct)
  {
      var adoption = await _adapter.TryAdoptAndPersistCookiesAsync(cookies, "refresh-from-browser", ct);

      switch (adoption)
      {
        case GVApiAdapter.CookieAdoptionOutcome.RejectedByGoogle:
          _logger.LogWarning(
            "Rejected an incoming cookie set: it failed a live health probe. Existing credentials kept, "
            + "{Path} not overwritten.", _config.CookieFilePath);
          return SetCookiesOutcome.RejectedByGoogle;

        case GVApiAdapter.CookieAdoptionOutcome.PersistFailed:
          // Deliberately does NOT re-activate. The cookies are good IN MEMORY but the disk still holds
          // the older set, and re-activation reloads FROM DISK — so it would replace proven-good
          // credentials with whatever is on disk, possibly the dead ones we were called to replace.
          _logger.LogError(
            "Cookies passed a live probe but could not be written to {Path}. They are in use now; a "
            + "restart will revert to the older set. Google is not the problem — the disk is.",
            _config.CookieFilePath);
          return SetCookiesOutcome.AdoptedButNotPersisted;

        case GVApiAdapter.CookieAdoptionOutcome.NotActivated:
          // Only reachable if a teardown raced the guard above. Seeding blindly here would overwrite
          // a set we never proved, which is the whole thing this path exists to prevent.
          _logger.LogError(
            "The adapter was torn down between the hot-path check and the adoption. Nothing was "
            + "written and nothing tested the Google login.");
          return SetCookiesOutcome.ActivationFailed;
      }

      // ⚠ THE STRANDED-ADAPTER RECOVERY. Adopting credentials is not the same as having a working
      // adapter, and this hot path never calls SwitchModeAsync — so on its own it can never rebuild
      // anything. The state that makes that fatal is reachable and is the incident's OWN recovery
      // path: a restart holding a dead PSIDTS makes ActivateCoreAsync fail its probe at step 4 and
      // return, leaving _cookieSet and _cookieStore set but NO SIP transport and NO timers. The
      // operator then re-logs into Chrome, the 20-minute cron POSTs refresh-from-browser, this branch
      // adopts the good cookies — and without the re-activation below every later cron fire repeats
      // exactly that and changes nothing. SMS and voicemail recover; CALLS NEVER DO.
      //
      // Gated on IsSipRegistered rather than on IsAvailable: a live, registered transport must not be
      // churned on the cron's cadence (that is the F6/F7 regression), while an absent or unregistered
      // one is precisely what needs rebuilding. The set on disk has already passed a live probe at
      // this point, so re-activation is loading a set we just proved.
      if (!_adapter.IsSipRegistered)
      {
        try
        {
          _logger.LogInformation(
            "Adopted new cookies but SIP is not registered — re-activating the GV adapter to rebuild "
            + "the transport and re-arm the periodic timers.");
          await _registry.SwitchModeAsync(CallAdapterMode.GVApi, ct);
        }
        catch (Exception ex)
        {
          // A failed re-activation must NOT be reported as a failed refresh: the cookies were proven
          // against Google and are safely on disk, which is what this endpoint was asked to do. Losing
          // that distinction would send the operator to re-login at voice.google.com for a fault that
          // has nothing to do with their Google session.
          _logger.LogError(ex,
            "Cookies were validated and persisted, but re-activating the GV adapter failed. SMS and "
            + "voicemail should work; CALLS WILL NOT until the adapter activates. ACTION: check the "
            + "service log above this line, then GET /api/gvbridge/status.");
          return SetCookiesOutcome.AdoptedButActivationFailed;
        }
      }

      return SetCookiesOutcome.Adopted;
  }

  /// <summary>
  /// The cold path: no validated credentials exist to protect (first boot, or the adapter never
  /// activated). Saving an unproven set is acceptable here precisely because there is nothing better
  /// to lose.
  /// </summary>
  private async Task<SetCookiesOutcome> SeedOnColdPathAsync(GvCookieSet cookies, CancellationToken ct)
  {
    // Guarded: key generation writes a file and the save writes another, so both can throw on a full
    // or read-only disk. Outside a try these escaped as an unhandled 500 — and, crucially, the seed
    // did NOT reach disk in that case, which the message has to say rather than imply the opposite.
    try
    {
      var keyBase64 = await EnsureEncryptionKeyAsync();
      var store = new GvCookieStore(_config.CookieFilePath, keyBase64);
      await store.SaveAsync(cookies);
      _logger.LogInformation(
        "Cookies saved to {Path} (cold start — no validated set to protect)", _config.CookieFilePath);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex,
        "Cold-start seed could NOT be written to {Path} (nor could its key). Nothing reached disk and "
        + "nothing tested the Google login. ACTION: check disk space and permissions.",
        _config.CookieFilePath);
      return SetCookiesOutcome.ActivationFailed;
    }

    try
    {
      await _registry.SwitchModeAsync(CallAdapterMode.GVApi, ct);
      // Report whether the cookies actually WORK, not merely that activation did not throw.
      // ActivateCoreAsync handles a failed probe with SetAvailable(false) and a plain return, so the
      // old `return true` here reported success through every dead-cookie activation.
      //
      // ⚠ But NOT "Google rejected them", which is what this used to say. AreCookiesValid is
      // `_areCookiesValid && !AuthBlackout`, so an unrelated earlier data-plane 401 makes it false even
      // when the new cookies probed perfectly — and on this path the file was ALREADY overwritten
      // several lines above. Claiming a dead browser session here sends the operator to re-login for a
      // fault that may have nothing to do with their Google session.
      if (!_adapter.AreCookiesValid)
      {
        _logger.LogError(
          "Cold-start seed written to {Path} (there was no validated set to protect), but the adapter "
          + "does NOT report valid cookies afterwards. That is either a dead incoming set OR an earlier, "
          + "unrelated auth failure still in effect — this path did not distinguish them. ACTION: read "
          + "GET /api/gvbridge/status (authBlackout, lastApiAuthFailureAt) BEFORE assuming the Google "
          + "login is dead.", _config.CookieFilePath);
        return SetCookiesOutcome.ColdSeedUnvalidated;
      }
      _logger.LogInformation("GV adapter re-activated with new, validated cookies");
      return SetCookiesOutcome.Adopted;
    }
    catch (Exception ex)
    {
      // Missing encryption key, a throwing registry, an IO error — none of which tested the Google
      // login, and all of which used to be reported as "Google rejected your cookies".
      _logger.LogError(ex,
        "Failed to re-activate GV adapter after cookie update. The seed WAS written to {Path}; nothing "
        + "here tested the Google login.", _config.CookieFilePath);
      return SetCookiesOutcome.ActivationFailed;
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
