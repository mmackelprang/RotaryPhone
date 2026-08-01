# Known Issues

## 🔴 ACTIVE OUTAGE — the box's Chrome Google Voice session is signed out (2026-08-01 19:36 EDT)

**Status:** 🔴 **OPEN — needs a human.** Nothing in the service can fix this; it needs an **owner re-login
at `voice.google.com` in the box's Chrome**. Discovered while deploying the canonical post-merge B2 build.

**Impact is wider than SMS/voicemail.** SIP registration resolves its credentials through the same
authenticated GV client, so a dead GV session takes the **whole phone** down, not just the data plane:

```
available:false  sipRegistered:false  wsConnected:false  cookiesValid:false
/api/gvbridge/sms/threads → 502
```

**How to confirm it (the obvious check lies).** The Chrome tab title read `Voice - (99+) Voicemail` and the
URL read `https://voice.google.com/u/0/voicemail` — both **stale cached renders** from before the session
died. The reliable test is to force a navigation and see where it lands:

```
https://voice.google.com/u/0/voicemail  →  redirects to  →  https://workspace.google.com/products/voice/
```

That redirect to the signed-out marketing page **is** the signed-out signal. A `RotateCookiesPage` tab was
also parked in the browser. Service-side, the tell is all three recovery rungs failing at once, which the
service already reports in plain language:

```
[WRN] RotateCookies returned 401 — falling back
[WRN] ReloadCookiesAsync: new cookies failed health check
[WRN] GVApi: all cookie-recovery rungs failed. The box's Chrome login may be dead —
      re-login at voice.google.com so the next CDP refresh can pick up a fresh session.
```

### The mechanism that turned a healthy service into a full outage in 5 seconds

**A `refresh-from-browser` against a signed-out Chrome overwrites a *working* cookie set with a dead one,
and the working set is then unrecoverable.** Captured exactly, from the restart log:

```
19:36:49 [INF] Listed 149 recent SMS messages          <- WORKING, cookies loaded from disk
19:36:49 [INF] Listed 50 voicemails from 50 raw threads
19:36:51 [INF] Cookies saved to data/gv-cookies.enc    <- 20 dead cookies overwrite the good set
19:36:51 [WRN] GV health check failed: Unauthorized
19:36:51 [INF] CDP cookie refresh: 20 cookies extracted and activated
```

The only copies of the good set were the old process's memory (gone on restart) and
`data/gv-cookies.enc` (overwritten two seconds later). Both pre-existing on-box backups
(`/opt/rotary-phone.bak.prefix-uat-20260801-160513/data/gv-cookies.enc`, 16:00) were tried and are **also
dead**, so the whole Chrome-derived lineage is invalid — this was a genuine account-session death, not a
local corruption.

### Why B2 makes this *more* consequential, not less — and what it means for the cron

Pre-B2, the app's cookies and Chrome's jar were kept in lockstep by the 20-minute cron, so pulling from
Chrome was near-harmless. **B2's in-process 8-minute refresh keeps the app's lineage alive independently**,
so the two lineages now **diverge** — the app can hold fresh, working credentials while Chrome's jar rots.
Once that is true, `refresh-from-browser` is no longer a harmless idempotent top-up: it is a **downgrade
path**, and the box-side cron fires it **every 20 minutes**.

> **This escalates finding M1 (retire the cron) from "redundant" to "standing hazard."** The cron's
> original justification — keep GV authenticated until the in-process refresh is proven — is not merely
> expired; the cron is now the mechanism most likely to *destroy* working credentials. Retiring it should
> be prioritized accordingly. It remains a box-side change needing its own rollback story.

**Proposed hardening (not implemented — needs its own change):**

- **Validate before adopting.** Health-check a newly extracted cookie set **before** persisting it and
  swapping it in. Today the order is adopt → persist → discover it fails.
- **Keep a last-known-good set and roll back** when the new set fails its health check, instead of leaving
  the adapter holding credentials already proven bad.
