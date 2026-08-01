# Plan: B2 — GV auth blackout (refresh cadence + reactive 401 recovery + honest status)

**Status:** ✅ **Build-ready — the spec's §8 questions were all decided by the owner 2026-08-01.** No sign-off
outstanding. Binding decisions: interval **8 min**; the box-side refresh cron **stays running** through UAT
(so the in-process refresh must be idempotent); `available` stays **true** during a blackout
(`degraded`/`authBlackout` carry the fact); and the **F6/F7 re-entrancy fix stays in this PR** — see spec §8.
**Spec:** `docs/plans/gv-auth-blackout-b2-design.md` (read it first — this plan assumes its findings F1-F7)
**Branch:** `fix/gv-auth-blackout` → PR → `main`
**Base:** `origin/main` @ `627b928` — all line numbers below are anchored there.
**Test project:** `src/RotaryPhoneController.GVBridge.Tests` (xUnit + Moq; `[InternalsVisibleTo]` already
configured at `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj:15`)
**Not a BT/audio-boundary change.** Boundary doc gets an API-only Change Log + Integration Points entry.

> **Ordering constraint:** Task 0 (diagnostic) must complete before Task 4 (proactive timer) is *enabled*
> on the box. Task 3 (re-entrancy) must land before Task 4 — see spec §4.1 ground 2.

> **B1 confounder:** `fix/gv-threadid-decode` is in flight in parallel. Until it merges, every UAT step
> below uses **non-group** threads only (`t.32665`, `t.+18019208129`). Group threads (`g.Group Message.*`)
> fail for an unrelated reason and would score as false failures.

---

## Task 0 — On-box diagnostic: identify the external ~20-minute scheduler

**No code.** Read-only, bounded, run once by the Builder/Tester before Task 4 is enabled.

> **Box-health rule (non-negotiable).** `radio` is an Intel N100 with a documented correlation between
> journald/SSH churn and audio distortion. Two rules, and they are separate:
>
> 1. **No follow/streaming output.** Never `-f`, never `--follow`, never `tail -f`. Nothing that stays
>    attached to a growing file or journal.
> 2. **Every read is explicitly bounded — at the command, not by the pipe.** `journalctl` gets `--since`
>    *and* `-n`. File reads get `head -c` / `head -n`. Greps get `--include` and `-m` so they stop early
>    instead of walking a whole tree and being truncated downstream. A `| head` only truncates the
>    *output*; the expensive part has already run.
>
> A bounded, terminating `tail -n N` is not itself the hazard — but it is avoidable here, so the commands
> below use `journalctl -r … | head -n N` ("most recent N", newest first) and no `tail` appears at all.
>
> Run them in one short SSH session and disconnect.

> **Narrowed 2026-08-01 — the scheduler is already identified.** F3 is confirmed: a **box-side cron entry
> running `/opt/rotary-phone/refresh-gv-cookies.sh` every 20 minutes**. Step 1 below is now a
> *re-confirmation* that it is still present at build time, not an open hunt; the systemd-timer and
> `chrome.alarms` candidates are ruled out and their probes are retained only as cheap negative checks.
> **The cron stays running through UAT** (owner decision, spec §8.2) — this diagnostic must not remove it.
> The genuinely open question is step 4: does rung 1 (`TryRotateCookiesAsync`) rotate live, or no-op?

```bash
# 1. Re-confirm the known cron (expect: */20 → /opt/rotary-phone/refresh-gv-cookies.sh).
crontab -l 2>/dev/null | head -n 40
sudo crontab -l 2>/dev/null | head -n 40
grep -rn --include='*' -m 5 'refresh-gv-cookies\|refresh-from-browser' /etc/cron.d /etc/crontab 2>/dev/null | head -n 20

# 1b. Negative checks on the ruled-out candidates (cheap; expect no GV refresh timer).
systemctl list-timers --all --no-pager | head -n 40
systemctl --user list-timers --all --no-pager | head -n 40

# 2. The boundary doc names these box-side units/scripts (not in this repo).
#    Bound the script read directly — these are small, but do not read them unbounded.
systemctl --user status gv-bridge-watchdog gv-bridge-restart --no-pager 2>/dev/null | head -n 40
ls -la ~/bin/gv-bridge-*.sh /opt/rotary-phone/refresh-gv-cookies.sh 2>/dev/null
head -c 4000 /opt/rotary-phone/refresh-gv-cookies.sh 2>/dev/null
for f in ~/bin/gv-bridge-*.sh; do [ -f "$f" ] && { echo "--- $f ---"; head -n 60 "$f"; }; done

# 3. Ruled-out candidate, kept as a negative check. Bounded AT the grep (-m stops early,
#    --include limits the walk) rather than relying on `| head` to truncate after the fact.
grep -rn --include='*.js' --include='*.json' -m 5 \
  -e 'alarms' -e 'periodInMinutes' -e 'refresh-from-browser' \
  /opt/rotary-phone/ChromeExtension 2>/dev/null | head -n 20

# 4. THE OPEN QUESTION: cadence + RotateCookies' live success rate.
#    `-r` = newest first, so `head` gives the most recent N with no tail.
journalctl -u rotary-phone --since '-90min' -n 2000 --no-pager -r \
  | grep -E 'CDP cookie refresh|RotateCookies|adapter re-activated' | head -n 40
journalctl -u rotary-phone --since '-90min' -n 5000 --no-pager \
  | grep -c 'api2thread/list returned Unauthorized'
```

