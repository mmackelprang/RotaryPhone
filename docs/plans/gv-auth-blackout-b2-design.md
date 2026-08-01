# Design Spec: B2 — GV auth blackout (PSIDTS staleness → deterministic ~9-min dead window)

**Status:** Draft — awaiting owner review before the plan is executed
**Date:** 2026-07-31
**Defect:** B2 from `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md` §B2
**Companion plan:** `docs/plans/gv-auth-blackout-b2-plan.md`
**Arc:** `docs/plans/gv-voicemail-sms-arc.md` (this repo has **no** `BUILDER_QUEUE.md`; the arc tracker is the queue)
**Branch policy:** feature branch `fix/gv-auth-blackout` → PR → `main`. **Not** a BT/audio-boundary change (GV HTTP/auth only), so no `RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` adapter/profile coordination is required — but the boundary doc's Integration Points table gains new `/api/gvbridge/status` fields (API-only Change Log entry, same posture as PR5/mark-read).

---

## 1. Problem

Radio Console observes HTTP 502 from `/api/gvbridge/sms/*` in a clean, repeating pattern: roughly 9 dead
minutes inside every ~20-minute cycle. Upstream, `journalctl -u rotary-phone` shows
`api2thread/list returned Unauthorized for folder Sms` — **271 occurrences on 2026-07-31**, zero
`TooManyRequests`. 11 of 11 of their 502s fall inside a dead window (perfect correlation).

**Throttling is falsified** and is not re-litigated here (§B2.2 of the handoff: constant-rate poller shows
the same on/off pattern; upstream status is always 401, never 429; recovery lands on fixed boundaries, not
after a variable cooldown). The upstream status is `Unauthorized`. This is an **auth-freshness** defect.

Google's rotating `__Secure-1PSIDTS` / `__Secure-3PSIDTS` cookies appear good for **~11 minutes**. Nothing
in this service refreshes them on that timescale, and nothing on the SMS/voicemail data path reacts to the
401 when it arrives.

---

## 2. Root-cause findings