- **Never let an unvalidated refresh overwrite a validated set** — that single rule would have contained
  this outage to a logged warning.

**Recovery procedure:** re-login at `voice.google.com` in the box's Chrome (profile on `radio`, CDP port
9224), confirm the URL stays on `voice.google.com` rather than redirecting, then
`curl -X POST localhost:5004/api/gvbridge/cookies/refresh-from-browser` and restart `rotary-phone`.
Verify `sipRegistered:true` and `/api/gvbridge/sms/threads` → 200.


## ⚠️ OPEN — Deploying clobbers the box's `appsettings.Production.json`, including BT adapter config

**Status:** 🔴 **OPEN** — recurs on **every** deploy that falls back to the tar path. Found during PR #72
UAT (finding **L3**); the tester caught it and restored the file by hand.
**Why this is the most dangerous item on the list:** the clobbered values include
**`BluetoothAdapter: hci1`** and `UseActualBluetoothHfp`. **This crosses the Radio Console audio boundary**
(`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`) — a silent BT-config change on this side can break the
*other* service's audio, and nothing in the deploy surfaces that it happened. It will happen again on the
next deploy unless the tooling is fixed.

**Mechanism:**

1. The publish output **contains** `appsettings.Production.json` — the SDK's default `Content` glob picks
   up `appsettings*.json`, and nothing in `RotaryPhoneController.Server.csproj` excludes it. So the repo's
   *template* ships in the artifact alongside the binaries.
2. `deploy/Deploy-ToLinux.ps1` has two sync paths. The **rsync** path is safe — it passes
   `--exclude 'appsettings.Production.json'` (`:89`). The **tar-pipe fallback** (`:126-129`) is not: it
   relies on a backup/restore *around* the extract
   (`cp -f … /tmp/rp-prod.bak` … `tar -xzf - --unlink-first …` … `mv -f /tmp/rp-prod.bak …`).
3. Reproducing that tar-pipe **on Linux**, `--unlink-first` errors on directories, so `tar` exits **2**.
   With `set -e -o pipefail` the chain aborts **before** the restore `mv` runs — leaving the box on the
   repo's template config. The backup survives in `/tmp/rp-prod.bak`, but nothing puts it back.

The box's copy is **authoritative** (see `docs/HT801-ADDRESS.md`) and carries values the repo template
does not: `EnableMarkRead`, the GV number (`GvPhoneNumber: +1XXXXXXXXXX` — redacted; this repo is public),
and the HT801 address, in addition to the BT keys above.