**Record in the PR description:** confirmation that the cron is still in place and its exact interval,
whether `TryRotateCookiesAsync` (rung 1) succeeds live or silently no-ops (spec §7 — its request shape is
UNVERIFIED), and the pre-fix `Unauthorized` count for the acceptance-criteria before/after.

**If the cron is no longer present**, stop and check with the owner before Task 4 — spec §8.2 assumes it is
running through UAT, and the double-refresh risk assessment changes if it is gone.

---

## Task 1 — Make the recovery ladder awaitable and outcome-reporting

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

Replace the `int _refreshingCookies` single-flight flag with a shared-task guard so a second caller
**awaits the in-flight recovery** instead of being turned away. The SIP entry point keeps its
fire-and-forget shape.

Replace the field at `:52-53`:

```csharp
    // Single-flight recovery. A second caller arriving mid-recovery AWAITS the in-flight run rather
    // than being turned away — during a blackout the poller and several RadioConsole requests hit 401
    // within milliseconds and must all ride one refresh. Guarded by _recoveryLock.
    private readonly object _recoveryLock = new();
    private Task<bool>? _recoveryTask;

    // Failure-only cooldown: after a ladder run that FAILED, suppress new runs for this long so a real
    // Google outage can't drive RotateCookies at the poll rate (the 2026-06-19 storm shape).
    private DateTime _recoveryCooldownUntilUtc = DateTime.MinValue;
```

Replace `TriggerCookieRecovery` (`:532-538`) and add the awaitable entry point:

```csharp
    /// <summary>
    /// Fire-and-forget entry into the recovery ladder (SIP transport + watchdog). Thin wrapper over
    /// <see cref="TryRecoverAuthAsync"/> so there is ONE ladder implementation, not two.
    /// </summary>
    private void TriggerCookieRecovery(string reason) => _ = TryRecoverAuthAsync(reason);

    /// <summary>
    /// Awaitable single-flight entry into the cookie-recovery ladder. Returns true when cookies were
    /// refreshed and re-validated. Concurrent callers share ONE run. Read paths await this and then
    /// retry once; the SIP path calls it fire-and-forget via <see cref="TriggerCookieRecovery"/>.
    /// </summary>
    public Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default)
    {
        lock (_recoveryLock)
        {
            if (_recoveryTask is { IsCompleted: false })
                return _recoveryTask;                       // ride the in-flight recovery

            if (DateTime.UtcNow < _recoveryCooldownUntilUtc)
                return Task.FromResult(false);              // failure cooldown active

            _recoveryTask = RecoverFromAuthFailureAsync(reason);
            return _recoveryTask;
        }
    }
```

Change `RecoverFromAuthFailureAsync` (`:540-586`) to `private async Task<bool>`: return `true` after each
successful rung (replacing the three bare `return;` statements at `:553`, `:561`, `:571`), `false` after
the all-rungs-failed warning and in the `catch`. Replace the `finally` body
(`Interlocked.Exchange(ref _refreshingCookies, 0)`) — the shared task now *is* the guard, so the `finally`
only arms the cooldown. Restructure so the outcome is visible to `finally`:

```csharp
    private async Task<bool> RecoverFromAuthFailureAsync(string reason)
    {
        var succeeded = false;
        try
        {
            // ... existing body unchanged, with each successful rung doing:
            //         succeeded = true; await ReRegisterUnlessThrottledAsync(); return true;
            //     and the all-rungs-failed tail returning false.
            return succeeded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: error during auth/registration recovery");
            return false;
        }
        finally
        {
            if (!succeeded)
            {
                _recoveryCooldownUntilUtc =
                    DateTime.UtcNow.AddSeconds(_config.AuthRecoveryFailureCooldownSeconds);
            }
        }
    }
```

Update the one remaining reader of the old flag — `RunHealthCheckAsync` (`:759`) currently tests
`Volatile.Read(ref _refreshingCookies) == 0`. Replace with a helper:

```csharp
    private bool IsRecoveryInFlight
    {
        get { lock (_recoveryLock) { return _recoveryTask is { IsCompleted: false }; } }
    }
```

