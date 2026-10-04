# Plan: anchor the first PSIDTS refresh, and stop the cookie lineage from lying

**Status:** 🟡 **Build-ready, with two decisions flagged for the owner** (§0.3). Nothing here is
blocked on them; both are "confirm the shape we chose", not "choose before starting".
**Branch:** `fix/gv-auth-first-refresh-anchor` → PR → `main`
**Base:** `main` @ `b264c07`. **Every line number below is anchored there** and was verified against
`git show main:<path>`, not against a working tree.
**Test project:** `src/RotaryPhoneController.GVBridge.Tests` (xUnit 2.\* + Moq 4.\*, `net10.0`;
`InternalsVisibleTo` already declared at `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj:14-16`).
**Not a BT/audio-boundary change.** It *is* an API change, so it takes an "API only" Change Log row in
`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` — see Task 11.

**Incident this exists to fix:** `docs/handoffs/2026-09-08-radioconsole-incident-and-corrections.md` §1.
83 minutes of guest-facing SMS/voicemail outage on 2026-09-08, preceded by two days in which Chrome's
Google Voice session was dead and *nothing warned*.

> ⚠ **Concurrency.** A Builder is working the bell / `PhoneSystemStatus` / `BellFailureTracker` path on
> another branch, and it has `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` modified. Task 11 touches
> that file. Rebase Task 11 last and re-read the file before editing it; append a Change Log row, never
> renumber an existing rule (that exact mistake is recorded as MEDIUM-1 in `0980171`).

---

## 0. What is actually broken

### 0.1 The chain, and why a restart breaks it

The service does not depend on Chrome minute-to-minute. It mints its own PSIDTS every 8 minutes via
`RotateCookies` (rung 1), and each rotation reseeds the next. Google's PSIDTS lives **~11 minutes**
(`src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:23-27`). So the chain survives only while
it is **unbroken**, and every link must be forged before the previous one dies.

`src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs:1128`:

```csharp
_cookieRefreshTimer = new Timer(OnCookieRefreshTimer, null, refreshMs, refreshMs);
```

`dueTime == period`. In a long-running process this is **invisible**: each rotation resets the timer and
mints in the same instant, so timer and credential stay locked. **Only a restart decouples them.** A fresh
process inherits a credential of arbitrary age and then waits a *full* interval anyway. On 2026-09-08 it
inherited a 55-second-old PSIDTS, scheduled its first refresh for 8m00s out, and the credential died at
8m03s. **Missed by 52 seconds.**

This was observed and filed **five weeks earlier** and scored LOW:
`docs/KNOWN-ISSUES.md` finding **L2** — *"`psidtsAgeSeconds` resets on activation regardless of the
cookies' true issue time … it read `6` right after a restart whose on-disk PSIDTS was ~7 minutes old."*
That is this outage, pre-observed. A 7-minute-old credential with an 11-minute life, and the first
refresh 8 minutes away.

### 0.2 Why nobody saw two days of failure

`_psidtsRefreshedAt = DateTime.UtcNow` is stamped on **every load**, not on every mint. Three write sites
on `main`, and only one of them is a genuine mint:

| Line | Method | Genuine mint? |
|---|---|---|
| `GVApiAdapter.cs:397` | `ActivateCoreAsync` | ❌ stamps "now" for a set just read off disk |
| `GVApiAdapter.cs:728` | `ReloadCookiesAsync` | ❌ stamps "now" for a set just read off disk |
| `GVApiAdapter.cs:1056` | `TryRotateCookiesAsync` | ✅ |

So `psidtsAgeSeconds` reported **208** for a credential minted on Sep 6. A process cannot schedule around
an age it does not know, and an operator cannot see a staleness the field refuses to report.

### 0.3 Two decisions the owner should confirm (not blockers)

1. **An unknown-age credential is refreshed almost immediately** (5 s floor), not trusted for a full
   interval. A credential we cannot date is one we must not extend credit to. Cost: on the first deploy
   after this change every activation triggers one extra rotation, because no persisted mint time exists
   yet. That is one rotation per activation, once. Alternative rejected: treat unknown as age-zero, which
   reproduces exactly the bug being fixed.
2. **`psidtsAgeSeconds` becomes `null` when the mint time is unknown**, instead of a reassuring small
   number. See §0.4 — this is a live cross-repo contract change.

### 0.4 ⚠ This changes a field Radio Console is actively building on

`psidtsAgeSeconds` is not a private diagnostic. Radio Console uses it as a **blackout predictor** and has
published bands for it — `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md:287-301`:
`<660` healthy, `660–1200` blackout, resets to ~0 at ~1200. And RotaryPhone told them in writing that the
field *"stays exactly as it is"* (`docs/handoffs/radioconsole-gv-auth-blackout-reply.md:182`,
`docs/plans/gv-auth-blackout-b2-design.md:438`).

Read that promise precisely: it was a promise **not to promote the field into the health derivation** —
not a promise never to make it accurate. Correcting it makes their predictor *work* (today it reads 208
for a two-day-old credential, which is precisely why their predictor stayed silent through the outage).
But it does change observed values in two ways they must be told about:

- A restarted process holding an old inherited credential now reports its **true** age — which can be
  five orders of magnitude outside their `660–1200` band (~172800 for two days).
- An **unknown** mint time now reports `null` where it used to report a small number.

Task 11 owes them a handoff note. Do not ship this silently.

### 0.5 Defect 3 is in two places, and the second one is the one that ran for two days

The prompt for this work names `GVApiAdapter.cs:984-985` (recovery rung 3). That is real — it persists
before validating, and it ran once a minute for the 83 minutes of the outage.

**But it is not the path that caused the two-day blackness.** The box-side cron POSTs
`/api/gvbridge/cookies/refresh-from-browser` every 20 minutes
(`docs/plans/gv-auth-blackout-b2-plan.md` Task 0; `GVApiAdapterTests.cs:392`), which lands in
`GvCookieManager.SetCookiesAsync` — and *that* method saves unconditionally, on its first statement,
before anything is validated:

```csharp
var store = new GvCookieStore(_config.CookieFilePath, keyBase64);
await store.SaveAsync(cookies);                        // <- overwrite, unvalidated
_logger.LogInformation("Cookies saved to {Path}", _config.CookieFilePath);
try {
  await _registry.SwitchModeAsync(CallAdapterMode.GVApi, ct);
  return true;                                         // <- true means "did not throw", NOT "works"
}
```

`ActivateCoreAsync` handles a failed probe by `SetAvailable(false); return;` — it does **not** throw. So
`SetCookiesAsync` returns `true` for a cookie set Google has just rejected, and the controller logs
`CDP cookie refresh: 20 cookies extracted and activated` at **INF** and answers HTTP 200. Every 20
minutes. For two days.

**This too was already documented.** `docs/KNOWN-ISSUES.md`, dated 2026-08-01, records the identical log
sequence and names the mechanism — *"a `refresh-from-browser` against a signed-out Chrome overwrites a
working cookie set with a dead one"* — and proposes the fix under **"Proposed hardening (not
implemented)"**: *"Validate before adopting… Keep a last-known-good set and roll back… Never let an
unvalidated refresh overwrite a validated set — that single rule would have contained this outage to a
logged warning."* Tasks 4 and 5 implement exactly that, five weeks late.

---

## 1. Task list

Dependency order. Tasks 1→3 are a chain; 4→7 depend on 1; 8 depends on 1 and 6; 9→11 are independent.

| # | Task | Defect | Files |
|---|---|---|---|
| 0 | On-box evidence capture (no code) | — | none |
| 1 | Persist the cookie lineage timestamps | 2 | `GvCookieSet.cs` |
| 2 | Stop stamping "now" on load | 2 | `GVApiAdapter.cs` |
| 3 | Anchor the first refresh to the real age | **1** | `GVApiAdapter.cs` |
| 4 | Validate before persisting — recovery rung 3 | 3a | `GVApiAdapter.cs` |
| 5 | Validate before persisting — the cron path | 3b | `GvCookieManager.cs`, `GVApiAdapter.cs` |
| 6 | Alarm on a stale browser session | 4 | `GVApiAdapter.cs`, `GVBridgeController.cs` |
| 7 | An operator message that earns its assertion | 4b | `GVApiAdapter.cs` |
| 8 | Surface the lineage on `/api/gvbridge/status` | 2/4 | `GvBridgeDtos.cs`, `GVBridgeController.cs` |
| 9 | Voicemail 100-item saturation signal | 5a | `GvVoicemailClient.cs` |
| 10 | The 100-item caveat on the routes that 404 | 5b | `GvVoicemailController.cs` |
| 11 | Docs: port correction, KNOWN-ISSUES, boundary, cross-repo reply | — | `docs/**` |

---

## Task 0 — On-box evidence capture

**No code. Read-only. Run once, before Task 3 reaches the box.**