**Proposed fix (not done in PR #72 — deploy tooling, needs its own change + rollback story):**

- **Primary:** add `--exclude=./appsettings.Production.json` to the `tar -C … -czf -` invocation in
  `deploy/Deploy-ToLinux.ps1`, matching what the rsync path already does. Then the file is never in the
  stream and the fragile backup/restore dance stops being load-bearing.
- **Belt and braces:** drop it from the publish output entirely — in
  `src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`, exclude
  `appsettings.Production.json` from `Content` (or set `CopyToPublishDirectory=Never`), so no artifact
  can carry a config that only the box should own.
- **Either way:** make the restore unconditional (run it in a `trap`/`||` rather than after a `set -e`
  command that can abort), and have the deploy **print** the post-deploy `BluetoothAdapter` value so a
  clobber is loud instead of silent.

**Until it is fixed — mandatory manual step on every deploy:** back up
`/opt/rotary-phone/appsettings.Production.json` **before** the sync and verify it **after**, explicitly
confirming `BluetoothAdapter` is still `hci1`. Restore it by hand if it changed.


## GV SMS/voicemail 502s in a repeating ~9-minute dead window (RESOLVED 2026-08-01)

**Status:** ✅ Resolved by the B2 auth-blackout PR (**#72**, `fix/gv-auth-blackout`), merged 2026-08-01.
**Live on-box soak PASSED** — 932 HTTP requests over ~88 minutes, **zero 502s, zero non-200s**, against a
pre-fix baseline of 15/49 (31%) 502s for the same shape. 6 of 7 acceptance criteria verified by
measurement, 1 partial, 0 failed. See "Verified live" and "Open verification items" below.
**Symptom (was):** Radio Console saw HTTP 502 from `/api/gvbridge/sms/*` in a clean repeating pattern —
roughly **9 dead minutes inside every ~20-minute cycle**. Upstream, `journalctl -u rotary-phone` showed
`api2thread/list returned Unauthorized for folder Sms` **271 times on 2026-07-31**, and zero
`TooManyRequests`. 11 of 11 of Radio Console's 502s fell inside a dead window.
**Impact (was):** Every SMS and voicemail read path, roughly 45% of the time. Invisible to
`/api/gvbridge/status`, which reported `cookiesValid:true`, `degraded:false` straight through the
outage — so the dashboard said healthy while the feature was dead.
**Not throttling.** Falsified early: a constant-rate poller showed the same on/off pattern, upstream
status was always 401 and never 429, and recovery landed on fixed wall-clock boundaries rather than
after a variable cooldown. This was an **auth-freshness** defect. Google's rotating
`__Secure-1PSIDTS` / `__Secure-3PSIDTS` cookies are good for about **11 minutes**.

**Root cause — seven findings, and the last two are the interesting ones:**

- **F1.** `CookieRefreshIntervalMinutes` was a **dead config knob** — declared, set in `appsettings.json`,
  and read by *nothing*. There was no proactive cookie/PSIDTS refresh timer in the service at all.
- **F2.** The only periodic auth mechanism was a **30-minute probe**, not a refresh — and it probed
  `threadinginfo/get`, a *different endpoint* from the `api2thread/list` that was failing.
- **F3.** The ~20-minute cadence came from **outside this process**: a box-side cron running
  `/opt/rotary-phone/refresh-gv-cookies.sh` every 20 minutes, POSTing
  `/api/gvbridge/cookies/refresh-from-browser`. A wall clock, which is why recovery was second-exact on
  20-minute boundaries.
- **F4.** The reactive-401 escalation **already existed but only on the SIP leg**.
  `GvThreadClient.ListRawAsync` collapsed *every* non-2xx to `null` with no status discrimination, so
  the SMS/voicemail data plane reached none of it. This is precisely why SIP recovered while SMS
  blacked out.
- **F5.** `_areCookiesValid` was a **cached probe result** — up to 30 minutes stale, of the wrong
  endpoint. `psidtsAgeSeconds: 781` (13m01s, already past the ~11-minute lifetime) was in the same
  payload: the endpoint was carrying the evidence of its own staleness and not using it.
- **F6.** `GVApiAdapter.ActivateAsync` was **not re-entrant**. `CallAdapterRegistry.SwitchModeAsync`
  skips `DeactivateAsync` when the mode is unchanged, so the cron's refresh re-entered `ActivateAsync`
  on the *live* adapter every ~20 minutes, each pass leaking an armed 30-minute `Timer`, an
  `HttpClient`, and a whole `GvSipTransport` (WebSocket + keep-alive timer + Opus codecs) with its
  event handlers still subscribed — **~72 leaked objects/day**.
- **F7.** …and therefore **the 30-minute watchdog was starved and effectively never fired.** Each
  refresh installed a *fresh* 30-minute timer; refreshes arrived every ~20 minutes; the newest timer
  never reached its due time. Since that watchdog was the **only timed entry into the recovery ladder
  in the entire service**, the deployed reality was that *the only thing that ever restored auth was
  the external cron*. There was no in-process recovery cadence — not a slow one, none.

**Fix:**
- **Proactive refresh is real.** `CookieRefreshIntervalMinutes` now governs an actual timer, defaulting
  to **8 minutes** (comfortably under the ~11-minute PSIDTS lifetime, without the 60% extra call volume
  that 5 would cost). Rung-1 only (browser-less `RotateCookies`) — CDP is heavy and stays reserved for
  reactive recovery. Setting it to **`0` disables the timer** — a kill switch with no redeploy. The
  proactive path deliberately does **not** re-register SIP (that would re-create the 2026-06-19
  REGISTER-storm risk).
- **Reactive refresh-and-retry on the read path.** On `401`/`403` only, `ListRawAsync` runs the shared
  recovery ladder, **re-resolves** the authenticated client (rungs 1 and 2 dispose and re-create it) and
  replays **exactly once**. `429`/`5xx`/network faults deliberately do not retry. Write paths
  (`sendsms`, `updateread`) *signal* recovery but **never replay** — ADR §4.2 #4 forbids auto-retry on
  irreversible GV writes.
- **One ladder, two doors.** `RecoverFromAuthFailureAsync` now reports its outcome and is guarded by a
  shared `Task<bool>` instead of an int flag, so concurrent callers **await the same run** rather than
  being turned away. SIP keeps its fire-and-forget entry point; the data plane gets an awaitable one.
  A **failure-only** 60-second cooldown (`AuthRecoveryFailureCooldownSeconds`) stops a real Google
  outage from driving `RotateCookies` at the poll rate.
- **`ActivateAsync` is re-entrant, and a healthy SIP transport is reused rather than rebuilt.** The
  decision is one predicate, `CanReuseTransport => _sipTransport?.IsRegistered == true`, evaluated after
  the incoming cookie set is loaded:
  - **registered + unchanged credentials** → total no-op; transport, `HttpClient` and both timers untouched.
  - **registered + changed credentials** (the common case — the cron pulls every 20 min while PSIDTS
    rotates every ~11, so the header differs on nearly every fire) → cookies adopted and clients rebuilt,
    **transport and timers kept**, deliberately **no re-register** (re-registering on a cadence re-creates
    the 2026-06-19 REGISTER-storm risk).
  - **absent or unregistered transport** → the full teardown via the existing `DeactivateAsync`, then
    rebuild — with an `_activeCallId` guard re-checked after timer disposal so a call that starts mid-
    teardown is never dropped.

  This is safe because `GvSipTransport` caches no credentials: it holds a `Func<Task<SipCredentials>>`
  invoked fresh on every register (`Sip/GvSipTransport.cs:1021`) that resolves the `HttpClient` lazily by
  field — recovery rung 2 already relied on it. **Reuse fixes F7 harder than a teardown would:** an
  unconditional teardown re-arms a fresh 30-minute health timer on every 20-minute cron fire, which is
  precisely the starvation shape F7 describes.
- **Honest status.** New `authBlackout`, `lastApiSuccessAt`, `lastApiAuthFailureAt` fields, written by
  the **real** data-plane calls rather than by a probe. `AreCookiesValid` became
  `probe && !authBlackout`, so `cookiesValid` — and `degraded`, which derives from it — go false the
  moment a real call is rejected. Field names are append-only; the four contract names are untouched.
- **`available` deliberately stays `true` during a blackout.** Radio Console asked for `available:false`;
  we declined for a concrete reason. `GetAuthenticatedClient()` gates on `IsAvailable` and returns
  `null` when false, so flipping it during a transient 401 would make the adapter **refuse its own
  recovery retry** — turning a 9-minute blackout into a hard stop. Bind status UIs to `degraded` or
  `authBlackout` instead.

**Verify:**
- `journalctl -u rotary-phone --since '-60min' -n 5000 --no-pager | grep -c 'api2thread/list returned Unauthorized'`
  should drop from ~40/hour toward 0. **Bounded reads only — never `-f`, never `tail -f`** (the box is
  an N100 shared with Radio Console and journald churn correlates with audible audio distortion there).
- Poll `GET /api/gvbridge/sms/threads` every 60 s for 30 minutes starting at an **arbitrary** wall-clock
  time — deliberately *not* aligned to a `CDP cookie refresh` line. Expect **zero 502s**. The retirement
  of the old "test inside a healthy window" discipline *is* the acceptance criterion.
- `proactive PSIDTS refresh succeeded` should appear at ~8-minute intervals.
- `POST /api/gvbridge/cookies/refresh-from-browser` twice should leave **one** health-check timer and
  **one** `GvSipTransport` (the F6/F7 gate). **⚠️ The log line to expect is inverted from the original
  plan text.** With SIP registered, expect
  `re-activation adopting new credentials — SIP transport is healthy, keeping it` (or
  `re-activation is a no-op …`), and `sipRegistered` must stay **true** across both refreshes with
  `lastConnectedAt` holding a single distinct value.
  `re-activating — tearing down the previous generation first` is correct **only** when the transport is
  absent or unregistered; seeing it while SIP is registered is a **failure**. Scoring this by the
  pre-amendment text turns a passing run into a false failure — see finding M2 on PR #72.
- If a 502 does occur, `curl -s localhost:5004/api/gvbridge/status` should show `degraded:true`,
  `cookiesValid:false`, `authBlackout:true` — and `available:true`, **by design**.

**Verified live (on-box UAT, 2026-08-01 16:05–17:36 EDT, PR head `b5b8444`):**

| # | Acceptance criterion | Result |
|---|---|---|
| 1 | `CookieRefreshIntervalMinutes` governs a real cadence; `0` disables it | ✅ 7 ticks exactly 8m00s apart; `0` produced zero proactive lines in 25 min while reactive recovery still worked |
| 2 | One shared recovery, exactly one replay, caller gets 200 | ✅ full ladder captured live at 17:32:53 — rung 1 401 → rung 2 fail → rung 3 CDP → replay 200 |
| 3 | Honest status during a 401; `available` stays true | ⚠️ **PARTIAL** — see Open verification items |
| 4 | Window-blind 30-min soak, 0 × 502 | ✅ 932 requests, **0** non-200 |
| 5 | `api2thread/list returned Unauthorized` drops toward 0 | ✅ **33/hr → 0/hr** |
| 6 | F6/F7 leak gate: one health timer, one `GvSipTransport` | ✅ stronger than asked — one transport across **6** re-activations, and the health timer ticked twice exactly 30 min apart on its original anchor (**F7 fixed, measured**) |
| 7 | Existing status-contract tests pass unchanged | ✅ |

`RotateCookies` (rung 1) is **not inert** — it rotated for real 5 times — but its usefulness splits by
cookie freshness: it works **proactively** (fresh cookies), and returns 401 **reactively** (already-stale
PSIDTS), where CDP carries the recovery. The design's layering is validated by evidence rather than
assumption. This resolves the "UNVERIFIED request shape" caveat previously carried here.

**Open verification items (not defects — merged knowingly):**

- ⛔ **Inbound call ringing was never tested for this change.** The tester had no way to originate a call
  to the GV number, so test-plan step 8 did not run. This matters because **Task 3 touches `_sipTransport`
  teardown**, and because conditional reuse makes teardown *rare*, any ringing regression would be
  **intermittent and hard to trace** — it would only surface on the path where the transport was absent or
  unregistered (restart, dropped WebSocket). Proxies were good throughout (`sipRegistered`/`wsConnected`
  true across 411 samples, one transport surviving six re-activations, and a server-side WebSocket close at
  17:07 that auto-recovered within the same second), but an actual ring is unverified. **Action: ring the
  phone once and confirm two-way audio.**
- ⚠️ **AC-3 is partial: `authBlackout:true` was never observed live.** Zero `authBlackout:true` samples
  across 411 status polls — not because the flag is broken, but because **recovery is faster than any
  practical sampling rate**: the one live blackout lasted **920 ms** (`lastApiAuthFailureAt 21:32:53.529`
  → `lastApiSuccessAt 21:32:54.449`) against a 4-second sampling floor. What *is* verified live:
  `available` never went false, and `lastApiAuthFailureAt` latched from a genuine data-plane 401. What
  remains **unit-test-only**: the derived `authBlackout` / `cookiesValid:false` / `degraded:true` trio
  during a *sustained* blackout.
  **⚠️ Radio Console must be told: `authBlackout` may be true for well under a second.** A reconnecting
  banner bound naively to it will effectively never appear. Bind to it only with a minimum-display or
  debounce window, or drive the UI from a sustained-failure signal instead. This is in the handoff reply.

**Follow-ups (do NOT do these in the B2 PR):**

- **Retire the box-side cron — its justification has expired.** Resolved decision 2 kept
  `*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh` running "until the in-process refresh is proven."
  **It is now proven:** 33/hr → 0 `Unauthorized`, 932 requests with zero 502s, and a measured 8-minute
  proactive cadence. Meanwhile the accepted double-refresh cost (spec §8.2) has **materialized as
  measurable 429s on 29% of proactive ticks** (2 of 7) — the 8-minute timer, the 20-minute cron and
  reactive recovery together exceed what `accounts.google.com/RotateCookies` will serve. It degrades
  gracefully (warns, returns `NotRotated`, backstop intact, no 502s resulted), so this is not urgent.
  **This is a box-side change and needs its own rollback story** — retire the cron (or raise its interval),
  then re-measure the 429 rate. (Finding **M1**, PR #72 UAT.)
  > 🔴 **ESCALATED the same day — see the ACTIVE OUTAGE entry at the top of this file.** The cron is not
  > merely redundant now. Because B2's in-process refresh keeps the app's cookie lineage alive
  > **independently of Chrome's jar**, the two diverge — and a `refresh-from-browser` against a stale or
  > signed-out Chrome **overwrites working credentials with dead ones**. The cron fires exactly that path
  > every 20 minutes. Treat retiring it as **hazard removal**, not a tidy-up.
- **Path A (`re-activation is a no-op`) is dead code in production.** It fired **0** times in 6
  re-activations — every cron fire carries changed credentials, exactly as predicted. Correct and
  unit-tested, but never exercised on the box. Worth knowing before anyone relies on it. (Finding **L1**.)
- **`psidtsAgeSeconds` resets on activation regardless of the cookies' true issue time**
  (`_psidtsRefreshedAt = DateTime.UtcNow` on load) — it read `6` right after a restart whose on-disk PSIDTS
  was ~7 minutes old. Pre-existing, not introduced by B2, but B2's re-activation path hits it more often,
  so the field is a **less trustworthy staleness signal** than the pre-fix traces implied. (Finding **L2**.)

**See:** [`docs/plans/gv-auth-blackout-b2-design.md`](plans/gv-auth-blackout-b2-design.md) (findings
F1-F7, design, owner decisions), [`docs/plans/gv-auth-blackout-b2-plan.md`](plans/gv-auth-blackout-b2-plan.md)
(task breakdown + test plan), [`docs/handoffs/radioconsole-gv-auth-blackout-reply.md`](handoffs/radioconsole-gv-auth-blackout-reply.md)
(cross-repo reply, including the `available` vs `degraded` ask).


## UI says "Ringing" but the bell never rings — INVITE sent to a stale HT801 address (RESOLVED 2026-07-29)

**Status:** ✅ Resolved by the config-binder fix (PR #67, `fix/ht801-invite-target`) and hardened by the
registrar-binding PR (`feat/ht801-registrar-binding`).
**Symptom (was):** An inbound call showed **Ringing** in the Radio.Web UI for the full 60-second window
while the physical rotary phone bell stayed silent. Nothing on screen, in the API, or in the logs said
anything was wrong. `/api/phone/system-status` reported the *correct* HT801 address throughout.
**Impact (was):** Every inbound call. The condition persisted for months undetected because the only
obvious verification signal was the one signal that could not see it.
**Root cause:** `AppConfiguration.Phones` was pre-seeded with one element carrying a hardcoded
`192.168.86.22`, and .NET's `ConfigurationBinder` **appends** to a non-null `List<T>` rather than
replacing it or binding into existing elements. A single-phone config therefore bound to *two* phones —
the compiled default first, the real configuration second — and `PhoneManagerService` registration was
first-wins, so it kept the hardcoded one and discarded the real entry with a single
`Phone default is already registered` warning. Every INVITE went to `.22`. **No edit to any
configuration file could fix it**, because the stale value was in the binary, not the config.
Meanwhile `/api/phone/system-status` read a *different*, last-wins projection (`HT801ConfigService`)
and truthfully reported the configured `.240` — a value that had nothing to do with the INVITE target.
**Fix:**
- **PR #67 (bell restoration):** `Phones` starts empty; `Program.cs` fails fast via a new
  `AppConfigurationValidator` instead of re-seeding a default phone; `PhoneManagerService` throws on a
  duplicate phone Id instead of silently keeping the first. Regression test `ConfigurationBindingTests`
  exercises the real binder plus the real `PhoneManagerService` and fails on the pre-fix code.
- **PR2 (durable):** no site-specific HT801 address anywhere in source; startup validation also rejects
  a missing/unparseable address or extension; the service **learns** the HT801's address from the source
  address of its SIP REGISTER and prefers that fresh binding over configuration, so a DHCP move
  self-heals within one registration interval; `GVBridge:HT801Ip` deleted so there is exactly one
  address key; new `GET /api/diagnostics/sip-registrations` reports where INVITEs will actually go.
**Verify (and how NOT to):** use `INVITE target endpoint: udp:<ip>:5060` in the journal, the
`Learned registrar binding:` line, and `/api/diagnostics/sip-registrations`. **Do not use
`/api/phone/system-status` → `ht801IpAddress`** — it reports the configured projection, not the INVITE
target, and reported the correct address for the entire duration of this bug.
**See:** [`docs/HT801-ADDRESS.md`](HT801-ADDRESS.md) (address locations, change procedure, verification),
[`docs/plans/ht801-address-resolution-and-config-binder-fix.md`](plans/ht801-address-resolution-and-config-binder-fix.md)
(full analysis, including the empirical binder repro).

## Outbound: bridge started at placement → errno-101 blip + early-audio clipping (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the outbound InCall-ordering PR (`fix/outbound-incall-ordering`).
**Symptom (was):** On an outbound call (rotary → cell), the HT801↔GV audio bridge started and the
state flipped to `InCall` at call *placement* — roughly 6–10s before the far end actually answered.
This streamed audio while the far end was still ringing (potential clipped first syllable) and produced
a one-shot `errno-101` "Network is unreachable" cold-send blip as RTP was pushed before the peer was up.
The genuine answer signal (GV `CallStatusType.Active` → `OnCallAnswered`) was ignored for outbound
because the answer handler guarded on `Ringing` and the call was already `InCall`.
**Note:** This was NOT the 0-RTP / one-way-audio bug — that was fixed separately by the HT801
`Content-Type: application/sdp` fix (PR #35) and the outbound-RTP-port-from-INVITE-SDP fix (PR #34),
both shipped and UAT-verified. This ordering fix is purely about *when* the (working) bridge starts.
**Fix:** `PlaceGvCallAsync` now stays in `Dialing` after sending the GV INVITE (stashing the negotiated
RTP details), and defers both the bridge-start and the `InCall` transition to the GV-answered path.
`HandleCallAnsweredOnCellPhone` gained an outbound-`Dialing` branch that starts the bridge and goes
`InCall` when `Active` arrives — mirroring the proven BT outbound path (`HandleDeviceCallActive`). A
~45s outbound no-answer timeout resets a never-answered call cleanly to `Idle`. The bridge-start is
idempotent (guarded by `_outboundConnectPending`) so a duplicate `Active` (e.g. re-INVITE 200 OK)
starts it at most once.
**Verify in UAT:** Outbound two-way audio still works; no early audio/clipping before answer;
`State changed to: InCall` now logs at answer time (not ~6–10s earlier at placement); the `errno-101`
cold-send blip is gone (or, if present, a single benign blip). Inbound ring + answer unaffected.

## GV BYE not terminating calls (2026-05-25)

**Status:** Workaround in place (SRTP media teardown forces Google timeout)
**Impact:** When hanging up the rotary phone, the cell phone call ends after ~5-10 seconds (Google media timeout) instead of immediately.
**Root cause:** Our SIP BYE over WebSocket is structurally correct but Google's SIP proxy silently ignores it. Likely a dialog state mismatch (From/To tags, Contact URI, or CSeq) that requires proper SIP tracing to diagnose.
**Workaround:** On hangup, `GvSipTransport.HangupAsync()` now closes the DTLS-SRTP `RTCPeerConnection` immediately (sending `close_notify`) before sending the SIP BYE. This stops all media flow, and Google's RTP timeout detection terminates the far-end call within 5-10 seconds.
**Next step:** Set up WebSocket frame capture to compare our BYE with what Google's own web client sends when terminating a call. Compare headers field-by-field.
**PRs:** #25 (initial BYE), #26 (CSeq fix), #27 (diagnostic logging), #28 (session race fix), #29 (force media teardown)

## Idle SIP WebSocket never reconnects → inbound calls stop ringing (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the keep-alive / auto-reconnect / honest-status PR (`fix/gv-ws-keepalive-reconnect`).
**Symptom (was):** After the line sat idle for ~256s, Google closed the idle SIP-over-WebSocket signaling socket. The receive loop just `break`d with no event and no reconnect, so inbound `INVITE`s never arrived and the rotary phone never rang — yet `/api/gvbridge/status` still reported `sipRegistered:true` on the dead socket.
**Root cause:** No keep-alive was sent (Google advertises `keep=240` in the REGISTER 200-OK Via per RFC 6223), the channel raised no `Closed` event, and `GvSipTransport._registered` was never reset on socket death.
**Fix:**
- **Keep-alive (primary fix):** parse the RFC 6223 `keep=` frequency from the REGISTER 200-OK first Via (default 120s) and send the RFC 5626 §3.5.1 double-CRLF (`\r\n\r\n`) ping every `max(15, keep/2)`s, plus a secondary protocol-level `ClientWebSocket.Options.KeepAliveInterval` (defense-in-depth). A failed ping is treated as a dropped link and triggers reconnect.
- **Auto-reconnect:** the channel now raises a `Closed` event (with a `WasIntentional` flag); the transport runs a single-flight (`Interlocked`-guarded) reconnect loop with capped exponential backoff (1,2,4,8,16,30s) + ±20% jitter, retrying indefinitely until success or disposal, reusing the existing `RegisterAsync` path. The old channel is disposed and its handlers unsubscribed before a new one is created (fixes a latent handler/channel leak).
- **401 auth-recovery:** a real post-Digest 401/403 (or a 401/403 from `sipregisterinfo/get`) now escalates to a browser-less `RotateCookies` refresh of the rotating `__Secure-1PSIDTS/3PSIDTS` (primary), falling back to the CDP `cookies/refresh-from-browser` flow. Plain network drops do NOT trigger cookie work. (RotateCookies request shape is best-effort / unconfirmed — see `docs/research/gv-protocol-notes.md` §3.2 and the `GvCookieRotator` TODO.)
- **Honest status:** `IsRegistered` is now `registered AND socket-connected`; `/api/gvbridge/status` adds `wsConnected`, `lastConnectedAt`, and `psidtsAgeSeconds` (the original four field names are unchanged).
**Next step:** Confirm the exact `RotateCookies` request shape for the voice.google.com origin via a packet capture and tighten `GvCookieRotator` (fast-follow).