**Also close the deferred PR1 HIGH-2 (arc tracker lines 108-112, open decision #6).** `GetAuthenticatedClient()`
(`:148`) gates on `IsAvailable`, so a successful rung-1 rotation leaves the seam returning `null` until the
next health tick — which would silently defeat Task 2's retry. On a successful ladder run, mark available:

```csharp
        // A successful rung means we have a live authenticated client again. Without this the
        // IsAvailable gate on GetAuthenticatedClient() (:148) keeps the seam returning null until the
        // next 30-min health tick — the PR1 review HIGH-2 window (arc tracker, open decision #6).
        if (!IsAvailable) SetAvailable(true);
```

**Config** — add to `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs` next to the cookie block:

```csharp
    // After a FAILED cookie-recovery run, suppress further runs for this long. Protects RotateCookies
    // from being driven at the poll rate during a real Google outage. 0 disables the cooldown.
    public int AuthRecoveryFailureCooldownSeconds { get; set; } = 60;
```

**Tests** — new `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterRecoveryTests.cs`:

| Test | Asserts |
|---|---|
| `TryRecoverAuthAsync_ConcurrentCallers_ShareOneRun` | Two overlapping calls return the *same* `Task<bool>`; the ladder body runs once |
| `TryRecoverAuthAsync_ReturnsTrue_WhenRungSucceeds` | Rotation succeeds → `true` |
| `TryRecoverAuthAsync_ReturnsFalse_WhenAllRungsFail` | All rungs fail → `false` |
| `TryRecoverAuthAsync_FailureArmsCooldown` | After a failure, an immediate second call returns `false` **without** re-running the ladder |
| `TryRecoverAuthAsync_SuccessDoesNotArmCooldown` | After success, a later call runs the ladder again |
| `TryRecoverAuthAsync_Success_SetsAvailable` | `IsAvailable` is `true` afterwards (the HIGH-2 fix) |
| `TriggerCookieRecovery_StillFireAndForget` | The SIP path does not block |

---

## Task 2 — Reactive refresh-and-retry on the `api2thread` read path

### 2a. Widen the seam

**File:** `src/RotaryPhoneController.GVBridge/Adapters/IGvAuthenticatedClientProvider.cs`
(the interface sits with the adapter, **not** under `Clients/` — an earlier draft of this plan mis-cited it)

```csharp
    /// <summary>
    /// Ask the adapter to refresh GV auth (the rotate → reload → CDP ladder) and report whether it
    /// worked. Concurrent callers share ONE recovery. READ paths await this and then retry once;
    /// WRITE paths (sendsms, updateread) may call it WITHOUT awaiting but MUST NOT replay the
    /// request — ADR §4.2 #4 forbids auto-retry on irreversible writes.
    /// </summary>
    Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default);
```

`GVApiAdapter` already satisfies this via Task 1 (the method is `public`). Verified: `GVApiAdapter` is the
only production implementer (`Extensions/GVBridgeServiceExtensions.cs:25-28` registers the singleton and
maps the interface to that same instance). Test fakes implementing the interface need the member added.

### 2b. Retry once, on read paths only

**File:** `src/RotaryPhoneController.GVBridge/Clients/GvThreadClient.cs`

`ListRawAsync` (`:93-142` on main) is the single shared raw read call behind threads, SMS **and**
voicemail — one patch covers the whole blackout surface.

> **Note for Builder:** this method was rewritten by PR #69 (the live-capture parser fix). The body shape
> is now **VERIFIED** as `[folder, count, 15, null, null, [null,1,1,1]]` and there is a `pageToken`
> UNVERIFIED-warning branch above the `try`. The refactor below preserves both **exactly** — it only
> moves the send into a helper so it can be attempted twice. Do not "tidy" the payload or the warning.

Restructure so the request is built once and sent at most twice:

```csharp
    public async Task<JsonDocument?> ListRawAsync(
        GvThreadFolder folder, int count, string? pageToken, CancellationToken ct = default)
    {
        // Paging is UNVERIFIED: the capture was a single un-paged request, so we know neither which
        // body position carries a page token nor whether root[2]'s version cursor is one. Rather than
        // guess a position, we ignore the token and say so — silently dropping it would make a caller
        // believe it was paging while it re-read page 1 forever.
        if (pageToken is not null)
        {
            _logger.LogWarning(
                "api2thread/list ignoring pageToken for folder {Folder} — the request body's paging " +
                "field position is UNVERIFIED (no paged capture exists). Returning the first page.",
                folder);
        }

        var url = $"{_baseUrl}/api2thread/list?alt=protojson&key={_apiKey}";
        // VERIFIED body: [folder, count, 15, null, null, [null,1,1,1]].
        // Index 2's constant 15 and the trailing flags array are sent verbatim as captured; their
        // meanings are unknown, so they are reproduced rather than reinterpreted.
        var payload = GvProtobuf.BuildArray(
            folder.ToWireValue(), count, 15, null, null,
            new object?[] { null, 1, 1, 1 });

        // Attempt 1, then — on 401/403 only, and only when provider-backed — recover and replay ONCE.
        var (doc, authFailed) = await TrySendAsync(url, payload, folder, ct);
        if (doc is not null || !authFailed || _provider is null)
            return doc;

        _logger.LogInformation(
            "api2thread/list auth-failed for folder {Folder} — recovering cookies and retrying once", folder);

        if (!await _provider.TryRecoverAuthAsync($"api2thread/list 401 ({folder})", ct))
        {
            _logger.LogWarning("api2thread/list retry skipped for folder {Folder} — recovery failed", folder);
            return null;
        }

        (doc, _) = await TrySendAsync(url, payload, folder, ct);
        return doc;
    }

    /// <summary>
    /// One attempt. Returns (document, authFailed). The client is RE-RESOLVED per attempt: recovery
    /// rungs 1 and 2 dispose and re-create the adapter's HttpClient (GVApiAdapter.cs:398, :691), so a
    /// captured instance would throw ObjectDisposedException on the retry.
    /// </summary>
    private async Task<(JsonDocument? Doc, bool AuthFailed)> TrySendAsync(
        string url, string payload, GvThreadFolder folder, CancellationToken ct)
    {
        // Resolve the live client per call when provider-backed; the test path uses the captured one.
        var http = _http ?? _provider?.GetAuthenticatedClient();
        if (http is null)
        {
            _logger.LogWarning("api2thread/list skipped — authenticated client unavailable for folder {Folder}",
                folder);
            return (null, false);
        }

        try
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("api2thread/list returned {Status} for folder {Folder}",
                    response.StatusCode, folder);
                var authFailed = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                                     or System.Net.HttpStatusCode.Forbidden;
                _provider?.RecordApiOutcome(success: false, authFailure: authFailed);   // Task 5
                return (null, authFailed);
            }
            var raw = await response.Content.ReadAsStringAsync(ct);
            _provider?.RecordApiOutcome(success: true, authFailure: false);             // Task 5
            return (JsonDocument.Parse(raw), false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "api2thread/list failed for folder {Folder}", folder);
            return (null, false);
        }
    }
```

Notes that are load-bearing and must survive review:
- The `_http`-injected **test** constructor path never retries (`_provider is null`), preserving every
  existing `GvThreadClientTests` / `GvSmsClientTests` / `GvVoicemailClientTests` fixture.
- Only `401`/`403` trigger recovery. A `429`, `5xx`, or network fault must **not** — throttling is
  falsified for this defect and replaying into a 429 is exactly the wrong move.
- Exactly **one** retry. No loop, no backoff ladder — `TryRecoverAuthAsync` already carries the
  single-flight and the failure cooldown.
- The `RecordApiOutcome` calls belong to Task 5 and are shown here so the final shape of the method is
  visible in one place; if Tasks are committed separately, add them when Task 5 lands.

### 2c. Write paths signal but never replay

**Files:** `Clients/GvSmsClient.cs` (`SendAsync`, ~`:142-162`), `Clients/GvReadStateClient.cs`
(`PostOneAsync`, ~`:108-144`).

On a `401`/`403` response only, add a **non-awaited** signal so the *next* call is healthy, and return the
existing failure outcome unchanged:

```csharp
                // Signal recovery so the NEXT call is healthy. Deliberately NOT awaited and NEVER
                // replayed: ADR §4.2 #4 forbids auto-retry on irreversible GV writes (a replayed
                // sendsms/updateread could double-write). See spec §4.2.
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                        or System.Net.HttpStatusCode.Forbidden)
                    _ = _provider?.TryRecoverAuthAsync("api2thread write 401");
```

Both methods take an explicit `HttpClient?` in their core overloads (`GvSmsClient.cs:126-127`, `GvReadStateClient.cs:108-109`; the no-auto-retry contracts are stated at `GvSmsClient.cs:26` and `GvReadStateClient.cs:24`) and so structurally *cannot* re-resolve mid-call — another reason retry
belongs only on the read path.

**Tests** — new `src/RotaryPhoneController.GVBridge.Tests/Clients/GvThreadClientAuthRetryTests.cs`, using a
stub `HttpMessageHandler` that returns 401 then 200, and a fake `IGvAuthenticatedClientProvider`:

| Test | Asserts |
|---|---|
| `ListRaw_On401_RecoversAndRetriesOnce_ReturnsData` | Recovery called once; 2 HTTP attempts; non-null document |
| `ListRaw_On401_WhenRecoveryFails_ReturnsNull_NoRetry` | Recovery called once; exactly 1 HTTP attempt |
| `ListRaw_On429_DoesNotRecoverOrRetry` | Recovery never called; 1 attempt (throttling is not this defect) |
| `ListRaw_On500_DoesNotRecoverOrRetry` | Recovery never called; 1 attempt |
| `ListRaw_On200_DoesNotRecover` | Recovery never called |
| `ListRaw_RetryReResolvesClient` | Provider's `GetAuthenticatedClient()` called **twice**; retry uses the second (post-recovery) client |
| `ListRaw_TestConstructorPath_NeverRetries` | `_http`-injected client 401s → 1 attempt, no recovery |
| `SendAsync_On401_SignalsRecovery_ButDoesNotReplay` | Recovery signalled; exactly 1 HTTP attempt |

---

## Task 3 — Make `ActivateAsync` re-entrant (F6/F7)

**Files:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

**Must land before Task 4.** Today a re-activation (which the external refresher triggers every ~20 min via
`GvCookieManager.cs:95` → `CallAdapterRegistry.cs:37`, whose `DeactivateAsync` is skipped because the mode
is unchanged) leaks the health-check timer, the `HttpClient`, and the whole `GvSipTransport`.

At the top of `ActivateAsync` (`:220`), before any component is constructed:

```csharp
        // RE-ENTRANCY (F6/F7). CallAdapterRegistry.SwitchModeAsync skips DeactivateAsync when the mode
        // is unchanged (CallAdapterRegistry.cs:37), so the external cookie refresher re-enters here on
        // the LIVE adapter every ~20 min. Without this teardown each pass leaks an armed 30-min Timer,
        // an HttpClient, and a whole GvSipTransport (WebSocket + keep-alive timer + Opus codecs) with
        // its event handlers still subscribed — ~72/day on the box. Worse, each pass re-arms a fresh
        // 30-min health timer that never reaches its due time, which is why the watchdog — the ONLY
        // timed entry into the recovery ladder — never fires in production.
        if (_sipTransport != null || _healthCheckTimer != null)
        {
            if (_activeCallId != null)
            {
                // A call is up. Adopt the new cookies (HttpClient/account client are rebuilt below)
                // but do NOT tear down the transport — that would drop the live call.
                _logger.LogInformation(
                    "GVApi: re-activating during an active call — refreshing cookies only, keeping SIP transport");
                await RefreshAuthenticatedClientsAsync(ct);
                return;
            }

            _logger.LogInformation("GVApi: re-activating — tearing down the previous generation first");
            await DeactivateAsync(ct);
        }
```

`DeactivateAsync` (`GVApiAdapter.cs:329`) already performs the correct teardown (disposes the health timer,
unsubscribes `AuthenticationFailed` at `:344`, disposes the SIP transport). Reuse it rather than
duplicating the sequence — and while here, confirm it also disposes `_httpClient` and `_rotatorHttpClient`;
add those disposals if missing.

`RefreshAuthenticatedClientsAsync` is a small new private helper extracted from the existing
`ActivateAsync` cookie/HttpClient block (`:255-275`, where `_httpClient` is built at `:260` and the probe sets `_areCookiesValid` at `:273`) so the mid-call path can reuse it verbatim rather
than duplicating construction.

**Tests** — extend `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterTests.cs`:

| Test | Asserts |
|---|---|
| `ActivateAsync_CalledTwice_DisposesPreviousTransport` | The first `GvSipTransport` is disposed; only one live transport remains |
| `ActivateAsync_CalledTwice_DoesNotLeakHealthTimer` | Exactly one armed timer after two activations |
| `ActivateAsync_DuringActiveCall_KeepsTransport` | `_activeCallId != null` → transport instance is unchanged, cookies still adopted |
| `ActivateAsync_Reentrant_StillAdoptsNewCookies` | The refresher's contract is preserved (the point of the fix) |

---

## Task 4 — Real proactive refresh governed by `CookieRefreshIntervalMinutes`

**Files:** `GVApiAdapter.cs`, `Models/GVBridgeConfig.cs`

Document the (previously dead) knob and set the spec's recommended default:

```csharp
    // Proactive PSIDTS refresh cadence. Google's rotating __Secure-*PSIDTS cookies expire ~11 minutes
    // after issue (measured 2026-07-31), so this MUST stay below that. 8 min leaves ~3 min of margin
    // without inflating RotateCookies volume. 0 DISABLES the timer (kill switch, no redeploy needed).
    // Before 2026-07-31 this value was declared but READ BY NOTHING — see spec F1.
    public int CookieRefreshIntervalMinutes { get; set; } = 8;
```

Update `src/RotaryPhoneController.Server/appsettings.json:75` to `8`.

Add the timer alongside the health-check timer in `ActivateAsync` (after `:323`, and dispose it in `DeactivateAsync` at `:329`):

```csharp
        // Proactive PSIDTS refresh (spec §4.1). Rung 1 ONLY — browser-less RotateCookies. CDP (rung 3)
        // is heavy and needs the box's Chrome; it stays reserved for reactive recovery.
        if (_config.CookieRefreshIntervalMinutes > 0)
        {
            var refreshMs = _config.CookieRefreshIntervalMinutes * 60 * 1000;
            _cookieRefreshTimer = new Timer(OnCookieRefreshTimer, null, refreshMs, refreshMs);
        }
```

Field next to `_healthCheckTimer` (`:36`): `private Timer? _cookieRefreshTimer;` — and dispose it in
`DeactivateAsync` beside the health timer (Task 3 makes that path actually run).

```csharp
    private void OnCookieRefreshTimer(object? state) => _ = RunProactiveCookieRefreshAsync();

    /// <summary>
    /// Proactive PSIDTS rotation on the CookieRefreshIntervalMinutes cadence. Deliberately narrower
    /// than the reactive ladder: rung 1 only, and NO re-register (a successful rotation does not
    /// invalidate a live SIP registration, and re-registering every 8 min would re-create the
    /// 2026-06-19 REGISTER-storm risk — spec §4.1).
    /// </summary>
    private async Task RunProactiveCookieRefreshAsync()
    {
        try
        {
            if (!IsAvailable || _cookieSet == null || !_config.EnableCookieRotation) return;

            // Never talk to Google during a 603/403 account cooldown.
            if (_sipTransport?.IsThrottled == true)
            {
                _logger.LogDebug("GVApi: proactive PSIDTS refresh skipped — throttle cooldown active");
                return;
            }

            // Share the reactive single-flight guard so a tick can never race a recovery.
            if (IsRecoveryInFlight)
            {
                _logger.LogDebug("GVApi: proactive PSIDTS refresh skipped — recovery already in flight");
                return;
            }

            if (await TryRotateCookiesAsync())
                _logger.LogInformation("GVApi: proactive PSIDTS refresh succeeded");
            else
                _logger.LogWarning(
                    "GVApi: proactive PSIDTS refresh did not rotate — reactive 401 recovery remains the backstop");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GVApi: proactive PSIDTS refresh error");
        }
    }
```

**Tests** — extend `GVApiAdapterRecoveryTests.cs`:

| Test | Asserts |
|---|---|
| `ProactiveRefresh_IntervalZero_InstallsNoTimer` | Kill switch works |
| `ProactiveRefresh_SkipsWhenThrottled` | `IsThrottled` → `TryRotateCookiesAsync` not called |
| `ProactiveRefresh_SkipsWhenRecoveryInFlight` | No race with the reactive ladder |
| `ProactiveRefresh_SkipsWhenRotationDisabled` | `EnableCookieRotation:false` → no call |
| `ProactiveRefresh_DoesNotReRegister` | SIP `EnsureRegisteredAsync` not invoked on the proactive path |
| `Config_CookieRefreshIntervalMinutes_IsRead` | Regression guard for F1 — binding the config produces the timer |

---

## Task 5 — Honest status: derive health from the last real call

**Files:** `GVApiAdapter.cs`, `Api/GvBridgeDtos.cs`, `Api/GVBridgeController.cs`

Add data-plane outcome tracking to `GVApiAdapter`, recorded from the real calls (not a probe):

```csharp
    // Data-plane truth (spec §4.3). A health field derived from a PROBE reports healthy straight
    // through an outage — the 2026-07-31 blackout reported cookiesValid:true while api2thread/list
    // was returning 401. These are written by the actual GV calls, not by threadinginfo/get.
    private DateTime? _lastApiSuccessAtUtc;
    private DateTime? _lastApiAuthFailureAtUtc;

    /// <summary>UTC of the last 2xx from a real GV data-plane call.</summary>
    public DateTime? LastApiSuccessAt => _lastApiSuccessAtUtc;

    /// <summary>UTC of the last 401/403 from a real GV data-plane call.</summary>
    public DateTime? LastApiAuthFailureAt => _lastApiAuthFailureAtUtc;

    /// <summary>
    /// True when the most recent real GV data-plane call was rejected for auth and nothing has
    /// succeeded since. This is the field RadioConsole's "Google Voice is reconnecting" banner
    /// should bind to.
    /// </summary>
    public bool AuthBlackout =>
        _lastApiAuthFailureAtUtc is { } fail &&
        (_lastApiSuccessAtUtc is not { } ok || fail > ok);

    /// <summary>Called by the GV clients after every real data-plane call. Cheap, lock-free.</summary>
    public void RecordApiOutcome(bool success, bool authFailure)
    {
        if (success) _lastApiSuccessAtUtc = DateTime.UtcNow;
        else if (authFailure) _lastApiAuthFailureAtUtc = DateTime.UtcNow;
    }
```

Add `void RecordApiOutcome(bool success, bool authFailure);` to `IGvAuthenticatedClientProvider` and call it
from `GvThreadClient.TrySendAsync` (Task 2b) on both branches.

Make `AreCookiesValid` honest (replacing `GVApiAdapter.cs:82`):

```csharp
    /// <summary>
    /// Cookies are valid only if the last PROBE passed AND no real data-plane call has since been
    /// rejected for auth. The probe alone is a 30-minute-stale reading of a DIFFERENT endpoint
    /// (threadinginfo/get) than the one that fails (api2thread/list) — spec F5.
    /// </summary>
    public bool AreCookiesValid => _areCookiesValid && !AuthBlackout;
```

`Degraded` (`:91`) already derives from `AreCookiesValid` and needs no change — it becomes honest for free.

Extend the DTO **append-only** (`Api/GvBridgeDtos.cs`, after `ThrottleReason` at `:39`) so the four contract names
and the keep-alive fields are untouched:

```csharp
  // Added by the B2 auth-blackout fix: honest data-plane health. cookiesValid/degraded now also
  // reflect these. authBlackout is the field RadioConsole's reconnecting banner should bind to —
  // `available` deliberately stays true (it gates GetAuthenticatedClient() internally; flipping it
  // would make the adapter refuse its own recovery retry). See spec §4.3.
  [property: JsonPropertyName("authBlackout")] bool AuthBlackout = false,
  [property: JsonPropertyName("lastApiSuccessAt")] DateTime? LastApiSuccessAt = null,
  [property: JsonPropertyName("lastApiAuthFailureAt")] DateTime? LastApiAuthFailureAt = null);
```

Pass them through in `GVBridgeController.GetStatus` (`Api/GVBridgeController.cs:40-56`).

**Tests** — extend `Tests/Api/GVBridgeControllerTests.cs` and `GVApiAdapterRecoveryTests.cs`:

| Test | Asserts |
|---|---|
| `GetStatus_IncludesAuthBlackoutFields` | New fields present |
| `GetStatus_ReturnsAllFourFields` (existing) | **Still passes unchanged** — contract intact |
| `GetStatus_IncludesWsConnectedAndLastConnectedAt` (existing) | **Still passes unchanged** |
| `AuthBlackout_TrueAfterAuthFailure` | `RecordApiOutcome(false, true)` → `authBlackout` true, `cookiesValid` false, `degraded` true |
| `AuthBlackout_ClearsAfterSuccess` | A later success clears it |
| `AuthBlackout_NotSetByNonAuthFailure` | `RecordApiOutcome(false, false)` (429/500) leaves it false |
| `Available_StaysTrueDuringBlackout` | The deliberate deviation is locked in by a test |

---

## Task 6 — Docs, handoff reply, and the stale plan doc

1. ~~**`docs/plans/gv-websocket-keepalive-reconnect.md`** — commit it with a `SHIPPED — PR #36` banner.~~
   ✅ **DONE — already landed in PR #71** (the planning PR), not this one. The file is committed with a
   `SHIPPED — PR #36 (ef9f2ba), resolved 2026-06-13` banner recording that its §7 OPEN QUESTIONS 1-3 were
   all resolved in the shipped code (CDP auto-refresh *is* wired as ladder rung 3; `ReconnectOptions` +
   `TimeProvider` is the test seam). **Nothing to do here** — do not re-add or re-banner it.
2. **`docs/KNOWN-ISSUES.md`** — add the B2 entry (symptom, F1-F7 root cause, fix, how to verify).
3. **`docs/plans/gv-voicemail-sms-arc.md`** — add a phase-log row for this PR; mark **open decision #6**
   (PR1 HIGH-2 auth-recovery window) resolved by Task 1; strike the matching "Deferred to Planner" bullet.
4. **`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`** — Integration Points: new `/api/gvbridge/status`
   fields. Change Log: an **API-only, no BT/audio change** entry, matching the PR5 / mark-read posture.
5. **`docs/handoffs/radioconsole-gv-auth-blackout-reply.md`** (new) — per the handoff's §Reply protocol.
   Must cover:
   - **What `CookieRefreshIntervalMinutes: 5` was actually doing: nothing.** Zero readers (F1). The
     ~20-minute cadence was an **external** caller POSTing `cookies/refresh-from-browser` (F3) — name it
     once Task 0 identifies it.
   - **The bonus finding they'll want:** F7 — the 30-minute watchdog, the only timed path into cookie
     recovery, was starved by that same external refresher and effectively never fired. Their "recovery
     tracks wall-clock, not request volume" observation is explained by this, and it is a stronger result
     than the tuning fix.
   - **`degraded`, not `available`.** We decline the literal `available:false` ask with the reason from
     spec §4.3 (`IsAvailable` gates `GetAuthenticatedClient()` internally; flipping it would make the
     adapter refuse its own recovery retry). Ask them to bind the reconnecting banner to `degraded` or the
     new `authBlackout`. **This needs their agreement — flag it prominently.**
   - Confirmation that reactive 401 refresh-and-retry now covers `api2thread` read paths, and that write
     paths signal but deliberately never replay (ADR §4.2 #4).
   - That their GV-8 remains worth shipping: ours makes the failure rare, theirs makes it honest.
   - B1 is tracked separately on `fix/gv-threadid-decode`.

---

## Test plan

### Unit / integration

```bash
dotnet test src/RotaryPhoneController.GVBridge.Tests
dotnet test src/RotaryPhoneController.Server.Tests
```

All new tests above, **plus** the existing suite green — specifically `GetStatus_ReturnsAllFourFields`,
`GetStatus_IncludesWsConnectedAndLastConnectedAt`, `GvThreadClientTests`, `GvSmsClientTests`,
`GvVoicemailClientTests`, `GvThreadPollerTests` must pass **unchanged**. Any edit to an existing assertion
is a contract break and needs review sign-off.

### Live UAT on `radio` (192.168.86.50:5004)

**Preconditions.**
- Task 0 complete. The external scheduler is already identified (cron → `/opt/rotary-phone/refresh-gv-cookies.sh`,
  every 20 min) and its disposition is decided: **it stays running through this UAT** (spec §8.2).
  ⚠️ **Do not disable or remove the cron for the soak.** Double refresh is accepted and expected here; the
  in-process refresh is required to be idempotent precisely so this configuration is safe.
- Non-group threads only until `fix/gv-threadid-decode` merges (`t.32665`, `t.+18019208129`).
- **Bounded, non-streaming journalctl only** — `--since` *and* `-n`, no `-f`/`--follow`. One short SSH
  session per step.

**Baseline (before deploy)** — establishes the before/after number:

```bash
journalctl -u rotary-phone --since '-60min' -n 5000 --no-pager \
  | grep -c 'api2thread/list returned Unauthorized'
curl -s localhost:5004/api/gvbridge/status
```

| # | Step | Pass criterion |
|---|---|---|
| 1 | **Window-blind soak (the headline test).** Poll `GET /api/gvbridge/sms/threads` and one non-group thread every 60 s for **30 minutes**, starting at an **arbitrary** wall-clock time — deliberately *not* aligned to a `CDP cookie refresh` line. Record every status code with its timestamp. | **0 × HTTP 502.** This is acceptance criterion 4: the pre-fix window-awareness discipline is retired, and its retirement is the proof. |
| 2 | **Honest status during a blackout.** If any 502 does occur, immediately `curl -s localhost:5004/api/gvbridge/status`. | `degraded:true`, `cookiesValid:false`, `authBlackout:true`, `lastApiAuthFailureAt` recent. `available` stays `true` **by design** (spec §4.3). |
| 3 | **Re-entrancy (F6/F7).** `curl -X POST localhost:5004/api/gvbridge/cookies/refresh-from-browser` twice, ~2 min apart, with no call in progress. Then bounded log read. | New cookies adopted both times (`Cookies saved` + `re-activated`); logs show `re-activating — tearing down the previous generation first`; **one** `SIP registration successful` per pass, no accumulation. |
| 4 | **Reactive recovery fires.** Bounded log read after the soak. | Where a 401 occurred, it is followed by `api2thread/list auth-failed … recovering cookies and retrying once` and then a success — not a 502 to the caller. |
| 5 | **Proactive cadence is real.** Bounded log read over 30 min. | `proactive PSIDTS refresh succeeded` appears at the configured interval (~8 min), and `CookieRefreshIntervalMinutes` demonstrably governs it (acceptance criterion 1). |
| 6 | **No storm, no REGISTER churn.** Same log window. | No `REGISTER suppressed`, no throttle-cooldown entries, no repeated connect/REGISTER cycles. `RotateCookies` call count ≈ soak minutes ÷ interval, not ≈ poll count. |
| 7 | **Kill switch.** Set `CookieRefreshIntervalMinutes: 0`, restart, bounded log read. | No proactive refresh lines; reactive path still recovers a 401. |
| 8 | **Inbound call still rings** (regression guard — Task 3 touches `_sipTransport` teardown). | Phone rings; two-way audio; `sipRegistered:true`, `wsConnected:true`. |

**After (acceptance criterion 5):**

```bash
journalctl -u rotary-phone --since '-60min' -n 5000 --no-pager \
  | grep -c 'api2thread/list returned Unauthorized'
```

Expect a drop from ~40/hour toward 0.

**Cross-service check.** Radio Console's probes stay valid and bounded:

```bash
journalctl -u radio-web --since '-60min' -n 5000 --no-pager \
  | grep -c 'Failed to get GV SMS thread'
```

---

## Risks carried from the spec

See spec §7 for the full table. The three that most need reviewer attention:

1. **Double refresh** — the box-side cron **is** left running alongside the new in-process timer, by owner
   decision (spec §8.2). This is **accepted, not mitigated away**: the obligation it creates is that the
   in-process refresh must be **idempotent** and share the single-flight guard, so the two interleave
   safely at any relative offset. `CookieRefreshIntervalMinutes: 0` remains the kill switch, and UAT
   step 6 is where the combined call volume gets checked. Reviewers should read Task 4 with this in mind.
2. **Task 3 dropping a live call** — mitigated by the `_activeCallId` guard and UAT step 8.
3. **`RotateCookies` request shape is UNVERIFIED** (`docs/research/gv-protocol-notes.md` §3.2). If Task 0
   shows rung 1 silently no-ops live, the proactive timer in Task 4 is inert and the reactive path in
   Task 2 carries the whole fix — still a correct outcome, but say so explicitly in the PR and the
   handoff reply rather than claiming a cadence fix that isn't working.