> **Box-health rule (non-negotiable).** `radio` is an Intel N100 shared with Radio Console, and journald
> churn correlates with audio distortion. **No follow/streaming output** — never `-f`, never `tail -f`.
> **Every read bounded at the command**, not by a downstream pipe. One short SSH session, then disconnect.
> Use `mcp__ssh-mcp__exec`, not bare `ssh`.

> ⚠ **The box is a live appliance in use.** Nothing in this task changes it, and nothing in this task
> restarts it.

```bash
# 1. What is the service actually listening on? (Settles the 5004-vs-5555 question from the doc bug.)
systemctl show -p ExecStart rotary-phone | head -c 400
systemctl show -p Environment rotary-phone | head -c 400

# 2. Is the 20-minute cron — the hazard in §0.5 — still firing?
crontab -l 2>/dev/null | head -n 40

# 3. The two-day signature: an INF "extracted and activated" followed by a 401 within ~a minute.
#    -r = newest first, so head gives the most recent N and no tail is needed.
journalctl -u rotary-phone --since '-24h' -n 4000 --no-pager -r \
  | grep -E 'cookies extracted and activated|Cookies saved to|health check failed|Unauthorized' \
  | head -n 60

# 4. What does status claim right now? Record it verbatim into the PR body.
curl -s http://localhost:5004/api/gvbridge/status

# 5. Chrome's own PSIDTS age — the §10 cannibalisation hypothesis. Do not modify the profile.
ls -la --time-style=full-iso ~/.config/gv-bridge-chrome/Default/Network/Cookies | head -n 3
```

**Record in the PR body:** the real port, whether the cron is still installed, and the step-4 status JSON.
**Pause and ask the owner** if step 3 shows the "extracted and activated → 401" pattern still running —
that means the browser session has died *again* and §10's cannibalisation hypothesis is live, which
changes the priority of Task 6 from "nice" to "urgent".

---

## Task 1 — Persist the cookie lineage timestamps

**File:** `src/RotaryPhoneController.GVBridge/Auth/GvCookieSet.cs`

Two nullable timestamps travel *with the cookies*, so they survive `SaveAsync` → restart → `LoadAsync`.

> ⚠ **The trap in this file.** `WithRefreshedPsidts` (`:53-77`) builds its result with a **hand-written
> object initializer listing every property**. A property added to the class and forgotten there is
> silently reset to `null` on **every successful rotation** — i.e. on exactly the path that should be
> setting it. This task collapses that initializer into a single private `CopyWith`, so there is one
> place to update, and Task 1's tests add a reflection tripwire that fails if a future property misses it.

**Add after `RawCookieHeader` (`:23`):**

```csharp
    /// <summary>
    /// UTC time the PSIDTS values carried by this set were genuinely MINTED — the moment
    /// <c>RotateCookies</c> returned them. <c>null</c> means UNKNOWN: a set written before this field
    /// existed, one pasted in by hand, one produced by <c>scripts/gv-extract-cookies.py</c>, or one
    /// extracted from the browser (Chrome's jar carries no issue time we can read).
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT "when we loaded the file". The adapter used to stamp <c>DateTime.UtcNow</c> on
    /// every load, which reported a credential minted 2026-09-06 as 208 seconds old and concealed a
    /// two-day session death. It is PERSISTED because a process that cannot know the age of the
    /// credential it inherited cannot schedule around it — that is the 2026-09-08 outage.
    /// </remarks>
    public DateTime? PsidtsMintedAtUtc { get; init; }

    /// <summary>
    /// UTC time a cookie set extracted from the box's Chrome last PASSED a live health probe against
    /// Google. <c>null</c> means no browser-sourced set in this lineage has ever been validated.
    /// Persisted so the age of the BROWSER session survives a restart — the missing signal that let a
    /// dead Chrome login go unnoticed from 2026-09-06 to 2026-09-08.
    /// </summary>
    public DateTime? BrowserSessionValidatedAtUtc { get; init; }
```

**Replace `WithRefreshedPsidts` (`:53-77`) entirely with:**

```csharp
    /// <summary>
    /// Overlay freshly-minted PSIDTS values onto the raw cookie header and stamp the mint time.
    /// A successful <c>RotateCookies</c> IS the mint, so the timestamp is set here rather than at the
    /// call site — that is what makes it travel into <see cref="Serialize"/> and onto disk.
    /// </summary>
    /// <param name="mintedAtUtc">
    /// Test seam. Defaults to now; pass an explicit value to build a set of a known age.
    /// </param>
    public GvCookieSet WithRefreshedPsidts(string? psidts1, string? psidts3, DateTime? mintedAtUtc = null)
    {
        if (psidts1 is null && psidts3 is null) return this;

        var raw = string.IsNullOrEmpty(RawCookieHeader) ? ToCookieHeader() : RawCookieHeader;
        if (psidts1 is not null) raw = SpliceCookie(raw, "__Secure-1PSIDTS", psidts1);
        if (psidts3 is not null) raw = SpliceCookie(raw, "__Secure-3PSIDTS", psidts3);

        return CopyWith(raw, mintedAtUtc ?? DateTime.UtcNow, BrowserSessionValidatedAtUtc);
    }

    /// <summary>
    /// Record that this set came from the browser and has just passed a live health probe. Only ever
    /// called after a successful probe — an unvalidated browser set must never carry this stamp.
    /// </summary>
    public GvCookieSet WithBrowserSessionValidatedAt(DateTime validatedAtUtc)
        => CopyWith(RawCookieHeader, PsidtsMintedAtUtc, validatedAtUtc);

    /// <summary>
    /// The ONLY place this type is copied. Every property must appear here exactly once.
    /// </summary>
    /// <remarks>
    /// ⚠ A property added to <see cref="GvCookieSet"/> and forgotten here is silently reset on every
    /// rotation. No test of a rotation's OUTPUT would catch it, because the output looks correct — the
    /// loss is of a field the test did not think to check. <c>CopyWith_CoversEveryProperty</c> is the
    /// tripwire; if you add a property, it will fail until you add it here too.
    /// </remarks>
    private GvCookieSet CopyWith(
        string? rawCookieHeader, DateTime? psidtsMintedAtUtc, DateTime? browserSessionValidatedAtUtc)
        => new()
        {
            Sapisid = Sapisid,
            Sid = Sid,
            Hsid = Hsid,
            Ssid = Ssid,
            Apisid = Apisid,
            Secure1Psid = Secure1Psid,
            Secure3Psid = Secure3Psid,
            RawCookieHeader = rawCookieHeader,
            PsidtsMintedAtUtc = psidtsMintedAtUtc,
            BrowserSessionValidatedAtUtc = browserSessionValidatedAtUtc,
        };
```

**Backward compatibility — verified, not assumed.** `GvCookieStore.SaveAsync` is
`JsonSerializer.Serialize(this)` with **no options** (`GvCookieStore.cs:18`, `GvCookieSet.cs:96`), and
`LoadAsync` deserialises with the same defaults. `System.Text.Json` ignores unmapped members by default
(`UnmappedMemberHandling.Disallow` is not set), so:

- new code reading an **existing** `gv-cookies.enc` → both fields absent → `null` → treated as UNKNOWN;
- old code reading a **new** file (rollback) → both fields ignored → loads normally.

**Both properties must stay `DateTime?` and non-`required`.** Marking either `required` would make every
existing encrypted cookie file throw `JsonException` inside `LoadAsync`, which swallows it and returns
`null` (`GvCookieStore.cs:36-38`) — the adapter would go unavailable with no diagnostic until a human
re-logged in. That is a worse outage than the one being fixed.

**Two writers bypass this type and will produce sets with `null` timestamps.** Both are correct as-is —
`null` means unknown, and Task 3 handles unknown safely — but note them so nobody is surprised:
`src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs:181-207` (the `gv-login` CLI) and
`scripts/gv-extract-cookies.py:94-121` (writes the JSON by hand).

---

## Task 2 — Stop stamping "now" on load

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

**`:43-44`, retitle the field so the name stops inviting the bug:**

```csharp
    // When the PSIDTS currently held was genuinely MINTED (UTC), as carried by the cookie set itself.
    // Null = unknown. NEVER assign DateTime.UtcNow here on a LOAD — see PsidtsAgeSeconds.
    private DateTime? _psidtsRefreshedAt;
```

**`:397`, in `ActivateCoreAsync` — replace:**

```csharp
        _psidtsRefreshedAt = _cookieSet != null ? DateTime.UtcNow : null;
```

**with:**

```csharp
        // Adopt the mint time the cookie set carries. Loading a credential does not refresh it, and
        // stamping "now" here is what reported a Sep-6 credential as 208 seconds old on Sep 8.
        _psidtsRefreshedAt = _cookieSet?.PsidtsMintedAtUtc;
```

**`:727-728`, in `ReloadCookiesAsync` — replace:**

```csharp
        LoadedAt = DateTime.UtcNow;
        _psidtsRefreshedAt = DateTime.UtcNow;
```

**with:**

```csharp
        LoadedAt = DateTime.UtcNow;                      // when WE loaded it — genuinely now
        _psidtsRefreshedAt = newCookies.PsidtsMintedAtUtc;  // when GOOGLE minted it — not now
```

