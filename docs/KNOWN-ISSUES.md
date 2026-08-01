# Known Issues

## GV SMS/voicemail 502s in a repeating ~9-minute dead window (RESOLVED 2026-08-01)

**Status:** ✅ Resolved by the B2 auth-blackout PR (`fix/gv-auth-blackout`). **Live on-box soak pending
at merge time** — see "Verify" below.
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
- **`ActivateAsync` is re-entrant.** A re-activation now tears down the previous generation via the
  existing `DeactivateAsync` before rebuilding — except while a call is active, where it adopts the new
  cookies and leaves the transport alone rather than dropping the call.
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
- `POST /api/gvbridge/cookies/refresh-from-browser` twice should log
  `re-activating — tearing down the previous generation first` and leave **one** health-check timer and
  **one** `GvSipTransport` (the F6/F7 gate).
- If a 502 does occur, `curl -s localhost:5004/api/gvbridge/status` should show `degraded:true`,
  `cookiesValid:false`, `authBlackout:true` — and `available:true`, **by design**.

**Caveats:**
- The box-side cron is **deliberately still running**; double refresh is an accepted cost until it is
  retired as a separate change with its own rollback story. The in-process refresh is single-flighted
  and idempotent so the two interleave safely. **Do not remove that cron casually** — if the new path
  turns out inert, it is the only thing keeping GV authenticated.
- `RotateCookies`' request shape is still **UNVERIFIED** (`docs/research/gv-protocol-notes.md` §3.2). If
  rung 1 silently no-ops live, the proactive timer is inert and the reactive 401 path carries the whole
  fix — still a correct outcome, but the cadence claim would not hold.

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
