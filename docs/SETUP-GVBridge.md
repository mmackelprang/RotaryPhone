# GV Bridge — Setup Guide

**Last updated:** September 25, 2026

This guide covers setting up the GV Bridge system on a fresh Ubuntu box. The GV Bridge enables incoming Google Voice calls to ring a physical rotary phone connected via a Grandstream HT801 ATA.

## Architecture

Two independent paths share the box. They are worth keeping apart in your head, because
only one of them still involves the browser.

**Calls — no browser involvement:**

```
Google Voice call
  -> SIP over WebSocket to the .NET RotaryPhoneController server
  -> CallManager sends SIP INVITE to HT801
  -> HT801 rings the rotary phone
  -> User picks up -> 200 OK -> call connected
  -> Audio flows both ways over DTLS-SRTP (SIPSorcery)
```

**SMS and voicemail — the browser holds the session, nothing more:**

```
gv-bridge-ensure.sh
  -> Google Chrome, dedicated profile, CDP on :9224
  -> holds ONE authenticated voice.google.com session
  -> cron (*/20) POSTs /api/gvbridge/cookies/refresh-from-browser
  -> the API reads that session's cookies over CDP
  -> the API calls Google's HTTP API directly for SMS + voicemail
```

The Chrome extension is **superseded** and is not loaded — see
[The extension is no longer in the path](#the-extension-is-no-longer-in-the-path).

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Ubuntu | 24.04+ | Tested on Ubuntu 24.04 (x64) |
| .NET SDK | 10.0 | For building from source |
| Google Chrome | 137+ | `google-chrome` on PATH. Chromium is no longer used. |
| Grandstream HT801 | Firmware 1.0.5+ | Factory reset recommended before setup |
| Google Voice account | — | With a phone number |

## Hardware Setup

### HT801 ATA Configuration

1. Connect HT801 to your LAN and note its IP address
2. Log into the web interface at `http://<HT801-IP>`
3. Go to **Port Settings** and configure:
   - **SIP Server**: `<radio-box-IP>` (e.g., `192.168.86.50`)
   - **SIP User ID**: `1000`
   - **SIP Transport**: `UDP`
   - **SIP Registration**: Checked
   - **Enable SIP NOTIFY Authentication**: Unchecked
4. Verify **Port Status** shows "Registered"

**Important:** If the HT801 was previously configured, do a **factory reset** first. Hidden state can cause incoming SIP to be silently dropped even when settings appear correct.

### Rotary Phone

Connect the rotary phone to the HT801's FXS (Phone) port using a standard RJ11 cable.

## Deployment

### Quick Deploy (from Windows build machine)

```powershell
# Build and deploy to the radio box
.\deploy\Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64

# Run the GV Bridge setup on the radio box
ssh mmack@radio "bash /opt/rotary-phone/deploy/setup-gvbridge.sh"
```

### What the setup script does

1. Verifies Google Chrome is installed (it will not install a browser for you)
2. Installs `gv-bridge-ensure.sh` and `gv-bridge-restart.sh` into `~/bin`
3. Installs the watchdog and nightly-restart systemd **user** units
4. Enables the 2-minute watchdog timer, which brings the bridge up if it is down
5. Installs the login autostart entry
6. Creates a desktop shortcut that runs the ensure script

No `sudo` is required for any of it. Pass `--with-legacy-extension-service` to also
provision the superseded snap-Chromium configuration; it is installed but never enabled.

### What starts automatically on boot

| Unit | Type | Starts | Enabled by setup |
|---|---|---|---|
| `rotary-phone.service` | System (systemd) | On boot | yes |
| `gv-bridge-watchdog.timer` | User (`systemctl --user`) | 2 min after boot, then every 2 min | **yes** |
| `gv-bridge-restart.timer` | User (`systemctl --user`) | Nightly 04:00 | no — installed, left disabled |
| `~/.config/autostart/gv-bridge-chrome.desktop` | GNOME autostart | 15 s after login | yes |

The watchdog is what actually keeps the bridge alive. `gv-bridge-ensure.sh` is idempotent
by contract: it looks for a process carrying this profile's `--user-data-dir` and exits 0
without touching anything if it finds one, so running it every 2 minutes costs nothing.

## First-Time Setup (one-time steps)

After deploying and running the setup script:

### 1. Bring the bridge up

```bash
~/bin/gv-bridge-ensure.sh
```

Or wait up to 2 minutes for the watchdog, or click the **"GV Bridge"** desktop shortcut.

### 2. Log into Google Voice

The window is placed by the Wayland compositor — `--window-position` is a no-op there, so
raise the window from the GNOME overview rather than editing coordinates. Sign in with the
Google account that owns the Voice number.

### 3. Confirm CDP is answering

This is what everything else depends on:

```bash
curl -s http://localhost:9224/json/version
```

### 4. Confirm cookie extraction works end to end

```bash
curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser \
  -H 'Content-Type: application/json' -d '{}'
# Expected: {"refreshed":true,"cookieCount":<n>}
```

Anything else here means SMS and voicemail lists will come back empty and the API will log
"authenticated client unavailable".

### 5. Verify

```bash
curl -s http://localhost:5004/api/gvbridge/status
curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool
curl -s http://localhost:5004/api/diagnostics/audio-bridge
```

### Optional: enable the nightly recycle

Off by default. Enable it if renderer heap growth becomes a problem:

```bash
systemctl --user enable --now gv-bridge-restart.timer
```

## Configuration

### appsettings.Production.json

The GVBridge section (update IPs for your network):

```json
{
  "GVBridge": {
    "WebSocketPort": 8765,
    "WebSocketHost": "127.0.0.1",
    "LocalRtpPort": 5070,
    "LocalIp": "0.0.0.0",
    "HT801RtpPort": 5004,
    "AudioSampleRateHz": 16000,
    "AudioChannels": 1,
    "PcmFrameMs": 20,
    "ExtensionConnectTimeoutSeconds": 30,
    "CallLogDbPath": "/opt/rotary-phone/data/gvbridge-calllog.db",
    "DefaultMode": "GVBrowser"
  }
}
```

> **There is no `GVBridge:HT801Ip` key.** It was removed — the HT801's address has exactly one
> home, `RotaryPhone:Phones[].HT801IpAddress`, and at runtime the address learned from the device's
> own SIP REGISTER wins over it. See **[docs/HT801-ADDRESS.md](HT801-ADDRESS.md)** for the one place
> to change the address and how to verify it correctly.

### Key files on the radio box

| Path | Purpose |
|------|---------|
| `/opt/rotary-phone/` | Main application |
| `~/bin/gv-bridge-ensure.sh` | Launch-if-down. Idempotent; the watchdog runs it every 2 min |
| `~/bin/gv-bridge-restart.sh` | Nightly recycle — kills, then delegates relaunch to ensure |
| `~/.config/systemd/user/gv-bridge-watchdog.{service,timer}` | 2-minute liveness check |
| `~/.config/systemd/user/gv-bridge-restart.{service,timer}` | Nightly 04:00 recycle |
| `~/.config/autostart/gv-bridge-chrome.desktop` | Runs the ensure script at login |
| `~/Desktop/GV-Bridge.desktop` | Desktop shortcut (mode 755 — see note below) |
| `~/.config/gv-bridge-chrome/` | Chrome profile holding the authenticated GV session |
| `~/.local/state/gv-bridge-restart.log` | Launch / restart log |
| `/opt/rotary-phone/refresh-gv-cookies.sh` | Cron `*/20` — refreshes cookies, mutes the tab |
| `/opt/rotary-phone/ChromeExtension/` | Extension source. Passed to Chrome but **not loaded** |

**.desktop files must be mode 755, never 775.** GNOME silently refuses to launch a
group-writable `.desktop` file, which is exactly why the previously shipped
`GV-Bridge.desktop` did nothing when clicked.

## Diagnostics

### Web UI

Open `http://<radio-box>:5004/diagnostics` for real-time:
- SIP message log (REGISTER, INVITE, BYE with timestamps)
- HT801 health (registration status, ping, config validation)
- Call state timeline
- GV Bridge extension status

### API Endpoints

```bash
# Full status snapshot
curl http://localhost:5004/api/diagnostics/status

# SIP message log (filterable)
curl "http://localhost:5004/api/diagnostics/sip-log?count=20&method=INVITE"

# Send test INVITE to ring the phone
curl -X POST http://localhost:5004/api/diagnostics/test-ring

# HT801 config comparison (expected vs actual)
curl http://localhost:5004/api/diagnostics/ht801/config

# Call timeline
curl http://localhost:5004/api/diagnostics/timeline
```

### Service logs

```bash
# RotaryPhone server
journalctl -u rotary-phone -f

# GV bridge watchdog (launch / relaunch events)
journalctl --user -u gv-bridge-watchdog.service --since '-1h'
tail -f ~/.local/state/gv-bridge-restart.log

# Timer state
systemctl --user list-timers 'gv-bridge-*'
```

## Troubleshooting

### Phone doesn't ring

1. **Check HT801 registration**: `curl http://localhost:5004/api/diagnostics/status` → `ht801.isRegistered` should be `true`
2. **Send test INVITE**: `curl -X POST http://localhost:5004/api/diagnostics/test-ring` and check the SIP log for 100/180/200 responses
3. **If INVITE times out**: The HT801 may need a factory reset (hidden state blocks incoming SIP). After reset, reconfigure SIP Server, User ID, and registration.

### SMS or voicemail lists are empty

Almost always the CDP link to the bridge, not the API.

1. Is the bridge running? `pgrep -af 'user-data-dir=/home/mmack/.config/gv-bridge-chrome'`
2. Is CDP answering? `curl -s http://localhost:9224/json/version`
3. Does a manual refresh succeed?
   `curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser -H 'Content-Type: application/json' -d '{}'`
4. Has the session expired? Raise the bridge window and check it is still signed in.
5. Watchdog history: `tail ~/.local/state/gv-bridge-restart.log`

If the browser is up but CDP is silent, it was launched **without**
`--remote-debugging-port=9224 --remote-allow-origins=*`. Kill it and re-run
`~/bin/gv-bridge-ensure.sh`, which always supplies both.

### The extension is no longer in the path

Chrome has ignored `--load-extension` since v137. This box runs Chrome 151, and the live
profile's `Preferences` lists only Chrome's five built-in extensions — the GV Bridge
extension is absent (verified 2026-08-18).

Calls work regardless. A live test call under exactly that configuration reported
`inboundFramesSent: 345`, `outboundFramesReceived: 341`, `bidirectionalAudio: true` and
zero errors, because audio runs on the SIPSorcery DTLS-SRTP path
(`docs/superpowers/specs/2026-03-27-gv-api-migration-design.md`) rather than the
extension's tabCapture relay, and answer/hangup go over SIP rather than DOM clicking.

The flag is still passed so the command line matches the process the box runs today, but
nothing depends on it. Do not write code that assumes the extension is loaded.

### GV doesn't ring in the browser

This is no longer a meaningful symptom, and chasing it will waste your time.
Incoming calls are signalled over SIP; the browser is not in the ring path at all,
so whether Google Voice rings *in the browser* has no bearing on whether the
rotary phone rings. See [Phone doesn't ring](#phone-doesnt-ring) for the
troubleshooting that actually applies.

### The bridge window is covered by the kiosk (`--disable-backgrounding-occluded-windows`)

The bridge window sits **behind** Radio Console's fullscreen kiosk. That is how stacking works here;
it is not placed off-screen. Since 2026-09-25, `gv-bridge-ensure.sh` launches Chrome with
`--disable-backgrounding-occluded-windows`, the same flag the kiosk's own launcher passes. The aim is to
keep a covered window rendering. **On this Wayland box it probably does not achieve that on its own**
(see below). The flag does not change stacking or focus, and the window is never raised.

**Why:** the first unattended auto-relogin (2026-09-25 22:54 EDT, kiosk up) reached
`accounts.google.com/v3/signin/challenge/pwd`. The driver's "password input is rendered" check
(offsetParent, non-zero box, computed display and visibility) then stayed false for 15 s. In the
attended spike, with the window in front of the owner, the same input measured 348×52.

⛔ **The occlusion/backgrounding hypothesis is NOT supported. This flag is hardening, not the fix.** A
second real run (2026-09-26 03:23Z) put the kiosk deliberately in front of the bridge. The driver's
timeout log recorded `document.visibilityState = 'visible'` for the whole 16 s, with the URL on
`/v3/signin/challenge/pwd` and the `Passwd` input present but **0×0**. The page was not hidden. This
flag does only one thing, keep a page from being demoted out of `VISIBLE`, so it cannot address that
failure. The research below predicted exactly this reading. One thing is still untested: whether a
covered page that is *visible* is also *frame-starved* (the frame-callback limit below). The
`requestAnimationFrame` count in runbook step 6 would test it. The auto-relogin failure itself remains
**undiagnosed**.

**⚠ What research says the flag can do under Ozone/Wayland: probably nothing here.** Chromium source
was read at `main` in September 2026, not at the box's 153.0.8010.52 tag:

- The switch is `kDisableBackgroundingOccludedWindowsForTesting` in
  [`content_switches.cc`](https://github.com/chromium/chromium/blob/main/content/public/common/content_switches.cc).
  Its only production reader is `WebContentsImpl::UpdateWebContentsVisibility`
  ([`web_contents_impl.cc`](https://github.com/chromium/chromium/blob/main/content/browser/web_contents/web_contents_impl.cc),
  about line 12718). It rewrites `OCCLUDED` to `VISIBLE` and never touches `HIDDEN`. *Spot-checked
  directly.*
- `OCCLUDED` comes from a native occlusion tracker. That is `NativeWindowOcclusionTrackerWin` on Windows,
  and X11 `VisibilityNotify` on X11
  ([`x11_window.cc`](https://github.com/chromium/chromium/blob/main/ui/ozone/platform/x11/x11_window.cc)).
  **Nothing under `ui/ozone/platform/wayland` reports occlusion.** Wayland's `xdg_toplevel`
  *suspended* state is parsed, but it is passed only to the frame manager
  ([`wayland_toplevel_window.cc`](https://github.com/chromium/chromium/blob/main/ui/ozone/platform/wayland/host/wayland_toplevel_window.cc)
  `OnWindowSuspensionChanged`, *spot-checked*), never to page visibility. So on this box a covered bridge
  page is probably still `visibilityState === "visible"` **with or without the flag**. That is an
  inference, and runbook step 6 checks it.
- **The more likely limit is the compositor.** mutter (GNOME 46 on the box) does not send `wl_surface.frame`
  callbacks to a surface that is fully obscured
  ([mutter MR 918](https://gitlab.gnome.org/GNOME/mutter/-/merge_requests/918)). It also marks a window
  covered for 3 s as *suspended* ([MR 3019](https://gitlab.gnome.org/GNOME/mutter/-/merge_requests/3019)).
  Chrome's Wayland frame manager waits for a callback before it commits the next frame
  ([`wayland_frame_manager.cc`](https://github.com/chromium/chromium/blob/main/ui/ozone/platform/wayland/host/wayland_frame_manager.cc):
  *"Frame callback hasn't been acked, need to wait"*). Its two bypasses are gated on
  `video_capture_count_ > 0`, that is, on active tab or video capture (*spot-checked*). **No switch or
  feature enables them.** The same interaction is recorded in
  [mutter#3663](https://gitlab.gnome.org/GNOME/mutter/-/issues/3663).
- `--disable-features=CalculateNativeWinOcclusion` is **Windows-only**, as the source comment says.
  **There is no Linux/Wayland equivalent feature.** `--disable-renderer-backgrounding` (process priority)
  and `--disable-background-timer-throttling` (timer throttling) do not touch frame callbacks either.
- `offsetParent` and `getBoundingClientRect()` force a synchronous style and layout pass
  ([CSSOM View](https://drafts.csswg.org/cssom-view/)). So a null or zero result means the element really
  was `display:none` or detached. It was not stale layout. That fits the page's own transition never
  advancing because frames stalled, but **it has not been shown that Google's sign-in page is
  frame-driven.**

**Not established:** behaviour at the exact 153 tag; whether a covered page's frames slow to about 1/s
or stop entirely; whether CDP `Page.startScreencast` counts as capture and so turns on the bypass; how
mutter paces an obscured **Xwayland** window; and any mechanism linking any of this to the post-reboot
`Network.getCookies` timeouts described below. Cookie reads do not go through rendering.

**If step 6 shows the flag did not help, these are the options, all untested and all the owner's call:**

- Run the bridge under `--ozone-platform=x11` (Xwayland). There the flag *does* apply, but it is a
  bigger change to a working browser.
- Have the relogin driver hold a CDP screencast open during sign-in, if that counts as capture. Unknown.
- Uncover the window briefly during an attended relogin. That changes the stacking the design deliberately
  avoids.

**Installing the flag needs an attended session.** The deploy ships `gv-bridge-ensure.sh` to
`/opt/rotary-phone/deploy/`, but it never installs it into `~/bin`. The running Chrome also keeps its
old command line until it is restarted.

#### What installing the two scripts changes besides the flag

Measured on the box 2026-09-25, read-only:

| | Installed `~/bin` copy | Repo copy |
|---|---|---|
| `gv-bridge-ensure.sh` | sha256 `fd04f1ff…`, 1044 B, Aug 18, hardcoded paths | Same Chrome argv **plus the flag** (the shipped copy's `--print-config` matches the running process argv exactly). Adds a `flock` on `~/.config/gv-bridge-chrome.lock`, so a second launcher now exits 0 **without launching** (see below). Adds `--print-config`. Adds env overrides, whose defaults equal the box values. The exit code is still 0 on every path, and the log line is unchanged. |
| `gv-bridge-restart.sh` | sha256 `9221e814…`, Jul 16, **its own launch line, which LACKS `--remote-debugging-port`/`--remote-allow-origins`** | Kills, then **delegates to ensure**, so a relaunch gets the CDP flags. Waits up to 60 s for the lock and exits 1 if it cannot get it. Only the nightly timer runs it, and that timer is **disabled**. |

The four `gv-bridge-{watchdog,restart}.{service,timer}` units are **byte-identical** to the repo copies.

**Radio Console's exit-code dependency.** Their `KIOSK-2` launcher runs `~/bin/gv-bridge-ensure.sh` and
reads its exit code. The new lock adds a third exit-0 outcome, "another launcher holds the lock". The
cross-repo decision ADR `docs/architecture/decisions/2026-09-08-gv-bridge-ensure-exit-code.md` keeps
the exit code at 0 permanently. Their installed launcher re-probes with `pgrep` after exit 0
(`radio-console-open:414`, `:473`), which should make the new outcome harmless to them. **They have not
confirmed that.** The question is drafted at
`docs/handoffs/2026-09-25-radioconsole-ensure-install-exit-code-question-DRAFT.md`. **Do not run this
runbook until Radio Console answers it.**

**`--password-store` is not involved.** Neither the flag nor the scripts pass it. The deploy's hard gate
and `deploy/tests/check-bridge-chrome-flags.sh` both assert that.

#### Does the Google session survive a Chrome restart on this profile?

The evidence says yes, but it is not conclusive:

- Chrome has been relaunched on `~/.config/gv-bridge-chrome` at least 20 times since 2026-08-23
  (`~/.local/state/gv-bridge-restart.log`), including after the weekly reboots of 2026-09-13 and
  2026-09-20. After each of those two relaunches, cookie refresh later reported `validated against Google
  and persisted`. The logs show no sign-in in between.
- ⚠ Each relaunch was also followed by **5–6 hours** of `CDP: WebSocket cookie extraction failed` (a
  10 s `Network.getCookies` timeout every 20 min). The bridge was behind the kiosk throughout. Recovery
  came at 09:13 on 2026-09-13 and 08:14 on 2026-09-20, and **what triggered it is not known**. The user
  journal for those mornings has been rotated away. A human sign-in at that moment cannot be ruled out.
  An uncovered window is also possible, and would fit the occlusion hypothesis. **Expect the first
  refresh after the restart to fail for the same reason, and do not read that failure as a lost session.**
- `gv-bridge-restart.sh` sends SIGTERM and waits 3 s before SIGKILL, so Chrome gets a chance to flush
  its cookie store.

#### Attended runbook

Run it on the box as `mmack`, with no call in progress. Each step lists what you should see; if you see
something else, stop.

**0. Preconditions.** Merge this change and deploy it with `Deploy-ToLinux.ps1`, then confirm the
deploy shipped it. Check for the thing the fix *adds*:

```bash
bash /opt/rotary-phone/deploy/gv-bridge-ensure.sh --print-config | grep -cx -- 'chrome_arg=--disable-backgrounding-occluded-windows'   # 1
bash /opt/rotary-phone/deploy/gv-bridge-ensure.sh --print-config | grep -c password-store                                          # 0
```

`--print-config` has no side effects: it starts nothing, takes no lock and writes no log.

**1. Record the "before" state.**

```bash
sha256sum ~/bin/gv-bridge-ensure.sh ~/bin/gv-bridge-restart.sh      # fd04f1ff…, 9221e814… (or note what it is now)
pgrep -af 'user-data-dir=/home/mmack/.config/gv-bridge-chrome' | grep -v -- --type= | grep -c -- --disable-backgrounding-occluded-windows   # 0
curl -s http://localhost:9224/json/version | head -3                 # CDP answers
curl -s http://localhost:5004/api/gvbridge/status                    # note browserRefreshOutcome
```

**1b. (Recommended) Get the "before" answer to the open question now, with nothing installed.** With the
kiosk covering the bridge, run step 6's `requestAnimationFrame` count against the **current** Chrome. If
it is about 120 while covered, frames are flowing, the covered window is not the cause of the relogin
failure, and nothing in this runbook will help it. If it is near zero, the covered window is
frame-starved. The research above says this flag cannot change that.

**2. Install the two scripts: backup, then atomic rename.** This is the narrow path, and it installs
nothing else. The watchdog does not need to be stopped. A rename is atomic, so a watchdog run sees
either the old file or the new one, never a partial one.

```bash
cp -p ~/bin/gv-bridge-ensure.sh  ~/bin/gv-bridge-ensure.sh.bak
cp -p ~/bin/gv-bridge-restart.sh ~/bin/gv-bridge-restart.sh.bak
install -m 755 /opt/rotary-phone/deploy/gv-bridge-ensure.sh  ~/bin/gv-bridge-ensure.sh.new  && mv -f ~/bin/gv-bridge-ensure.sh.new  ~/bin/gv-bridge-ensure.sh
install -m 755 /opt/rotary-phone/deploy/gv-bridge-restart.sh ~/bin/gv-bridge-restart.sh.new && mv -f ~/bin/gv-bridge-restart.sh.new ~/bin/gv-bridge-restart.sh
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group bridge   # "[drift-check] bridge: 2/2 installed files match", exit 0
bash ~/bin/gv-bridge-ensure.sh --print-config | grep -cx -- 'chrome_arg=--disable-backgrounding-occluded-windows'   # 1
```

The alternative is `bash /opt/rotary-phone/deploy/setup-gvbridge.sh`. It installs the same two scripts.
Measured against the box, it would **also** rewrite the autostart entry's `Comment=` line (leaving a
`.bak`), **create a new `~/Desktop/GV-Bridge.desktop` icon** on the kiosk's desktop (none exists today),
and re-run `enable --now` on the watchdog, which is already enabled. The units are unchanged. Use it
only if you want those extras.

After step 2, **nothing has changed for the running Chrome.** The watchdog no-ops while the old process
is alive.

**3. Restart the bridge Chrome.** Go through the unit, so that it runs exactly the way the nightly
recycle would:

```bash
systemctl --user start gv-bridge-restart.service
tail -n 4 ~/.local/state/gv-bridge-restart.log
#   … restart: killed existing
#   Running as unit: run-….service; …
#   … ensure: bridge was down -> launched
#   … restart: handed off to ensure
```

**4. Confirm the new process carries the flag and CDP.**

```bash
pgrep -af 'user-data-dir=/home/mmack/.config/gv-bridge-chrome' | grep -v -- --type= | grep -c -- --disable-backgrounding-occluded-windows   # 1
curl -s http://localhost:9224/json/version | head -3                                                                                       # answers within ~10 s
```

**5. Confirm the session.**

```bash
curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser -H 'Content-Type: application/json' -d '{}'
```

If the session was signed in at step 1, expect `200` and `{"refreshed":true,…}`. If it was already
signed out (it was on 2026-09-25 at 23:00: `missing required SAPISID`), expect `502` with outcome
`SignedOut`. A restart cannot have caused that. A **timeout or `Unreachable`** matches the post-reboot
pattern described above. Retry after one cron cycle (20 min) before concluding anything.

**6. The test the flag exists for.** With the kiosk up and covering the bridge, read the voice tab's
`document.visibilityState` over CDP on port 9224, with the owner's driver or DevTools
`Runtime.evaluate`. Source reading predicts `"visible"` **with or without the flag**, so this step alone
cannot tell you whether the flag helped. Also evaluate
`new Promise(r => { let n = 0; const t0 = performance.now(); (function f(){ n++; performance.now() - t0 < 2000 ? requestAnimationFrame(f) : r(n); })(); })`
with `awaitPromise: true`. About 120 means frames are flowing. A single-digit count, or a call that
times out, means the frame-callback limit applies and the flag cannot fix it. Then reset the auto-relogin
breaker (`gv-auto-relogin.sh --reset`) and re-run the attended sign-in with the kiosk in front. Pass
criterion: the password input measures a non-zero box. If it fails, see the options listed above.

**7. Measure the cost.** The baseline was measured 2026-09-25, with the bridge covered and without the
flag: the bridge's 14 processes averaged **1.14 % of one core** (5738 CPU-s over 5.8 days) at **858 MB
RSS**. After at least an hour, compare:

```bash
ps -o etimes=,times=,rss= -p $(pgrep -d, -f 'user-data-dir=/home/mmack/.config/gv-bridge-chrome') \
  | awk '{t+=$2; r+=$3; if($1>e)e=$1} END {printf "avg_core_pct=%.2f rss_MB=%d\n", 100*t/e, r/1024}'
```

**Rollback.** Restore the scripts, then restart Chrome with the **old ensure** only:

```bash
mv -f ~/bin/gv-bridge-ensure.sh.bak  ~/bin/gv-bridge-ensure.sh
mv -f ~/bin/gv-bridge-restart.sh.bak ~/bin/gv-bridge-restart.sh
pkill -f 'user-data-dir=/home/mmack/.config/gv-bridge-chrome'; sleep 3
~/bin/gv-bridge-ensure.sh        # or wait up to 2 min for the watchdog
```

⛔ **Do not roll back with the old `gv-bridge-restart.sh` or `gv-bridge-restart.service`.** The old
restart script launches Chrome **without** `--remote-debugging-port`, and that silently breaks cookie
refresh.

### After reboot

Both services auto-start, but you may need to:
1. Wait 1-2 minutes for HT801 to re-register
2. Switch adapter mode: `curl -X PUT http://localhost:5004/api/gvbridge/adapter/mode -H 'Content-Type: application/json' -d '{"mode":"GVBrowser"}'`

## Current Status & Known Limitations

### Working (call flow verified 2026-03-24; audio + cookie bridge verified 2026-08-18)
- **Full incoming call flow**: GV call → SIP INVITE → HT801 → phone rings → user answers (200 OK) → InCall → user hangs up (BYE) → Idle
- **Call state machine**: SIP events are authoritative (not browser extension events). 60-second ringing timeout prevents stuck state.
- **Incoming call detection**: signalled over SIP, not by the browser. The extension's
  500 ms DOM button poll is no longer in the path — the extension is not loaded (see
  Troubleshooting), yet the 2026-08-18 live call rang and connected normally.
- **SIP diagnostics**: Real-time message log, INVITE timeout detection, HT801 health, call timeline
- **Bidirectional call audio**: DTLS-SRTP via SIPSorcery. Live call 2026-08-18 reported `inboundFramesSent: 345`, `outboundFramesReceived: 341`, `bidirectionalAudio: true`, zero errors. This closes the June 13 investigation that recorded `inboundFramesSent: 0`.
- **SMS + voicemail**: read from Google's HTTP API using cookies scraped from the bridge browser over CDP. Live check 2026-08-18 listed 149 SMS messages and 50 voicemails.
- **Diagnostics web UI** at `/diagnostics` and REST API

### Not yet working
- **BYE handling**: HT801's BYE after a test-ring gets 481 response, leaving the device stuck. Workaround: reboot HT801 after using test-ring.
- **Auto mode on boot**: Adapter defaults to BluetoothHfp; needs manual switch to GVBrowser after each service restart
- **Outgoing calls**: Rotary dial → GV not yet implemented

### HT801 Quirks
- **Factory reset required** if incoming SIP stops working. Only configure 3 settings: SIP Server, SIP User ID, SIP Registration. Changing other settings (Register Expiration, NOTIFY Auth, etc.) can silently break incoming SIP.
- **Re-registration delay**: After service restart, HT801 won't re-register until its timer fires (up to 60 min). Reboot the HT801 to force immediate re-registration.
- **Test-ring caution**: The test-ring endpoint auto-answers after 4s and the BYE response (481) leaves the HT801 stuck. Always reboot HT801 after using test-ring.