**`:1056`, in `TryRotateCookiesAsync` — replace:**

```csharp
        _psidtsRefreshedAt = DateTime.UtcNow;
```

**with:**

```csharp
        // WithRefreshedPsidts stamped the mint; read it back rather than taking a second clock sample,
        // so the in-memory age and the persisted age can never disagree.
        _psidtsRefreshedAt = refreshed.PsidtsMintedAtUtc;
```

**`:165-175`, correct the property's doc comment** (it currently documents the bug as the contract):

```csharp
    /// <summary>
    /// Age (seconds) of the rotating freshness cookies (__Secure-1PSIDTS/3PSIDTS), measured from when
    /// Google MINTED them — not from when this process loaded them. Null when the mint time is unknown
    /// (a cookie file written before the timestamp existed, a hand-pasted set, or one extracted from the
    /// browser, whose jar carries no readable issue time).
    /// </summary>
    /// <remarks>
    /// Google's PSIDTS lives ~11 minutes (measured 2026-07-31), so a value approaching 660 means the
    /// next request may 401 even if the periodic health check last passed.
    /// ⚠ <c>null</c> means UNKNOWN, and unknown is NOT healthy — do not render it as 0 or as "fresh".
    /// Before 2026-09-08 this was stamped on every load and therefore always read low after a restart,
    /// which is how a two-day-dead credential looked reassuring. See docs/KNOWN-ISSUES.md finding L2.
    /// </remarks>
    public long? PsidtsAgeSeconds =>
        _psidtsRefreshedAt is { } refreshed
            ? (long)Math.Max(0, (DateTime.UtcNow - refreshed).TotalSeconds)
            : null;
```

`:680` (`_psidtsRefreshedAt = null` on teardown) is **correct as-is** — leave it.

**Existing test that must keep passing:** `GVApiAdapterTests.cs:69-73`
(`PsidtsAgeSeconds_BeforeActivate_ReturnsNull`). It still passes — an un-activated adapter has no cookie
set, so the field is null for the same reason as before.

---

## Task 3 — Anchor the first refresh to the credential's real age

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

**Add above `StartPeriodicTimers` (`:1112`):**

```csharp
    /// <summary>
    /// Floor for the first proactive refresh delay. Never zero, and the reason is subtle:
    /// <see cref="RunProactiveCookieRefreshAsync"/> bails on <c>!IsAvailable</c> and then waits a FULL
    /// period for its next tick. So a tick that fires before activation finishes is not merely wasted —
    /// it is SKIPPED, and the next one is an interval away. That is the same "miss it and wait a whole
    /// period" shape as the bug this method exists to fix.
    /// </summary>
    internal const int MinFirstRefreshDelayMs = 5_000;

    /// <summary>
    /// Test seam: the due time (ms) handed to the most recently created refresh timer.
    /// <see cref="System.Threading.Timer"/> exposes no readable due time, so this is the only way a test
    /// can assert the scheduling decision without waiting out a real interval.
    /// </summary>
    internal int? LastFirstRefreshDelayMs { get; private set; }

    /// <summary>
    /// Delay (ms) until the FIRST proactive PSIDTS refresh of this activation.
    /// </summary>
    /// <remarks>
    /// A long-running process keeps its timer and its credential locked together — every rotation resets
    /// the timer and mints in the same instant — so <c>dueTime == period</c> is invisible there.
    /// ONLY A RESTART decouples them. A fresh process inherits a credential of arbitrary age and, before
    /// this method existed, waited a full interval regardless. On 2026-09-08 it inherited a 55-second-old
    /// PSIDTS, scheduled its first refresh 8m00s out, and the credential died at 8m03s — missed by 52
    /// seconds, and an 83-minute guest-facing outage followed.
    ///
    /// An UNKNOWN age is deliberately treated as "refresh at the floor", not as "brand new". A credential
    /// we cannot date is one we must not extend a full interval of credit to; assuming age-zero would
    /// reproduce precisely the bug being fixed.
    ///
    /// Pure and static so a test can drive it directly — the adapter has no clock seam, and this needs
    /// none.
    /// </remarks>
    internal static int ComputeFirstRefreshDelayMs(
        int refreshIntervalMs, DateTime? psidtsMintedAtUtc, DateTime nowUtc)
    {
        var floorMs = Math.Min(MinFirstRefreshDelayMs, refreshIntervalMs);

        if (psidtsMintedAtUtc is not { } minted)
            return floorMs;

        var remainingMs = refreshIntervalMs - (nowUtc - minted).TotalMilliseconds;

        // Clamp high: a mint time in the future (clock skew, a hand-edited file, a restored backup)
        // must never push the first refresh out beyond one interval.
        if (remainingMs > refreshIntervalMs) return refreshIntervalMs;

        // Clamp low: already past due, or so close that the tick would race activation.
        if (remainingMs < floorMs) return floorMs;

        return (int)remainingMs;
    }
```

**Replace `StartPeriodicTimers` (`:1118-1130`) with:**

```csharp
    private void StartPeriodicTimers()
    {
        var intervalMs = _config.CookieHealthCheckIntervalMinutes * 60 * 1000;
        _healthCheckTimer = new Timer(OnHealthCheckTimer, null, intervalMs, intervalMs);

        // Proactive PSIDTS refresh (spec §4.1). Rung 1 ONLY — browser-less RotateCookies. CDP
        // (rung 3) is heavy and needs the box's Chrome; it stays reserved for reactive recovery.
        if (_config.CookieRefreshIntervalMinutes > 0)
        {
            var refreshMs = _config.CookieRefreshIntervalMinutes * 60 * 1000;

            // dueTime is NOT the period. It is what remains of the interval for the credential we are
            // actually holding — which, after a restart, is not a fresh one. See ComputeFirstRefreshDelayMs.
            var firstMs = ComputeFirstRefreshDelayMs(refreshMs, _cookieSet?.PsidtsMintedAtUtc, DateTime.UtcNow);
            LastFirstRefreshDelayMs = firstMs;

            if (firstMs != refreshMs)
            {
                _logger.LogInformation(
                    "GVApi: first proactive PSIDTS refresh in {FirstMs} ms of a {IntervalMs} ms interval — "
                    + "anchored to the inherited credential's age ({MintedAt}), not to process start",
                    firstMs, refreshMs,
                    _cookieSet?.PsidtsMintedAtUtc?.ToString("O") ?? "unknown");
            }

            _cookieRefreshTimer = new Timer(OnCookieRefreshTimer, null, firstMs, refreshMs);
        }
    }
```

**Also reorder `ActivateCoreAsync` `:460-464`.** `StartPeriodicTimers()` currently runs *before*
`SetAvailable(true)`, so a short due time can fire into `RunProactiveCookieRefreshAsync`'s
`!IsAvailable` guard and be skipped — costing a full interval. The 5 s floor makes that unlikely; the
reorder makes it impossible. Replace:

```csharp
        // 7. Start the periodic timers (health watchdog + proactive PSIDTS refresh)
        StartPeriodicTimers();

        SetAvailable(true);
        _logger.LogInformation("GVApiAdapter activated — SIP transport ready");
```

**with:**

```csharp
        // 7. Mark available BEFORE arming the timers. The proactive refresh bails on !IsAvailable and
        // then waits a full period, so a short first due time firing into an adapter that is not yet
        // marked available would silently cost an entire interval — the same failure mode as the
        // dueTime==period bug. Ordering, not the 5 s floor, is what makes that unreachable.
        SetAvailable(true);

        // 8. Start the periodic timers (health watchdog + proactive PSIDTS refresh).
        StartPeriodicTimers();

        _logger.LogInformation("GVApiAdapter activated — SIP transport ready");
```

The abort path at `:380` calls `StartPeriodicTimers()` on an adapter that is already available; it is
unaffected and needs no change.

---

## Task 4 — Validate before persisting, in recovery rung 3

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

**Add the shared validate-or-roll-back helper, above `TryCdpRefreshAsync` (`:965`):**