All seven findings are verified against **`origin/main` at `627b928`** (the tree Builder will branch from; the parser fix PR #69 is included). The deployed binary's log strings all
resolve here, so the source is trustworthy. F1-F5 map to the handoff's three asks; **F6 and F7 were found
while tracing F3 and were not in the handoff** — F7 in particular reframes ask 1, because it shows the
service has *no* working in-process recovery cadence at all, not merely a mistuned one.

### F1 — `CookieRefreshIntervalMinutes` is a dead config knob (confirms handoff §B2.3)

Declared at `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:23` (default `5`), set at
`src/RotaryPhoneController.Server/appsettings.json:75`. A grep across `src/` finds **zero readers**. There
is no proactive cookie/PSIDTS refresh timer in this service at all.

### F2 — The only periodic auth mechanism is a 30-minute probe, not a refresh

`GVApiAdapter.ActivateAsync` starts one timer (`GVApiAdapter.cs:322-323`):

```csharp
var intervalMs = _config.CookieHealthCheckIntervalMinutes * 60 * 1000;   // default 30
_healthCheckTimer = new Timer(OnHealthCheckTimer, null, intervalMs, intervalMs);
```

`RunHealthCheckAsync` (`GVApiAdapter.cs:718-771`) *probes* `threadinginfo/get` via
`_accountClient.IsHealthyAsync()` and only enters recovery when that probe fails. At 30-minute granularity
it cannot possibly track an 11-minute cookie lifetime, and — critically — it probes a **different endpoint**
than the one that is failing.

### F3 — The ~20-minute cadence is not produced by any constant in this repo

No `20` / `1200` / `TimeSpan.FromMinutes(20)` exists anywhere in `GVBridge` or `Server`. The only timers are
the 30-minute health check (F2) and the SIP keep-alive (`GvSipTransport.cs:1917`). Two pieces of evidence
say the scheduler is **external to this process**:

1. The observed log line `CDP cookie refresh: {Count} cookies extracted and activated` is
   `src/RotaryPhoneController.GVBridge/Api/GVBridgeController.cs:183` — an **HTTP endpoint handler**
   (`POST /api/gvbridge/cookies/refresh-from-browser`). Something outside the process is calling it.
2. The uncommitted 2026-07-16 entry in `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` documents
   box-side `gv-bridge-{watchdog,restart}` **systemd user units** plus `~/bin/gv-bridge-*.sh`, owned by
   RotaryPhone but **not tracked in this repo**.

Corroborating: the adapter's *internal* CDP rung logs a different string — `GVApi: refreshed cookies from
browser via CDP` (`GVApiAdapter.cs:569`) — so the observed line can only have come from the HTTP endpoint.
The handoff's own boundary capture (`15:00:01 Cookies saved` → `15:00:02 GV adapter re-activated` →
`15:00:02 CDP cookie refresh`) traces exactly to `GvCookieManager.cs:88` → `:96` → `GVBridgeController.cs:183`,
i.e. the inbound-HTTP path, not the ladder.

The second-level exactness of that capture at the top of a 20-minute boundary favours **cron or a systemd
timer** (`*/20 * * * *` / `OnCalendar=*:0/20`) over an in-process timer — a .NET `Timer` cannot hold
wall-clock alignment across hours. (Radio Console's *healthy-again* transition times drift ~+5 s per cycle,
but those are their 60-second poller's observations, not the refresh itself, so that drift is a property of
their sampling and is **not** usable evidence about the scheduler's shape.)

> **This is a hypothesis, not a confirmed finding.** The plan opens with a bounded on-box diagnostic
> (Task 0) to identify the actual scheduler before anything else is changed, because installing an
> in-process refresh timer while an unknown external one is still running would double the refresh rate
> against `RotateCookies`. Task 0 also checks the deployed `${INSTALL_DIR}/ChromeExtension` (referenced at
> `deploy/setup-gvbridge.sh:26` but **not present in this repo**) for a `chrome.alarms` /
> `periodInMinutes: 20` scheduler — a third plausible shape.

### F4 — The reactive-401 escalation already exists, but only on the SIP leg

The box logs `Credential fetch auth-failed (401) — escalating cookie refresh`. That string is
`src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs:1029`, in the `catch (GvAuthException)` around
`_getCredentials()`. The full existing mechanism:

| Stage | Location |
|---|---|
| `GvAuthException` thrown on 401/403 | `Sip/GvSipCredentialProvider.cs:70` (type at `Sip/SipModels.cs:40`) — **the only throw site in the codebase** |
| Caught, logged, escalated, rethrown | `Sip/GvSipTransport.cs:1023-1033` (the log line is `:1029`) |
| `AuthenticationFailed` event raised | `GvSipTransport.RaiseAuthenticationFailed` (`:1748`); event at `:163`; raised at `:1031`, `:1130`, `:1187`, `:1203` |
| Adapter handler | `GVApiAdapter.HandleAuthenticationFailed` (`:525-526`) → `TriggerCookieRecovery` (`:532-538`) |
| Single-flight guard | `Interlocked.CompareExchange(ref _refreshingCookies, 1, 0)` (`:534`), released in `finally` (`:583`) |
| The ladder | `RecoverFromAuthFailureAsync` (`:540-586`): rung 1 `TryRotateCookiesAsync` (browser-less RotateCookies) → rung 2 `ReloadCookiesAsync` (disk) → rung 3 `TryCdpRefreshAsync` (Chrome/CDP) |

**The SMS/voicemail path reaches none of this.** `GvThreadClient.ListRawAsync`
(`Clients/GvThreadClient.cs:93-142`) collapses *every* non-2xx to `null` at `:127-133`:

```csharp
if (!response.IsSuccessStatusCode)
{
    _logger.LogWarning("api2thread/list returned {Status} for folder {Folder}",
        response.StatusCode, folder);
    return null;
}
```

`null` → `GvThreadListResult.Empty(succeeded: false)` → `GvSmsController` returns **502**
(`Api/GvSmsController.cs:62`, `:73`, `:182`, `:197`). No exception, no status discrimination, no
escalation. This is precisely why SIP recovers while SMS blacks out: **SIP has the reactive path and SMS
does not.**

Two consequences worth recording:
- `GvThreadPoller` increments `_consecutiveFailures` only on a *thrown* exception
  (`Services/GvThreadPoller.cs:78-84`), so a 401 does not even engage `ThreadPollBackoffSeconds`. It
  re-polls at full cadence through the entire blackout, logging one warning per cycle.
- The single genuine choke point for **every** GV request (list, sendsms, updateread, recording,
  account, sipregisterinfo) is the `DelegatingHandler` `Auth/GvHttpClientHandler.SendAsync`
  (`:21-34`). It stamps SAPISIDHASH/Cookie/Origin/Referer and forwards, with **zero status inspection**.

**Answer to "can it be factored into one shared helper?" — yes, and it should be.** The ladder is already
one method; what is missing is (a) an *awaitable* entry point so a caller can retry after it completes, and
(b) reachability from the data plane. Both are additive to the existing single-flight core, not a second
refresh path. See §4.2.

### F5 — `_areCookiesValid` is a cached probe result, exactly the shape Radio Console described

`GVApiAdapter.cs:722-723`:

```csharp
var healthy = await _accountClient.IsHealthyAsync();   // probes threadinginfo/get
_areCookiesValid = healthy;
```

Set from the same probe at `:273` (activation), `:411` (reload), `:701` (post-rotation), `:725` (watchdog).
`Degraded` is derived from it (`GVApiAdapter.cs:91`):

```csharp
public bool Degraded => IsAvailable && !(_areCookiesValid && (_sipTransport?.IsRegistered ?? false));
```

So the 15:13:03 measurement decodes cleanly:

| Field | Reported | Why it was wrong |
|---|---|---|
| `cookiesValid: true` | ✔ per the probe | Last `threadinginfo/get` probe ran up to **30 minutes** earlier and passed. It is a different endpoint from `api2thread/list` and was not re-run. |
| `sipRegistered: true` | ✔ and genuinely **honest** | SIP really was registered — its own reactive refresh (F4) had recovered it. |
| `degraded: false` | follows from the two above | `true && !(true && true)` = `false`. |
| `psidtsAgeSeconds: 781` | ✔ and **already correct** | 781 s = 13m01s, i.e. already past the ~11-minute lifetime. The endpoint was carrying the evidence of its own staleness and not using it. |

Radio Console's articulated principle holds exactly: *a health field derived from a probe rather than from
"did the last real call succeed" will report healthy straight through an outage.*

### F6 — `ActivateAsync` is not re-entrant: every external refresh leaks a timer and a SIP transport

`GvCookieManager.SetCookiesAsync` (`Services/GvCookieManager.cs:95`) calls
`_registry.SwitchModeAsync(CallAdapterMode.GVApi)`. In `Core/CallAdapterRegistry.cs:37` the deactivate is
guarded on a *mode change*:

```csharp
if (_activeAdapter != null && _activeAdapter.Mode != mode)
    await _activeAdapter.DeactivateAsync(ct);
```

GVApi is already the active mode (`DefaultMode: "GVApi"`, `Program.cs:174`), so `DeactivateAsync` is
**skipped** and `ActivateAsync` (`GVApiAdapter.cs:220`) re-runs on the live adapter. It has no re-entry
guard and disposes nothing at the top, so each external refresh:

- **`:323`** overwrites `_healthCheckTimer` — the previous `System.Threading.Timer` stays armed in the
  runtime timer queue and keeps firing every 30 minutes forever;
- **`:260`** overwrites `_httpClient` without disposing the previous one;
- **`:292`** constructs a **new `GvSipTransport`** without disposing the old — accumulating WebSockets,
  keep-alive timers, Opus codecs and REGISTER state, with `AuthenticationFailed` / `IncomingCallReceived`
  handlers still subscribed on the orphans.

At the observed 20-minute cadence that is ~3 leaked timers and ~3 leaked SIP transports per hour, ~72/day.
Plausibly a contributor to the journald/CPU churn described in
`docs/prompts/radioconsole-cdp-spam-and-build-stamp-request.md`.

### F7 — the 30-minute watchdog is starved and effectively never fires

F6 has a second-order consequence that is the real reason nothing ever recovers on its own. Each external
refresh installs a *fresh* 30-minute timer; refreshes arrive every ~20 minutes; therefore **the newest timer
never reaches its due time.** The orphaned timers still fire (F6), but the intended watchdog contract —
"notice invalid cookies, run the recovery ladder" — is dead on the deployed box.

Since `RunHealthCheckAsync` → `TriggerCookieRecovery` is the **only timed entry into the ladder in the whole
repo**, this explains the handoff's §B2.2 observation that recovery tracks wall-clock boundaries rather than
any reactive path: on the box today, *the only thing that ever restores auth is the external refresher.*
There is no in-process recovery cadence at all — not a slow one, none.

---

## 3. Reconciliation with `docs/plans/gv-websocket-keepalive-reconnect.md`

**Decision: independent. Do not merge, do not sequence behind. The overlapping plan is already shipped and
its working-tree copy is a stale artifact.**

Evidence:

- The plan's implementation merged as **PR #36** (`ef9f2ba Merge pull request #36 from
  mmackelprang/fix/gv-ws-keepalive-reconnect`, feature commits `a98f943`, `84a9a91`, `937e1c6`).
- `docs/KNOWN-ISSUES.md` records *"Idle SIP WebSocket never reconnects → inbound calls stop ringing
  (**RESOLVED 2026-06-13**) — ✅ Resolved by the keep-alive / auto-reconnect / honest-status PR."*
- Part C is in the tree verbatim: `GvSipTransport.IsRegistered => _registered && IsConnected` (`:115`),
  `IsConnected` (`:118`), `LastConnectedAt` (`:121`), `GVApiAdapter.IsWebSocketConnected` (`:72`),
  `SipLastConnectedAt` (`:76`), `GvBridgeStatusDto.WsConnected` / `LastConnectedAt`
  (`Api/GvBridgeDtos.cs:27-29`), plus the test `GetStatus_IncludesWsConnectedAndLastConnectedAt`
  (`Tests/Api/GVBridgeControllerTests.cs:58`).
- `git log -- docs/plans/gv-websocket-keepalive-reconnect.md` is empty: the **doc** was never committed
  even though the **code** was. The "Ready for Builder" banner is stale by ~7 weeks.

So there is no live plan to merge with. There is also no risk of a conflicting second status abstraction,
because the two concerns sit on **different planes and compose**:

| | Keepalive PR #36 (shipped) | B2 (this spec) |
|---|---|---|
| Plane | SIP signaling / WebSocket transport | GV data plane / HTTP auth |
| Question answered | "is the socket really up?" | "did the last real API call really work?" |
| Fields | `sipRegistered`, `wsConnected`, `lastConnectedAt` | `cookiesValid`, `degraded`, + new data-plane fields |

The 15:13:03 measurement is itself the proof they don't overlap: `sipRegistered:true` was **honest** at that
moment — PR #36 is exactly why the SIP leg looked fine while the SMS leg was dead. B2 extends the same
`GvBridgeStatusDto` in the same append-only style the mark-read and throttle work used. **One status
abstraction, extended — not a second one.**

**Recommended action (in this PR):** commit the stale plan doc with a `SHIPPED — PR #36 (2026-06-13)` banner
rather than deleting it, so `docs/plans/` keeps the historical record and no future session re-queues it.
Its §7 OPEN QUESTIONS 1-3 were all resolved in the shipped code (CDP auto-refresh *is* wired as rung 3;
`ReconnectOptions` + `TimeProvider` is the test seam) — the banner should say so.

---

## 4. Design

Three workstreams, matching the three asks. Ordered so each is independently reviewable and the risky one
(a new outbound-call cadence to Google) lands behind a diagnostic.

### 4.1 Ask 1 — make the interval real, and align it to the ~11-minute PSIDTS lifetime

**Diagnose first (Task 0).** Identify the external ~20m05s scheduler before adding an internal one.
Bounded, read-only, no tailing (see §6). If an external refresh loop exists, **retiring it is an owner
action on the box, outside this repo** — the plan documents it and the handoff reply flags it, but the PR
does not reach across the boundary to change it.

**Then:** wire `CookieRefreshIntervalMinutes` to a real proactive timer in `GVApiAdapter`, alongside the
existing health-check timer, that performs a **rung-1-only** refresh (browser-less `RotateCookies` via
`TryRotateCookiesAsync`). Rungs 2 and 3 stay reserved for reactive recovery — CDP in particular is heavy,
needs the box's Chrome, and must not run on a routine cadence.

**Interval choice: default 8 minutes** (raised from the currently-inert `5`).

- Must be comfortably below the observed ~11-minute lifetime; 8 min gives ~3 minutes of margin.
- 5 min would work but costs 60% more `RotateCookies` calls/day for no added safety, and this codebase has
  a documented account-level throttling incident (2026-06-19) that makes gratuitous call volume a real risk.
- The value is now genuinely owner-tunable, which is the point of the ask — if Google shortens the
  lifetime, the knob moves. The reactive path (§4.2) is what makes shortening survivable without a redeploy.

**Safety properties** (all mandatory — this is the workstream that adds outbound calls to Google):

- Skips entirely when the adapter is not `IsAvailable`, when `_cookieSet` is null, or when
  `EnableCookieRotation` is false.
- Shares the **existing** `_refreshingCookies` single-flight guard, so a proactive tick can never race a
  reactive recovery.
- Skips while `_sipTransport.IsThrottled` (a 603/403 account cooldown means *stop talking to Google*).
- Setting `CookieRefreshIntervalMinutes: 0` disables the timer — a kill switch that restores today's
  behavior without a redeploy.
- Does **not** call `ReRegisterUnlessThrottledAsync` on the proactive path. A successful PSIDTS rotation
  does not invalidate a live SIP registration, and re-registering on an 8-minute cadence would re-create
  the 2026-06-19 REGISTER-storm risk.

**Also in this workstream: make `ActivateAsync` re-entrant (F6/F7).** This is a deliberate scope expansion
beyond the handoff, justified on three grounds:

1. **It is the mechanism behind ask 1's symptom.** F7 shows the only timed entry into the recovery ladder
   is starved by the external refresher. "Why doesn't the 5-minute config produce a 5-minute cadence" has
   a two-part answer: the knob is dead (F1), *and* the one timed mechanism that does exist never fires (F7).
   Fixing only the first leaves the second.
2. **Without it, this workstream makes things worse.** A new proactive timer installed in `ActivateAsync`
   would be leaked and re-armed on every external refresh exactly as `_healthCheckTimer` is today —
   multiplying `RotateCookies` call volume by the number of orphans. That is a storm, and this codebase
   has a documented account-throttle incident.
3. **It is small and local.** Guard the top of `ActivateAsync` so a re-activation disposes what it is about
   to replace (`_healthCheckTimer`, `_httpClient`, `_sipTransport` with its event handlers unsubscribed),
   or short-circuits to a cookie-swap when already active. Same file, no new abstraction.

The preferred shape is **dispose-then-rebuild** rather than a hard idempotence guard, because the external
refresher's intent — adopt new cookies into the live adapter — is legitimate and must keep working. What
must stop is leaking the previous generation. `DeactivateAsync` (`GVApiAdapter.cs:329`) already contains
the correct teardown sequence; the fix is to invoke it rather than duplicate it.

### 4.2 Ask 2 — reactive refresh-and-retry on the first 401 for `api2thread`

**Reuse the shipped ladder; add an awaitable entry point.** The existing core stays exactly as it is; it
gains a return value and a second, awaitable door.

**(a) Make the ladder report its outcome and be awaitable by a concurrent caller.**
`RecoverFromAuthFailureAsync` becomes `Task<bool>`. The `int _refreshingCookies` flag is replaced by a
lock-guarded shared `Task<bool>? _recoveryTask`, so that:

- the SIP path keeps its fire-and-forget behavior (`TriggerCookieRecovery` becomes a thin wrapper — one
  helper, two entry points, per the brief);
- a data-plane caller that arrives *while a recovery is already running* **awaits that same recovery**
  rather than being turned away with `false`. This matters: during a blackout the poller and several
  Radio Console requests hit 401 within milliseconds of each other, and all of them should ride the one
  refresh.

**(b) Expose it on the existing seam.** Add one method to `IGvAuthenticatedClientProvider`
(`Clients/IGvAuthenticatedClientProvider.cs`), which `GVApiAdapter` already implements and which every GV
client already holds (DI: `Extensions/GVBridgeServiceExtensions.cs:25-28` registers `GVApiAdapter` as a
singleton and maps the interface to that same instance):

```csharp
Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default);
```

This is deliberately preferred over the alternatives: a `DelegatingHandler` hook in `GvHttpClientHandler`
would fire on write paths too (see below), and injecting the concrete `GVApiAdapter` into
`GVBridge.Clients` would break the seam that the ADR §1.3 comments exist to protect.

**(c) Retry on read paths only — one retry, at the raw call site.** In `GvThreadClient.ListRawAsync`, on
`401`/`403` only: call `TryRecoverAuthAsync`, **re-resolve** the client via `GetAuthenticatedClient()`, and
replay the request exactly once.

- Re-resolving is not optional: ladder rungs 1 and 2 **dispose and re-create** `_httpClient`
  (`GVApiAdapter.cs:395`, `:689`). A retry that reuses the captured instance would throw
  `ObjectDisposedException`.
- `ListRawAsync` is the single shared read path for threads, SMS and voicemail — one patch covers the
  entire blackout surface.
- The `_http`-injected test constructor path must **not** retry (no provider to recover through).

**Write paths are explicitly excluded.** `GvSmsClient.SendAsync` and `GvReadStateClient.PostOneAsync` carry
`// Callers MUST NOT auto-retry on failure (ADR §4.2 #4)` (`GvSmsClient.cs:26`, `GvReadStateClient.cs:24`) —
replaying a send or an `updateread` risks a duplicate irreversible write. They may *signal* recovery (fire
`TryRecoverAuthAsync` without awaiting) so the next call is healthy, but they never replay. This is a
deliberate, ADR-grounded asymmetry, not an oversight.

**Storm control.** Single-flight (shared task) covers the concurrent case. For the sequential case — Google
genuinely down, every poll 401ing — add a **failure-only cooldown**: after a ladder run that returns
`false`, suppress new ladder runs for `AuthRecoveryFailureCooldownSeconds` (new config, default **60**).
A *successful* run sets no cooldown. Without this, a hard outage would drive `RotateCookies` at the poll
rate, which is how the 2026-06-19 incident started.

**Absorbs a pre-existing deferred item.** `docs/plans/gv-voicemail-sms-arc.md` lines 108-112 (and open
decision #6) record a PR1 review HIGH-2 deferred *to Planner*: `GetAuthenticatedClient()` gates on
`IsAvailable` (`GVApiAdapter.cs:148`), so a successful rung-1 rotation leaves `IsAvailable=false` until the
next 30-minute health tick, and the seam returns `null` despite a valid client. That is the same recovery
window this workstream operates in, and it would silently defeat the retry. **B2 is the right home for it:**
a successful ladder run now calls `SetAvailable(true)`. This closes arc open-decision #6.

### 4.3 Ask 3 — honest status during a blackout

**Principle applied:** derive health from *the last real call*, keep the probe as a secondary signal.

Add data-plane outcome tracking to `GVApiAdapter`, fed from the same `ListRawAsync` site (success and
401/403), and expose it:

| Field | Meaning |
|---|---|
| `lastApiSuccessAt` | UTC of the last 2xx from a real GV data-plane call |
| `lastApiAuthFailureAt` | UTC of the last 401/403 from a real GV data-plane call |
| `authBlackout` | `true` when `lastApiAuthFailureAt` is more recent than `lastApiSuccessAt` |

`AreCookiesValid` becomes `probeResult && !authBlackout`, so `cookiesValid` goes false the moment a real
call is rejected, and `Degraded` (already derived from it) follows automatically. Existing field **names**
are preserved — the DTO is extended append-only with defaults, exactly as the throttle and watchdog work
did (`Api/GvBridgeDtos.cs:32-39`), so `GetStatus_ReturnsAllFourFields` and
`GetStatus_IncludesWsConnectedAndLastConnectedAt` keep passing.

**Deliberate deviation from Radio Console's ask, to be stated in the handoff reply.** They asked for
`available:false` **and** `degraded:true` during a blackout. We will ship **`degraded:true` (plus
`cookiesValid:false` and the new `authBlackout:true`) but will *not* flip `available:false`.**

Reason: `IsAvailable` is load-bearing *inside* this service — `GetAuthenticatedClient()` returns `null`
when it is false (`GVApiAdapter.cs:148`), so flipping it during a transient data-plane 401 would make the
adapter refuse its own recovery retry and convert a 9-minute blackout into a hard stop. `available` means
"this adapter is the active call path and is wired up"; `degraded` means "it is not currently usable".
Radio Console's reconnecting banner should bind to `degraded` (or `authBlackout`), which is the field that
actually carries the fact they want. This is a one-line change on their side and it makes both services'
semantics correct rather than one of them convenient.

`psidtsAgeSeconds` stays as-is and is **not** promoted into the health derivation. It is an age heuristic;
the whole lesson of this defect is that the real call outcome outranks any inference. It remains useful
context on the dashboard.

---

## 5. Non-goals

- **B1 (`%2F` thread-id decoding).** In flight in parallel on `fix/gv-threadid-decode`. Out of scope here.
- **Radio Console's GV-8** (client-side error state). Theirs makes the failure honest; ours makes it rare.
  Neither subsumes the other. No changes proposed to their side.
- **Retiring the external ~20-minute scheduler.** Diagnosed here, but it lives in box-side units/scripts
  outside this repo. Owner action; flagged in the handoff reply.
- **Fixing `GvThreadPoller`'s 401-doesn't-engage-backoff gap** (F4). Real but separable; once §4.2 lands the
  poller's 401s are self-healing, which removes the urgency. Recorded as a follow-up, not built here.
- **Making `RotateCookies`' request shape verified.** Still best-effort/UNVERIFIED per
  `docs/research/gv-protocol-notes.md` §3.2. §4.1 leans on it as *primary* but the ladder already falls
  through to rungs 2/3 when it no-ops, and Task 0's diagnostic reports its live success rate.
- **Any BT/audio change.** None.

---

## 6. Constraints carried into the plan

- **Box health.** `radio` is an Intel N100 with a documented correlation between journald/SSH churn and
  audio distortion. Every diagnostic must be a **bounded** `journalctl --since … -n …` read. **Never tail.**
  No `-f`, no unbounded greps, no long-lived SSH sessions.
- **B1 confounder.** Until `fix/gv-threadid-decode` lands, any UAT of B2 must use **non-group** threads
  only (e.g. `t.32665`, `t.+18019208129`). Group threads (`g.Group Message.*`) return 200-with-empty for a
  *different* reason and would be scored as false failures.
- **Window awareness (pre-fix only).** Until this lands, UAT must record wall-clock time against the
  ~20-minute cycle and test within ~10 minutes of a `CDP cookie refresh` log line, or results look random.
  **Retiring that discipline is itself the acceptance criterion** — post-fix, a test at an arbitrary
  wall-clock time must pass.
- **Contract stability.** `/api/gvbridge/status` field names `available`, `activeMode`, `sipRegistered`,
  `cookiesValid` are an established cross-service contract. Append only.
- **No auto-retry on GV write paths** (ADR §4.2 #4).

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| **Double refresh** — in-process timer plus a still-running external scheduler doubles `RotateCookies` volume | Task 0 diagnoses before Task 4 installs the timer; `CookieRefreshIntervalMinutes: 0` is a kill switch; handoff reply flags the owner action |
| **Refresh storm on a real Google outage** | Shared-task single-flight + failure-only cooldown (default 60 s) + `IsThrottled` gate |
| **Retry amplification** — each blackout request now costs 2 upstream calls | Exactly one retry, read paths only, and only on 401/403. Note the handoff's §Notes amplification (mark-read costs 2-3 calls/click) is bounded by `EnableMarkRead`, which is `false` in this repo's `appsettings.json:*` |
| **`ObjectDisposedException` on retry** — rungs 1/2 dispose `_httpClient` | Retry re-resolves via `GetAuthenticatedClient()`; never reuses the captured instance |
| **Interface change ripples** — new member on `IGvAuthenticatedClientProvider` | Only `GVApiAdapter` implements it in `src/`; test fakes need one added member. Verified: no other production implementer |
| **`RotateCookies` shape is UNVERIFIED** | Ladder falls through to rungs 2/3 on no-op; Task 0 measures the live success rate so §4.1 isn't built on sand |
| **Proactive rotation invalidating a live SIP registration** | Proactive path deliberately does not re-register; UAT step 6 watches for REGISTER churn |
| **Re-entrancy fix drops an in-flight call** — tearing down `_sipTransport` on re-activation could kill an active call | Skip the transport rebuild when `_activeCallId != null`; refresh cookies/HttpClient only. Covered by a unit test |
| **Re-entrancy fix regresses the external refresher's contract** — cookie adoption must keep working | Fix is dispose-then-rebuild, not a no-op guard; UAT step 3 explicitly re-runs `refresh-from-browser` and asserts new cookies are adopted |

---

## 8. Open questions for the owner

1. **Interval value.** Spec proposes **8 minutes** (§4.1). Accept, or prefer 5 (matches the currently-inert
   config value and the handoff's framing) or 10 (minimum call volume, thinner margin)?
2. **External scheduler.** Once Task 0 identifies it, do you want it retired on the box as part of this
   work's UAT, or left running (accepting double refresh) until a separate box-side change?
3. **`available:false`.** §4.3 declines Radio Console's literal ask for a stated technical reason and offers
   `degraded`/`authBlackout` instead. Confirm before the handoff reply goes back to them.
4. **Stale plan doc.** §3 recommends committing `gv-websocket-keepalive-reconnect.md` with a
   `SHIPPED — PR #36` banner rather than deleting it. Confirm.
5. **Scope expansion for F6/F7.** §4.1 pulls the `ActivateAsync` re-entrancy leak into this PR, with
   reasoning. It is the one item here that was *not* in Radio Console's ask. Accept it in scope, or split
   it into its own PR that lands **first** (the proactive timer in Task 4 depends on it)? Splitting is
   defensible — it is an independent correctness bug with its own UAT — but it must not land *after*.

---

## 9. Acceptance criteria

1. `CookieRefreshIntervalMinutes` demonstrably governs a real refresh cadence; setting it to `0` disables it.
2. A 401 from `api2thread/list` triggers at most one shared cookie recovery and exactly one replay, and the
   originating request returns **200** rather than 502 when recovery succeeds.
3. `/api/gvbridge/status` reports `degraded:true`, `cookiesValid:false`, `authBlackout:true` while
   `api2thread/list` is 401ing — verified by measurement, not by inspection.
4. A live UAT pass at an **arbitrary** wall-clock time (no 20-minute window awareness) shows 0 502s across
   a 30-minute soak on non-group threads.
5. `journalctl -u rotary-phone --since '-60min' | grep -c 'api2thread/list returned Unauthorized'` drops
   from ~40/hour (62 per 90 min observed) to a small number, ideally 0.
6. Re-running `POST /api/gvbridge/cookies/refresh-from-browser` twice still adopts the new cookies, and
   leaves exactly **one** live health-check timer and **one** live `GvSipTransport` (F6/F7).
7. Existing status-contract tests still pass unchanged.