```csharp
    /// <summary>Why the last browser (CDP) refresh attempt ended the way it did. Feeds status + alarms.</summary>
    internal enum BrowserRefreshOutcome { NotAttempted, Unreachable, Stale, Succeeded }

    private BrowserRefreshOutcome _lastBrowserRefreshOutcome = BrowserRefreshOutcome.NotAttempted;

    /// <summary>
    /// Adopt <paramref name="candidate"/> in memory, prove it against Google, and roll back completely
    /// if it fails. Returns true ONLY when the candidate passed a live probe.
    /// </summary>
    /// <remarks>
    /// ⚠ Persists NOTHING. The caller decides whether to write to disk, and must do so only on true.
    /// That ordering is the entire point. On 2026-08-01 and again on 2026-09-06→08, a refresh from a
    /// signed-out Chrome overwrote a WORKING on-disk cookie set with a dead one and the working set was
    /// unrecoverable. docs/KNOWN-ISSUES.md proposed this exact rule five weeks before the second outage:
    /// "Never let an unvalidated refresh overwrite a validated set."
    /// </remarks>
    private async Task<bool> TryValidateCandidateAsync(GvCookieSet candidate, CancellationToken ct = default)
    {
        var previousSet = _cookieSet;
        var previousValid = _areCookiesValid;

        _cookieSet = candidate;
        SwapAuthenticatedClients();

        var healthy = await ProbeHealthAsync(ct);
        LastValidatedAt = DateTime.UtcNow;

        if (healthy)
        {
            _areCookiesValid = true;
            LoadedAt = DateTime.UtcNow;
            _psidtsRefreshedAt = candidate.PsidtsMintedAtUtc;
            return true;
        }

        // Roll back to the set that was working. If there was none, there is nothing to protect and
        // nothing to restore — leave the candidate in place so status reflects what we actually tried.
        _areCookiesValid = previousValid;
        if (previousSet != null)
        {
            _cookieSet = previousSet;
            SwapAuthenticatedClients();
        }
        return false;
    }
```

**Replace `TryCdpRefreshAsync` (`:970-992`) with:**

```csharp
    private async Task<bool> TryCdpRefreshAsync()
    {
        if (_cdpExtractor == null || _cookieStore == null)
        {
            _lastBrowserRefreshOutcome = BrowserRefreshOutcome.NotAttempted;
            return false;
        }

        try
        {
            var result = await _cdpExtractor.ExtractAsync(_config.ChromeCdpPort, "voice.google.com");
            if (!result.Success || result.Cookies == null)
            {
                _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Unreachable;
                _logger.LogWarning("GVApi: CDP cookie refresh failed: {Status} {Error}",
                    result.Status, result.Error);
                return false;
            }

            // VALIDATE BEFORE PERSISTING. A signed-out Chrome hands back a full, well-formed, completely
            // dead cookie set; persisting that first destroys the last known-good copy on disk.
            if (!await TryValidateCandidateAsync(result.Cookies))
            {
                _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Stale;
                _logger.LogError(
                    "GVApi: STALE BROWSER SESSION — Chrome returned {Count} cookies and Google rejected "
                    + "them. The on-disk cookie set was NOT overwritten and the working credentials were "
                    + "kept. The box's Chrome login is dead or signed out; ACTION: re-login at "
                    + "voice.google.com. Browser session last validated: {LastValidated}.",
                    result.CookieCount,
                    _cookieSet?.BrowserSessionValidatedAtUtc?.ToString("O") ?? "never");
                return false;
            }

            // Proven. Stamp the browser-session validation and persist — in that order.
            // No SwapAuthenticatedClients needed: GvHttpClientHandler resolves _cookieSet through a
            // closure on every request, and the stamp changes no wire-visible cookie.
            var validated = result.Cookies.WithBrowserSessionValidatedAt(DateTime.UtcNow);
            _cookieSet = validated;
            await _cookieStore.SaveAsync(validated);

            _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Succeeded;
            _logger.LogInformation(
                "GVApi: CDP cookie refresh validated against Google and persisted ({Count} cookies)",
                result.CookieCount);
            return true;
        }
        catch (Exception ex)
        {
            _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Unreachable;
            _logger.LogWarning(ex, "GVApi: CDP cookie refresh threw");
            return false;
        }
    }
```

---

## Task 5 — Validate before persisting, in the cron path

This is the path that ran every 20 minutes for two days. See §0.5.

**5a. Add the public entry point to `GVApiAdapter.cs`, next to `ReloadCookiesAsync` (after `:753`):**

```csharp
    /// <summary>
    /// Adopt an externally-supplied cookie set — the CDP refresh-from-browser endpoint, or a hand-pasted
    /// set — prove it against Google, and persist it ONLY if it works. Returns false, leaving both the
    /// in-memory and the on-disk set untouched, when the candidate is rejected.
    /// </summary>
    /// <remarks>
    /// Returns false when the adapter has never activated: there is then no validated set to protect and
    /// no store to write to, so the caller must use its own cold-start path.
    /// </remarks>
    public async Task<bool> TryAdoptAndPersistCookiesAsync(
        GvCookieSet candidate, string source, CancellationToken ct = default)
    {
        if (_cookieStore == null || _cookieSet == null)
            return false;

        if (!await TryValidateCandidateAsync(candidate, ct))
        {
            _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Stale;
            _logger.LogError(
                "GVApi: REJECTED a cookie set from {Source} — Google refused it. The working on-disk set "
                + "was NOT overwritten. If the source is the box's Chrome, that session is dead: ACTION: "
                + "re-login at voice.google.com.", source);
            return false;
        }

        var validated = candidate.WithBrowserSessionValidatedAt(DateTime.UtcNow);
        _cookieSet = validated;
        await _cookieStore.SaveAsync(validated);
        _lastBrowserRefreshOutcome = BrowserRefreshOutcome.Succeeded;
        if (!IsAvailable) SetAvailable(true);

        _logger.LogInformation(
            "GVApi: adopted and persisted a cookie set from {Source} after it passed a live probe", source);
        return true;
    }
```

**5b. Replace `GvCookieManager.SetCookiesAsync` (`src/RotaryPhoneController.GVBridge/Services/GvCookieManager.cs:80-101`) with:**

```csharp
  public async Task<bool> SetCookiesAsync(GvCookieSet cookies, CancellationToken ct = default)
  {
    // HOT PATH — the adapter is live and holding credentials that may still be good. Prove the incoming
    // set before it is allowed anywhere near disk. The box-side cron drives this every 20 minutes, and
    // from 2026-09-06 to 2026-09-08 it spent two days overwriting a working set with a dead one and
    // reporting success, because the old code saved first and returned true if nothing threw.
    // Both are set together in ActivateCoreAsync (:394-395) and cleared together in teardown (:675-676),
    // so this is one condition expressed twice — checked explicitly anyway, because
    // TryAdoptAndPersistCookiesAsync returns false for "never activated" as well as for "rejected", and
    // conflating those two would send a cold start down the hot path and silently refuse to seed.
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
```

`CurrentCookieSet` is `internal` (`GVApiAdapter.cs:190`) and `GvCookieManager` is in the same assembly, so
this compiles with no visibility change.

**5c. `GVBridgeController.cs:179-186` needs no logic change** — it already does
`if (!success) return StatusCode(500, ...)`. But the two messages now lie in opposite directions, so
replace `:179-186` with:

```csharp
        var success = await _cookieManager.SetCookiesAsync(cookieSet);
        if (!success)
            return StatusCode(502, new
            {
                error = "Cookies were extracted from Chrome but Google rejected them — the browser "
                      + "session is stale. Existing credentials were kept and nothing was overwritten. "
                      + "Re-login at voice.google.com."
            });

        var sapisidPrefix = cookieSet.Sapisid.Length > 8
            ? cookieSet.Sapisid[..8]
            : cookieSet.Sapisid;

        // "extracted and activated" used to be logged for cookies Google had already rejected — the exact
        // INF line that ran every 20 minutes for two days while the bridge was dead. It now means what it
        // says: this set passed a live probe before it was persisted.
        _logger.LogInformation(
            "CDP cookie refresh: {Count} cookies extracted, validated against Google, and activated",
            extraction.CookieCount);
```

> ⚠ **Contract note for Task 11.** A stale-browser refresh now answers **502** where it used to answer
> **200**. The box cron will start logging failures — that is the point — but Radio Console and any
> operator script that treats 200 as "done" must be told.

---

## Task 6 — Alarm on a stale browser session, and make its age visible

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

The loud log lines are already in Tasks 4 and 5. This task adds the *observable* half — the browser
session's true age — so the condition is visible on `/api/gvbridge/status` and not only in journald.

**Add next to `PsidtsAgeSeconds` (after `:175`):**

```csharp
    /// <summary>
    /// UTC time a browser-extracted cookie set last passed a live probe, or null if never. Persisted on
    /// the cookie set, so it survives a restart.
    /// </summary>
    public DateTime? BrowserSessionValidatedAt => _cookieSet?.BrowserSessionValidatedAtUtc;

    /// <summary>
    /// Age (seconds) of the box's Chrome Google Voice session — how long since cookies pulled from it
    /// last actually worked. Null if no browser-sourced set has ever been validated.
    /// </summary>
    /// <remarks>
    /// This is the signal whose absence cost two days. The service mints its own PSIDTS and can look
    /// perfectly healthy on a lineage it regenerates from itself, while the browser it depends on for
    /// bootstrap has been dead since Sep 6. A steadily climbing value here, with everything else green,
    /// IS the warning — recovery has no floor below a working browser session.
    /// </remarks>
    public long? BrowserSessionAgeSeconds =>
        _cookieSet?.BrowserSessionValidatedAtUtc is { } validated
            ? (long)Math.Max(0, (DateTime.UtcNow - validated).TotalSeconds)
            : null;

    /// <summary>
    /// True when the most recent attempt to pull cookies from the box's Chrome produced a set Google
    /// rejected — i.e. Chrome is running and reachable but its Google Voice session is dead.
    /// Distinguishes "the browser session is stale" from "we could not reach the browser at all".
    /// </summary>
    public bool BrowserSessionStale => _lastBrowserRefreshOutcome == BrowserRefreshOutcome.Stale;
```

**Reset it with the rest of the per-generation state at `:686`** (after `_lastApiAuthFailureAtUtc = null;`):

```csharp
        _lastBrowserRefreshOutcome = BrowserRefreshOutcome.NotAttempted;
```

---

## Task 7 — An operator message that earns its assertion

**File:** `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

**Replace `:932-935`:**

```csharp
            _logger.LogWarning(
                "GVApi: all cookie-recovery rungs failed. The box's Chrome login may be dead — " +
                "re-login at voice.google.com so the next CDP refresh can pick up a fresh session.");
            return false;
```

**with:**

```csharp
            // Which rung failed determines what the operator should DO, and the actions differ. The old
            // single message asserted "your Chrome login may be dead" for EVERY exhausted ladder,
            // including runs in which Chrome was never consulted at all. On 2026-09-08 it happened to be
            // right and was still unearned — the owner confirmed the browser page was authenticated
            // while the message claimed otherwise. State only what was actually tested.
            switch (_lastBrowserRefreshOutcome)
            {
                case BrowserRefreshOutcome.Stale:
                    _logger.LogError(
                        "GVApi: all cookie-recovery rungs failed and the BROWSER SESSION IS STALE — Chrome "
                        + "handed us cookies and Google rejected them. This is TESTED, not inferred. "
                        + "ACTION: re-login at voice.google.com in the box's Chrome. Stored credentials "
                        + "were left intact.");
                    break;

                case BrowserRefreshOutcome.Unreachable:
                    _logger.LogError(
                        "GVApi: all cookie-recovery rungs failed and CHROME WAS UNREACHABLE on CDP port "
                        + "{Port} — our own rotation chain lapsed and the browser fallback could not be "
                        + "tried, so the Google login was never tested. ACTION: confirm Chrome is running "
                        + "(pgrep -f \"user-data-dir=$HOME/.config/gv-bridge-chrome\") BEFORE touching the "
                        + "Google login; the session may be perfectly fine.",
                        _config.ChromeCdpPort);
                    break;

                default:
                    _logger.LogError(
                        "GVApi: all cookie-recovery rungs failed and the browser was NEVER CONSULTED (no "
                        + "CDP extractor wired, or no cookie store). Our rotation chain lapsed and nothing "
                        + "tested the Google login. ACTION: check the service's CDP wiring and that Chrome "
                        + "is up on port {Port}; do NOT assume the login is dead.",
                        _config.ChromeCdpPort);
                    break;
            }
            return false;
```

Note the level change from `LogWarning` to `LogError`: an exhausted recovery ladder means the phone is
about to be down, which is not a warning.

---

## Task 8 — Surface the lineage on `/api/gvbridge/status`

**8a. `src/RotaryPhoneController.GVBridge/Api/GvBridgeDtos.cs` — append to `GvBridgeStatusDto`**,
after `LastApiAuthFailureAt` (`:45`). Change `:45`'s trailing `)` to `,` and add:

```csharp
  // Added by the 2026-09-08 first-refresh-anchor work. psidtsAgeSeconds now measures from Google's MINT
  // rather than from our load, so it can be NULL (unknown) and can legitimately be very large after a
  // restart onto an old credential — previously it always read low. browserSessionAgeSeconds is new and
  // is the signal whose absence let a dead Chrome session run unnoticed for two days: the service can
  // regenerate its own PSIDTS lineage indefinitely while the browser it bootstraps from is dead.
  // Appended with defaults to preserve the existing field contract.
  [property: JsonPropertyName("browserSessionValidatedAt")] DateTime? BrowserSessionValidatedAt = null,
  [property: JsonPropertyName("browserSessionAgeSeconds")] long? BrowserSessionAgeSeconds = null,
  [property: JsonPropertyName("browserSessionStale")] bool BrowserSessionStale = false);
```

**8b. `src/RotaryPhoneController.GVBridge/Api/GVBridgeController.cs:58`** — change
`LastApiAuthFailureAt: _adapter.LastApiAuthFailureAt));` to:

```csharp
            LastApiAuthFailureAt: _adapter.LastApiAuthFailureAt,
            BrowserSessionValidatedAt: _adapter.BrowserSessionValidatedAt,
            BrowserSessionAgeSeconds: _adapter.BrowserSessionAgeSeconds,
            BrowserSessionStale: _adapter.BrowserSessionStale));
```

Append-only with defaults is the established pattern here — the three prior extensions of this DTO all
did exactly this, and every existing test in `GVBridgeControllerTests.cs` asserts presence-or-value on
named properties rather than counting them, so none breaks.

> ⚠ `docs/plans/build-stamp-and-deploy-verification.md` plans a `gitShaShort` field on this same DTO. It
> is **not** on `main`. If that work lands first, append after it — do not renumber.

---

## Task 9 — Voicemail 100-item saturation signal

**File:** `src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs`

> ⚠ **`count` bounds THREADS, not messages.** Verified against the checked-in live capture
> `src/RotaryPhoneController.GVBridge.Tests/Fixtures/capture/voicemail.request.json`, whose whole body is
> `[4,20,15,null,null,[null,1,1,1]]` — folder 4, count at index 1 — and against
> `GvThreadClient.ListRawAsync` (`Clients/GvThreadClient.cs:112-114`). `items` flattens every message of
> every thread (`PositionalGvThreadParser.ParseVoicemailList`), so **`items.Count == 100` is the wrong
> test** and would both miss real saturation and fire spuriously. Test `rawThreads`.
>
> Comparing `rawThreads >= count` rather than `>= 100` also covers `GvThreadPoller`'s `count: 50`
> (`Services/GvThreadPoller.cs:129`) and any caller-supplied value, with no literal to drift.

**Insert immediately before the existing `LogInformation` at `:55-58`:**

```csharp
        // Saturation signal. We ask for `count` THREADS and GvThreadClient.ListRawAsync deliberately
        // ignores a page token (the paging field position is UNVERIFIED, so guessing would silently
        // re-read page 1 forever). A full page therefore means there may be voicemails we cannot see —
        // and FindNodeAsync's per-id lookup reports one of those as a genuine 404, which RadioConsole
        // maps to "permanently gone". Nobody knows whether this ceiling is ever reached in practice;
        // this line is the cheapest way to find out. Warning, not Error: it is a real limit being
        // approached, not a malfunction.
        if (rawThreads >= count)
        {
            _logger.LogWarning(
                "Voicemail list returned a FULL page: {RawThreads} threads for a requested count of "
                + "{RequestedCount}. Paging is disabled, so anything older than this page is invisible, "
                + "and a per-id lookup for it will 404 as a genuine miss.", rawThreads, count);
        }
```

---

## Task 10 — Put the 100-item caveat where the 404 is produced

**File:** `src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs`

**Half of this ask already shipped.** Commit `0980171` (2026-09-08) added a full `⚠ LIMITATION` block to
`FindNodeAsync` (`:154-161`) plus the `KNOWN-ISSUES` line and the reply. **Do not duplicate it.**

What is still missing is the caveat on the **routes**, which is what Radio Console actually asked for.
`FindNodeAsync` is a private helper; a reader of the route sees nothing. And note the public list route
`GetList` (`:38-40`) is **not** the right home — it takes `[FromQuery] int count = 20` and forwards the
caller's `pageToken`, so it is a different window from the one that produces the misleading 404.

**Add above `[HttpGet("{id}")]` (`:52`):**

```csharp
    /// <summary>
    /// Fetch one voicemail by id.
    /// </summary>
    /// <remarks>
    /// ⚠ Bounded at the 100 most recent voicemails. This route resolves through
    /// <see cref="FindNodeAsync"/>, which requests <c>count: 100</c> with no page token — see the
    /// LIMITATION note on that method. A <c>404</c> from here means "not in the 100 most recent", NOT
    /// "does not exist". Do not harden anything on 404 meaning permanently gone until paging is verified.
    /// </remarks>
```

**Add above `[HttpGet("{id}/audio")]` (`:62`):**

```csharp
    /// <summary>
    /// Stream a voicemail recording by id.
    /// </summary>
    /// <remarks>
    /// ⚠ Bounded at the 100 most recent voicemails — see <see cref="FindNodeAsync"/>'s LIMITATION note.
    /// A <c>404</c> here means "not in the 100 most recent", NOT "does not exist". RadioConsole maps
    /// this route's 404 to <c>GvMediaUnavailableException.IsPermanent</c> — "retrying will not help" —
    /// so the overclaim is guest-visible: a caller is told a recording is permanently gone when it is
    /// merely old. `GvVoicemailClient` now logs a WARNING whenever a list comes back saturated, which is
    /// how we will learn whether this ceiling is ever actually reached.
    /// </remarks>
```

**Add above `MarkRead`'s `[HttpPost(...)]` attribute (`:84`)** — same `<remarks>` block as `GetItem`.

---

## Task 11 — Docs

**11a. Correct the wrong port.** `docs/plans/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md` sends a
Tester to **5555**. Production is **5004**.

- `deploy/rotary-phone.service:12` — `Environment=ASPNETCORE_URLS=http://0.0.0.0:5004`
- `src/RotaryPhoneController.Server/Properties/launchSettings.json:8` — `5555`, **Development only**
- `docs/KNOWN-ISSUES.md` independently uses `curl -X POST localhost:5004/...`
- `docs/plans/build-stamp-and-deploy-verification.md:87` already states it correctly

⚠ **It is 17 occurrences, not one** — lines 683, 694, 705, 717, 718, 733, 754, 755, 766, 780, 783, 794,
796, 812, 813, 822, 825. Replace `<host>:5555` with `<host>:5004` throughout, and replace the prose at
`:683-686` with:

```markdown
The service listens on **`http://<host>:5004`** in production
(`deploy/rotary-phone.service:12`, `ASPNETCORE_URLS`). `5555` is the **Development** profile only
(`Properties/launchSettings.json:8`) and is not what runs on the box.

⚠ Do not "correct" this back. `5004` is *also* `GVBridge:HT801RtpPort`
(`Models/GVBridgeConfig.cs:13`), an unrelated setting that happens to share the number — that
coincidence is what produced the original error. **Confirm before starting** anyway:
`systemctl show -p Environment rotary-phone`.
```

**11b. `docs/KNOWN-ISSUES.md`.** Under the 2026-08-01 outage section, mark the **"Proposed hardening
(not implemented)"** block as implemented by this PR, and record that it recurred on 2026-09-08 because
the hardening was never built. Add a new resolved entry for finding **L2** (`psidtsAgeSeconds` resets on
activation), citing Tasks 1-3.

**11c. `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`** — an **API-only** Change Log row plus an
Integration Points update for the three new `/api/gvbridge/status` fields. ⚠ **Append; never renumber a
rule** (MEDIUM-1 in `0980171`). ⚠ **This file is modified on the Builder's branch** — re-read it
immediately before editing.

**11d. `docs/SETUP-AND-TESTING.md:190,198`** — the sample status body and the field-meaning table both
document the *old* `psidtsAgeSeconds` semantics. Update both, and state explicitly that `null` means
unknown and unknown is not healthy.

**11e. Cross-repo reply** — `docs/handoffs/2026-09-08-rotaryphone-auth-lineage-fixes.md`, per the naming
convention adopted in the incident doc §7. It must cover:

1. `psidtsAgeSeconds` now measures from Google's mint. Their `<660 / 660–1200` bands
   (`radioconsole-gv-threadid-decode-and-auth-blackout-request.md:287-301`) still hold for a healthy
   process, but **a restart onto an old credential can now report a genuinely huge value**, and
   **unknown is `null`**. Both used to be impossible. Their predictor needs a null branch and no upper
   bound.
2. This supersedes the "`psidtsAgeSeconds` stays exactly as it is" line in
   `radioconsole-gv-auth-blackout-reply.md:182` — scoped precisely: it stays out of the *health
   derivation*; its *accuracy* was a defect.
3. Three new fields, `browserSessionAgeSeconds` / `browserSessionValidatedAt` / `browserSessionStale`,
   and why they are the honest early warning the last two outages lacked.
4. `POST /api/gvbridge/cookies/refresh-from-browser` now answers **502** for a stale browser session
   where it answered **200**.
5. The 100-item saturation line is shipped, and where the caveat now lives.

---

## 2. Test Plan

`dotnet test src/RotaryPhoneController.GVBridge.Tests`

Conventions taken from the existing suite: xUnit `Assert.*` (no FluentAssertions), `NullLogger<T>.Instance`
by default and `Tests/Support/CapturingLogger.cs` where a log line *is* the deliverable, and the
reflection helpers `CreateAdapter` / `NewConfig` / `NewCookies` / `SetField` / `GetField` / `Invoke` /
`SetAvailable`, all `internal static` on `GVApiAdapterRecoveryTests` and already reused cross-file.

New file: `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterCookieLineageTests.cs`.

> **Every test below must be verified to FAIL against `main` before the fix is applied.** These are
> regression tests for a defect that shipped twice; a test that passes on `main` is testing nothing.

### 2.1 ⭐ The restart-simulation test — the important one

Defect 1 is **invisible in a long-running process**: timer and credential stay locked because each
rotation resets both together. It appears only across a restart. A test that does not simulate one
proves nothing.

**A fresh `GVApiAdapter` instance IS a fresh process** for this purpose: it has no memory of when the
credential was minted and must read it off the cookie set it loaded from disk. That is the whole defect.

`System.Threading.Timer` exposes no readable due time, so the assertion runs against
`ComputeFirstRefreshDelayMs` (pure, static) and against `LastFirstRefreshDelayMs` (the value actually
handed to the timer). Reflecting into `Timer._timer._dueTime` is brittle and is not done anywhere in this
repo.

```csharp
    [Theory]
    // A process that loads a PSIDTS of age N must schedule its FIRST refresh at (interval - N).
    [InlineData(0,       480_000)]  // brand new            -> a full interval
    [InlineData(55_000,  425_000)]  // THE 2026-09-08 SHAPE -> 7m05s, not 8m00s. Off by 52s = the outage.
    [InlineData(420_000,  60_000)]  // 7 min old (KNOWN-ISSUES L2) -> 1 min, not 8
    [InlineData(479_000,   5_000)]  // 7m59s old            -> the floor, not 1s
    [InlineData(600_000,   5_000)]  // already past due     -> the floor, never negative
    public void ComputeFirstRefreshDelay_AnchorsToInheritedCredentialAge(int ageMs, int expectedMs)
    {
        var now = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);

        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            refreshIntervalMs: 8 * 60 * 1000, psidtsMintedAtUtc: now.AddMilliseconds(-ageMs), nowUtc: now);

        Assert.Equal(expectedMs, delay);
    }

    [Fact]
    public void ComputeFirstRefreshDelay_UnknownMintTime_RefreshesAtTheFloor()
    {
        // A credential we cannot date must NOT be trusted for a full interval — that is the bug.
        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            8 * 60 * 1000, psidtsMintedAtUtc: null, nowUtc: DateTime.UtcNow);

        Assert.Equal(GVApiAdapter.MinFirstRefreshDelayMs, delay);
    }

    [Fact]
    public void ComputeFirstRefreshDelay_MintTimeInTheFuture_IsClampedToOneInterval()
    {
        // Clock skew or a restored backup must not push the first refresh past a full interval.
        var now = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);

        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            8 * 60 * 1000, psidtsMintedAtUtc: now.AddHours(3), nowUtc: now);

        Assert.Equal(8 * 60 * 1000, delay);
    }

    [Fact]
    public void StartPeriodicTimers_OnRestart_SchedulesFromThePersistedMintTime_NotFromProcessStart()
    {
        // THE RESTART SIMULATION, end to end through the production wiring.
        // A brand-new adapter instance stands in for a brand-new process: it knows nothing about the
        // credential except what the cookie set carries.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            config: GVApiAdapterRecoveryTests.NewConfig(refreshIntervalMinutes: 8));

        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", new GvCookieSet
        {
            Sapisid = "SAPISID-A", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
            PsidtsMintedAtUtc = DateTime.UtcNow.AddMinutes(-7),   // inherited, 7 minutes old
        });

        GVApiAdapterRecoveryTests.Invoke(adapter, "StartPeriodicTimers");

        // ~1 minute remains of the 8-minute interval.
        Assert.NotNull(adapter.LastFirstRefreshDelayMs);
        Assert.InRange(adapter.LastFirstRefreshDelayMs!.Value, 55_000, 65_000);

        // The assertion that fails on main: main schedules a FULL interval regardless of age.
        Assert.NotEqual(8 * 60 * 1000, adapter.LastFirstRefreshDelayMs!.Value);
    }

    [Fact]
    public void StartPeriodicTimers_FreshCredential_StillUsesTheFullInterval()
    {
        // The paired negative: the fix must not turn every activation into an immediate rotation.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            config: GVApiAdapterRecoveryTests.NewConfig(refreshIntervalMinutes: 8));

        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", new GvCookieSet
        {
            Sapisid = "SAPISID-A", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
            PsidtsMintedAtUtc = DateTime.UtcNow,
        });

        GVApiAdapterRecoveryTests.Invoke(adapter, "StartPeriodicTimers");

        Assert.InRange(adapter.LastFirstRefreshDelayMs!.Value, 475_000, 480_000);
    }
```

### 2.2 The persisted mint time survives a restart, and the age is the truth

```csharp
    [Fact]
    public async Task PsidtsMintTime_SurvivesAProcessRestart_AndAgeReportsTheTruth()
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));

        // --- process 1: a genuine rotation mints, stamps, and persists ---
        var minted = DateTime.UtcNow.AddMinutes(-42);
        await store.SaveAsync(
            GVApiAdapterRecoveryTests.NewCookies().WithRefreshedPsidts("p1", "p3", minted));

        // --- process 2: a brand-new adapter instance inherits it ---
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());

        // 42 minutes, not ~0. On main this reads ~0 — the field that concealed a two-day outage.
        Assert.NotNull(adapter.PsidtsAgeSeconds);
        Assert.InRange(adapter.PsidtsAgeSeconds!.Value, 2_500, 2_580);
    }

    [Fact]
    public async Task LegacyCookieFileWithNoMintTime_LoadsFine_AndReportsAgeAsUnknown()
    {
        // Backward compatibility: an existing gv-cookies.enc has neither new field. It must still load
        // (a JsonException here would be swallowed into a null and take the adapter down silently), and
        // it must report UNKNOWN rather than a reassuring small number.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies());   // no timestamps

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());
        Assert.Null(adapter.PsidtsAgeSeconds);
    }
```

### 2.3 ⭐ Negative test for defect 3 — a failed recovery must not destroy the good set

```csharp
    private sealed class FakeCdpExtractor(CdpExtractionResult result) : ICdpCookieExtractor
    {
        public int Calls { get; private set; }

        public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CdpRefresh_WhenExtractedCookiesAreRejected_LeavesTheStoredGoodSetIntact()
    {
        // The 2026-08-01 and 2026-09-06 mechanism: a signed-out Chrome returns a full, well-formed,
        // completely dead cookie set, and the old code persisted it BEFORE discovering that.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        await store.SaveAsync(good);

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);   // Google rejects the dead set

        var refreshed = await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;

        Assert.False(refreshed);

        // THE assertion. On main the on-disk set is SAPISID-DEAD and the good one is gone forever.
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("SAPISID-GOOD", onDisk!.Sapisid);

        // ...and the in-memory set rolled back too, so the adapter is not left holding proven-bad creds.
        Assert.Equal("SAPISID-GOOD", adapter.CurrentCookieSet!.Sapisid);
        Assert.True(adapter.BrowserSessionStale);
    }

    [Fact]
    public async Task CdpRefresh_WhenExtractedCookiesValidate_PersistsThemAndStampsTheBrowserSession()
    {
        // The paired positive: the guard must not block a genuine recovery.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-FRESH"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);

        var refreshed = await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;

        Assert.True(refreshed);
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-FRESH", onDisk!.Sapisid);
        Assert.NotNull(onDisk.BrowserSessionValidatedAtUtc);
        Assert.False(adapter.BrowserSessionStale);
        Assert.NotNull(adapter.BrowserSessionAgeSeconds);
    }
```

### 2.4 The cron path — the one that ran for two days

New file `src/RotaryPhoneController.GVBridge.Tests/Services/GvCookieManagerValidationTests.cs`.

`GvCookieManager` takes the **concrete** `GVApiAdapter` plus `ICallAdapterRegistry`. On the hot path the
registry is never touched (the method returns before `SwitchModeAsync`), so an unconfigured
`Mock<ICallAdapterRegistry>` suffices there. Drive the adapter's probe through `HealthProbeOverride`
rather than mocking HTTP, exactly as the adapter tests do.

```csharp
public class GvCookieManagerValidationTests
{
    private static (GvCookieManager Manager, GVApiAdapter Adapter, GvCookieStore Store, string Path)
        NewHotPathManager(bool probePasses)
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".enc");
        var config = GVApiAdapterRecoveryTests.NewConfig();
        config.CookieFilePath = path;
        config.CookieEncryptionKey = Convert.ToBase64String(new byte[32]);

        var store = new GvCookieStore(path, config.CookieEncryptionKey);
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(config: config);
        adapter.HealthProbeOverride = _ => Task.FromResult(probePasses);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);

        var manager = new GvCookieManager(
            Options.Create(config), adapter, new Mock<ICallAdapterRegistry>().Object,
            NullLogger<GvCookieManager>.Instance);

        return (manager, adapter, store, path);
    }

    [Fact]
    public async Task SetCookiesAsync_GoodCookiesHeld_DeadOnesOffered_ReturnsFalseAndKeepsTheGoodSet()
    {
        // The 20-minute cron's exact shape. On main this returns TRUE — SwitchModeAsync does not throw
        // on a failed probe — and the controller then logs "extracted and activated" at INF. That is how
        // two days of total failure produced no warning at all.
        var (manager, adapter, store, _) = NewHotPathManager(probePasses: false);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));

        var saved = await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"));

        Assert.False(saved);                                              // fails on main: returns true
        Assert.Equal("SAPISID-GOOD", adapter.CurrentCookieSet!.Sapisid);  // in-memory set rolled back
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-GOOD", onDisk!.Sapisid);                    // fails on main: SAPISID-DEAD
    }

    [Fact]
    public async Task SetCookiesAsync_GoodCookiesHeld_WorkingOnesOffered_AdoptsAndPersistsThem()
    {
        // The paired positive: the guard must not block a genuine refresh.
        var (manager, adapter, store, _) = NewHotPathManager(probePasses: true);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));

        var saved = await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-FRESH"));

        Assert.True(saved);
        Assert.Equal("SAPISID-FRESH", adapter.CurrentCookieSet!.Sapisid);
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-FRESH", onDisk!.Sapisid);
        Assert.NotNull(onDisk.BrowserSessionValidatedAtUtc);
    }

    [Fact]
    public async Task SetCookiesAsync_ColdStart_StillSavesSoTheBootstrapPathKeepsWorking()
    {
        // With no validated set to protect, an unproven save is correct — otherwise a box with no
        // cookies at all could never be seeded, and the recovery procedure in KNOWN-ISSUES would break.
        var path = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".enc");
        var keyPath = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".bin");
        var config = GVApiAdapterRecoveryTests.NewConfig();
        config.CookieFilePath = path;
        config.CookieKeyFilePath = keyPath;
        config.CookieEncryptionKey = Convert.ToBase64String(new byte[32]);

        // A never-activated adapter: CurrentCookieSet is null, so the cold path must be taken.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(config: config);
        var registry = new Mock<ICallAdapterRegistry>();
        registry.Setup(r => r.SwitchModeAsync(It.IsAny<CallAdapterMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var manager = new GvCookieManager(
            Options.Create(config), adapter, registry.Object, NullLogger<GvCookieManager>.Instance);

        await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-SEED"));

        // The seed reached disk even though nothing validated it — that is the intended cold-path
        // behaviour. (The return value is false here because the un-activated adapter reports
        // AreCookiesValid == false; the assertion under test is that the file was written.)
        var store = new GvCookieStore(path, config.CookieEncryptionKey);
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("SAPISID-SEED", onDisk!.Sapisid);
    }
}
```

Signature confirmed on `main`:
`Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default)`
(`src/RotaryPhoneController.Core/ICallAdapterRegistry.cs:13`), so the mock setup above compiles as
written. If standing the registry up still proves awkward, assert the same guarantee one layer down — on
`GVApiAdapter.TryAdoptAndPersistCookiesAsync` — and say so in the PR body rather than skipping the case.

### 2.5 The `CopyWith` tripwire

New file `src/RotaryPhoneController.GVBridge.Tests/Auth/GvCookieSetLineageTests.cs`.

```csharp
    [Fact]
    public void WithRefreshedPsidts_PreservesEveryPersistedField_AndStampsTheMint()
    {
        var validatedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var mintedAt = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);
        var original = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            RawCookieHeader = "SAPISID=s; __Secure-1PSIDTS=old",
            BrowserSessionValidatedAtUtc = validatedAt,
        };

        var rotated = original.WithRefreshedPsidts("new1", "new3", mintedAt);

        Assert.Equal(mintedAt, rotated.PsidtsMintedAtUtc);
        Assert.Equal(validatedAt, rotated.BrowserSessionValidatedAtUtc);   // must NOT be dropped
        Assert.Contains("__Secure-1PSIDTS=new1", rotated.RawCookieHeader);
    }

    [Fact]
    public void CopyWith_CoversEveryProperty()
    {
        // TRIPWIRE. If this fails, a property was added to GvCookieSet and not added to CopyWith — in
        // which case it is silently reset on every rotation and no test of the rotation's OUTPUT would
        // notice. Update CopyWith, then update this list.
        var props = typeof(GvCookieSet)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[]
        {
            "Apisid", "BrowserSessionValidatedAtUtc", "Hsid", "PsidtsMintedAtUtc", "RawCookieHeader",
            "Sapisid", "Secure1Psid", "Secure3Psid", "Sid", "Ssid",
        }, props);
    }

    [Fact]
    public void LineageTimestamps_RoundTripThroughSerializeAndDeserialize()
    {
        // The persistence guarantee at the type level: GvCookieStore is only Serialize -> AES -> file,
        // so if the timestamps survive this they survive a restart.
        var minted = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);
        var validated = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var original = new GvCookieSet
        {
            Sapisid = "s", Sid = "sid", Hsid = "h", Ssid = "ss", Apisid = "a",
            PsidtsMintedAtUtc = minted, BrowserSessionValidatedAtUtc = validated,
        };

        var round = GvCookieSet.Deserialize(original.Serialize());

        Assert.Equal(minted, round.PsidtsMintedAtUtc);
        Assert.Equal(validated, round.BrowserSessionValidatedAtUtc);
    }

    [Fact]
    public void Deserialize_JsonWithoutTheNewFields_YieldsNulls_NotAnException()
    {
        // Backward compatibility at the type level. If System.Text.Json ever started rejecting unmapped
        // members, GvCookieStore.LoadAsync would swallow the JsonException into a null and the adapter
        // would go unavailable with no diagnostic. This pins that it does not.
        const string legacy =
            """{"Sapisid":"s","Sid":"sid","Hsid":"h","Ssid":"ss","Apisid":"a","RawCookieHeader":null}""";

        var parsed = GvCookieSet.Deserialize(legacy);

        Assert.Equal("s", parsed.Sapisid);
        Assert.Null(parsed.PsidtsMintedAtUtc);
        Assert.Null(parsed.BrowserSessionValidatedAtUtc);
    }
```

### 2.6 Saturation signal

Extend `src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs`. Build an N-thread
body by spreading `GvWireBuilder.Thread(..., folder: 4, ...)` into `GvWireBuilder.Response(params string[])`
— the pattern at `Api/GvSmsControllerThreadIdDecodeTests.cs:63-70`. `NewClient` there takes only a
response handler, so add a sibling that also returns the logger:

```csharp
    private static (GvVoicemailClient Client, CapturingLogger<GvVoicemailClient> Log) NewLoggingClient(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var log = new CapturingLogger<GvVoicemailClient>();
        return (new GvVoicemailClient(threadClient, parser, new StubFetcher(), log), log);
    }

    /// <summary>N voicemail threads, each with <paramref name="messagesPerThread"/> messages.</summary>
    private static string VoicemailThreads(int threadCount, int messagesPerThread = 1)
        => GvWireBuilder.Response(Enumerable.Range(0, threadCount)
            .Select(t => GvWireBuilder.Thread(
                $"t.{t}", folder: 4, isRead: 0, counterparty: "+19195551234",
                Enumerable.Range(0, messagesPerThread)
                    .Select(m => GvWireBuilder.Message($"vm.{t}.{m}", Epoch, "+19195551234",
                        GvWireBuilder.TypeVoicemail, isRead: 0))
                    .ToArray()))
            .ToArray());

    [Fact]
    public async Task ListVoicemailsAsync_FullPage_LogsExactlyOneSaturationWarning()
    {
        // 100 threads for a requested count of 100 — the ceiling may be real and we cannot see past it.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(100)) });

        var result = await client.ListVoicemailsAsync(count: 100);

        Assert.True(result.Succeeded);
        var warning = Assert.Single(log.AtLevel(LogLevel.Warning));
        Assert.Contains("FULL page", warning.Message);
        Assert.Contains("100", warning.Message);
    }

    [Fact]
    public async Task ListVoicemailsAsync_UnderTheLimit_LogsNoSaturationWarning()
    {
        // A guard that fires on the happy path is noise, not a signal.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(99)) });

        await client.ListVoicemailsAsync(count: 100);

        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task ListVoicemailsAsync_MultiMessageThreads_DoesNotFalselyReportSaturation()
    {
        // ⚠ THE CORRECTNESS TRAP. 40 threads x 3 messages = 120 ITEMS but only 40 THREADS, against a
        // requested count of 100. An implementation testing items.Count warns here, wrongly — and would
        // also MISS real saturation whenever a full page happens to hold single-message threads.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(40, messagesPerThread: 3)) });

        var result = await client.ListVoicemailsAsync(count: 100);

        Assert.Equal(120, result.Items.Count);
        Assert.Empty(log.AtLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task ListVoicemailsAsync_PollerPageSize_SaturatesAtItsOwnCount_Not100()
    {
        // GvThreadPoller asks for 50 (Services/GvThreadPoller.cs:129). Comparing against `count` rather
        // than a hardcoded 100 is what makes this case work with no extra code.
        var (client, log) = NewLoggingClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(VoicemailThreads(50)) });

        await client.ListVoicemailsAsync(count: 50);

        Assert.Single(log.AtLevel(LogLevel.Warning));
    }
```

> **Implementer note.** Verify `GvWireBuilder.Thread`'s exact parameter order and that `Message` takes
> `GvWireBuilder.TypeVoicemail` before writing these — the shapes above follow
> `Api/GvSmsControllerThreadIdDecodeTests.cs:63-70`, which builds SMS threads (`folder: 2`,
> `TypeSmsInbound`). `Fixtures/capture/voicemail.response.json` is the ground truth for the voicemail
> shape, and `GvWireBuilderShapeTests` pins the builder against it.

### 2.7 Regression suite

`dotnet test src/RotaryPhoneController.GVBridge.Tests` and
`dotnet test src/RotaryPhoneController.Server.Tests` must both be green. Baseline before this work:
**745 passed, 2 skipped, 0 failed** (`0980171`). Expect that number to rise, never to fall. Pay attention
to `GVBridgeControllerTests.cs` (status shape), `GvCookieSetPsidtsOverlayTests.cs` (the overlay
round-trip), and `GVApiAdapterTests.cs:69-73`.

### 2.8 On-box UAT

> ⚠ **The box is a live appliance in use, shared with Radio Console.** Bounded, non-streaming reads only.
> Deploy with `Deploy-ToLinux.ps1`; note it supports **`-NoRestart`**, which stages the binary without
> restarting.
>
> ⚠ **A restart is exactly what triggered the 2026-09-08 outage** — it is not free. But it is also the
> only way to exercise this fix, and *with the fix in place* it is the thing that becomes safe. So:
> **deploy, then restart deliberately, with the owner aware and watching**, not as an afterthought at the
> end of an unrelated deploy.
>
> ⚠ **Back up `/opt/rotary-phone/appsettings.Production.json` before the sync and verify
> `BluetoothAdapter` is still `hci1` after** — the deploy tar path clobbers it
> (`docs/KNOWN-ISSUES.md`), and that crosses the Radio Console audio boundary.

**U1 — the restart-anchoring proof, on the box.** This is the acceptance test for defect 1.

1. `curl -s http://localhost:5004/api/gvbridge/status | jq '{psidtsAgeSeconds, browserSessionAgeSeconds}'`
   — note the age, call it `N`.
2. `sudo systemctl restart rotary-phone`, noting the wall-clock time.
3. Within 30 s, read the new log line, bounded:
   `journalctl -u rotary-phone --since '-2min' -n 200 --no-pager -r | grep -m 1 'first proactive PSIDTS refresh'`
4. **PASS:** the reported `FirstMs` ≈ `(8 min − N)`, not `480000`. **FAIL:** it reads `480000` for a
   non-zero `N`.
5. Re-read status: `psidtsAgeSeconds` must be ≈ `N + elapsed`, **not** reset to ~0. On `main` it resets —
   that reset is the lie.

**U2 — the good-set-survives proof.** With the service healthy, stop Chrome (or point `ChromeCdpPort` at
a dead port), `curl -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser`, and confirm:
a **502** with the stale-session message, an `ERR` line naming the stale browser session, `sipRegistered`
still `true`, and `/api/gvbridge/sms/threads` still 200. On `main` this sequence kills the service.

**U3 — the alarm is visible.** `browserSessionAgeSeconds` on `/api/gvbridge/status` climbs across a
restart and does not reset. This is the field that would have caught Sep 6 on Sep 6.

**U4 — no journald flood.** `journalctl -u rotary-phone --since '-30min' -n 3000 --no-pager | wc -l`
must be comparable to the pre-deploy baseline. The new WARN/ERR lines fire on failure paths and on a
saturated voicemail page — none should be periodic. A flood on this N100 box degrades audio.

---

## 3. PR body — required sections

**Docs Impact:** `docs/KNOWN-ISSUES.md`, `docs/SETUP-AND-TESTING.md`,
`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`,
`docs/plans/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md`,
`docs/handoffs/2026-09-08-rotaryphone-auth-lineage-fixes.md`, and this plan.

**Also record:** the Task 0 findings; confirmation that each §2 test was observed to FAIL on `main`
before the fix; and the U1 before/after numbers.

**Auto-merge.** Per the auto-merge policy this may merge on green gates **except** for two things that
need the owner:

1. **The §2.8 restart on the live box** — a restart is what caused the outage; the owner should be
   watching.
2. **The `psidtsAgeSeconds` semantic change (§0.4)** — it is a contract change to a live cross-repo
   consumer that has published bands for the field. The handoff in Task 11e must go out with it.

**Pause and ask** if Task 0 shows the browser session has died again — that would confirm §10's
cannibalisation hypothesis, and Task 6's alarm stops being a diagnostic and becomes the thing keeping
the phone alive.
