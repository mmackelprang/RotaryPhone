# GV Bridge — Operator Guide

**Last updated:** October 4, 2026

This guide covers running RotaryPhone's Google Voice path on the Ubuntu box (`radio`): how incoming
Google Voice calls ring a rotary phone through a Grandstream HT801 ATA, how the "bridge" Chrome keeps
the Google session alive for SMS and voicemail, what every deploy installs, and how to check and
troubleshoot all of it.

It replaces the older `docs/SETUP-AND-TESTING.md` (now in
[`docs/archive/gv-call-path/`](archive/gv-call-path/SETUP-AND-TESTING.md)). That guide described the
March 2026 Chrome-extension design; its still-current content, the `/api/gvbridge/status` field
reference, is in [Status fields](#status-fields) below.

## Architecture

Two independent paths share the box. Only one of them involves the browser.

**Calls — no browser involvement:**

```
Google Voice call
  -> SIP over WebSocket to the .NET RotaryPhone server (GVApi mode, the default)
  -> CallManager sends SIP INVITE to the HT801
  -> HT801 rings the rotary phone
  -> handset lifted -> the held 200 OK goes to Google Voice -> call connected
  -> audio flows both ways over DTLS-SRTP (SIPSorcery)
```

Since PR #95 (2026-10-03) the server answers Google Voice only when the handset is lifted, so a caller
who hangs up while the phone is ringing sends a SIP `CANCEL` and the ringing stops promptly.

**SMS and voicemail — the browser holds the session, nothing more:**

```
gv-bridge-ensure.sh
  -> Google Chrome, dedicated profile (~/.config/gv-bridge-chrome), CDP on :9224
  -> holds ONE authenticated voice.google.com session
  -> the server refreshes its own credentials every 8 minutes, and
     a box-side cron (*/20) POSTs /api/gvbridge/cookies/refresh-from-browser
  -> the API reads that session's cookies over CDP, validates them, and only then adopts them
  -> the API calls Google's HTTP API directly for SMS and voicemail
```

The Chrome extension under `ChromeExtension/` is **superseded** and is not loaded. See
[The extension is no longer in the path](#the-extension-is-no-longer-in-the-path).

The box is shared with Radio Console, which runs a fullscreen kiosk in its own Chrome. Read
[`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`](prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md) before
changing anything that affects Bluetooth, audio, the kiosk, or the bridge window.

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Ubuntu | 24.04+ | GNOME 46 on Wayland. Tested on 24.04 (x64) |
| .NET SDK | 10.0 | On the build machine |
| Google Chrome | 137+ | `google-chrome` on PATH. Chromium is not used |
| `python3-gi` | system | Used by the keyring unlock helper |
| `systemd-creds` and a TPM2 | system | For the keyring credential. Optional: without it Chrome may prompt for the keyring |
| Grandstream HT801 | Firmware 1.0.5+ | Factory reset recommended before setup |
| Google Voice account | — | With a phone number |

## Hardware setup

### HT801 ATA

Since 2026-09-06 the HT801 is on a dedicated point-to-point Ethernet link into the box's `enp1s0`, not
on the house LAN. Its address (`192.168.86.240`) is reachable **only from the box**, so open its web UI
from a browser on the box's own desktop. Boundary rule 9 in the boundary doc has the details.

1. Log into the web interface at `http://<HT801-IP>`.
2. Under **Port Settings**, set only:
   - **SIP Server**: the box's address on that link (for example `192.168.86.50`)
   - **SIP User ID**: `1000`
   - **SIP Registration**: checked
3. Verify **Port Status** shows "Registered".

If the HT801 was configured before, **factory reset it first**. Hidden state can make it silently drop
incoming SIP even when its settings look correct. Changing settings beyond the three above (Register
Expiration, NOTIFY authentication, and so on) has also broken incoming SIP before.

The server learns the HT801's real address from its SIP REGISTER. See
**[docs/HT801-ADDRESS.md](HT801-ADDRESS.md)** for the one configuration key that holds the address and
for the signals that verify it.

### Rotary phone

Connect the rotary phone to the HT801's FXS (Phone) port with a standard RJ11 cable.

## Deployment

### Deploy (from the Windows build machine)

```powershell
.\deploy\Deploy-ToLinux.ps1                    # defaults: -TargetHost radio -Runtime linux-x64
.\deploy\Deploy-ToLinux.ps1 -PreflightOnly     # check the transport and sudo, deploy nothing
```

The deploy builds, syncs `/opt/rotary-phone/`, installs and restarts `rotary-phone.service`, and then
runs three post-deploy steps on the box, each followed by a drift check:

| Step | Installs | Failure |
|---|---|---|
| `install-gv-session-alarm.sh` | `~/bin/gv-session-alarm.sh` and its user units (timer left disabled) | Fatal |
| `install-gv-bridge.sh` | The bridge launch scripts, the keyring helper and the GNOME extension (see below) | Reported in red, not fatal |
| `install-gv-auto-relogin.sh` | Auto-relogin scripts and units (timer left disabled, breaker never armed) | Reported in red, not fatal |

The box's `appsettings.Production.json` and `gv-account.conf` are never shipped, overwritten or deleted
by the deploy. The box's copy of `appsettings.Production.json` is authoritative.

### First-time setup on a new box

Run once, after the first deploy:

```bash
bash /opt/rotary-phone/deploy/setup-gvbridge.sh
```

It verifies Chrome is installed, installs `gv-bridge-ensure.sh` and `gv-bridge-restart.sh` into `~/bin`,
installs the watchdog and nightly-restart systemd **user** units, enables the 2-minute watchdog timer,
creates the login autostart entry and a desktop shortcut. No `sudo` is needed. Do not re-run it on
every deploy: it re-applies the autostart entry and the shortcut, which `install-gv-bridge.sh`
deliberately does not. `--with-legacy-extension-service` provisions the superseded snap-Chromium
configuration; it is installed but never enabled, and there is no reason to use it.

Then:

1. Set up the [keyring credential](#keyring-unlock-one-time-credential-setup) (one time).
2. Bring the bridge up: `~/bin/gv-bridge-ensure.sh`, or wait up to 2 minutes for the watchdog.
3. Sign in to Google Voice in the bridge window with the account that owns the number. The window is
   placed by the compositor (`--window-position` is a no-op on Wayland); raise it from the GNOME
   overview.
4. Log out and back in (or reboot) so GNOME loads the [stacking extension](#the-bridge-window-is-covered-by-the-kiosk).
5. Run the checks in [Verify the bridge](#verify-the-bridge).

### What starts automatically on boot

| Unit | Type | Starts | Enabled |
|---|---|---|---|
| `rotary-phone.service` | System | On boot | yes |
| `gv-bridge-watchdog.timer` | User (`systemctl --user`) | 2 min after boot, then every 2 min | **yes** |
| `gv-bridge-restart.timer` | User | Nightly 04:00 | no — installed, left disabled |
| `gv-session-alarm.timer` | User | — | no — `install-gv-session-alarm.sh --enable` |
| `gv-auto-relogin.timer` | User | — | no — owner's `install-gv-auto-relogin.sh --enable` |
| `~/.config/autostart/gv-bridge-chrome.desktop` | GNOME autostart | 15 s after login | yes |

The watchdog is what keeps the bridge alive. `gv-bridge-ensure.sh` is idempotent: it looks for a process
carrying this profile's `--user-data-dir` and exits 0 without touching anything if it finds one.

To enable the nightly recycle (off by default; use it if renderer memory growth becomes a problem):

```bash
systemctl --user enable --now gv-bridge-restart.timer
```

## How the deploy installs the bridge scripts

`deploy/install-gv-bridge.sh` runs on **every** deploy. It installs, from `/opt/rotary-phone/deploy/`:

| Source | Installed to |
|---|---|
| `gv-bridge-ensure.sh` | `~/bin/gv-bridge-ensure.sh` |
| `gv-bridge-restart.sh` | `~/bin/gv-bridge-restart.sh` |
| `gv-keyring-unlock.py` | `~/bin/gv-keyring-unlock.py` |
| `gnome-extension/gv-bridge-behind@rotaryphone/{metadata.json,extension.js}` | `~/.local/share/gnome-shell/extensions/gv-bridge-behind@rotaryphone/` |

In order, it:

1. **Checks the shipped files** exist and that both shell scripts parse (`bash -n`). Otherwise it
   installs nothing.
2. **Applies the flag-match gate.** It compares the Chrome flags the new `gv-bridge-ensure.sh` would
   use (its `--print-config` output, which has no side effects) with the command line of the bridge
   Chrome running now. If they differ, it prints the diff and **refuses**, leaving `~/bin` untouched. It
   also refuses when no bridge Chrome is running, because there is nothing to compare against.
3. **Installs** each changed file atomically (write `.new`, then rename). A file that is already
   identical is left alone. Every replaced file is first backed up as `<name>.bak-<YYYYmmdd-HHMMSS>`.
4. **Installs and enables the GNOME extension**, and keeps `org.gnome.shell disable-user-extensions`
   set to `false` (see [the stacking section](#the-bridge-window-is-covered-by-the-kiosk)).
5. **Post-install check:** the installed `~/bin/gv-bridge-ensure.sh --print-config` must report exactly
   what the shipped copy reports. This tests what the installed script does, not just its bytes.

A refusal is reported in red by the deploy and is not fatal. The rest of the deploy has already
completed.

### When the gate refuses

A refusal means the new script would launch Chrome differently from the one running now. That is
expected when a change adds or removes a Chrome flag, and it is a warning sign otherwise.

1. Read the diff the installer printed (`<` lines are the running Chrome, `>` lines are the new script).
2. If the difference is intended, install by hand on the box:

   ```bash
   bash /opt/rotary-phone/deploy/install-gv-bridge.sh --skip-flag-check
   ```

3. Restart the bridge so the running Chrome picks up the new command line. Until you do, every later
   deploy compares against the old process and refuses again.

   ```bash
   systemctl --user start gv-bridge-restart.service
   tail -n 4 ~/.local/state/gv-bridge-restart.log
   ```

   Expect `restart: killed existing`, `ensure: bridge was down -> launched` and
   `restart: handed off to ensure`. The first cookie refresh after a restart can fail; see
   [Does the Google session survive a Chrome restart?](#does-the-google-session-survive-a-chrome-restart).

Use `--skip-flag-check` the same way when no bridge Chrome is running.

`--password-store` must never appear in the bridge's flags. On this profile it makes Chrome discard
the stored cookies and destroys the Google session. The deploy refuses to start if
`deploy/gv-bridge-ensure.sh` contains it, and `deploy/tests/check-bridge-chrome-flags.sh` asserts the
same.

### Rollback

Restore the backups the installer made, then restart Chrome through the restored ensure script:

```bash
ls -1 ~/bin/*.bak-*                                   # pick the stamp to restore
cp -p ~/bin/gv-bridge-ensure.sh.bak-<stamp>  ~/bin/gv-bridge-ensure.sh
cp -p ~/bin/gv-bridge-restart.sh.bak-<stamp> ~/bin/gv-bridge-restart.sh
pkill -f "user-data-dir=$HOME/.config/gv-bridge-chrome"; sleep 3
~/bin/gv-bridge-ensure.sh                             # or wait up to 2 min for the watchdog
```

Relaunch with `gv-bridge-ensure.sh`, not with a restored `gv-bridge-restart.sh`: the hand-installed
restart script from before October 2026 had its own launch line **without**
`--remote-debugging-port`, which silently breaks cookie refresh. The next deploy reinstalls the repo
version, and the drift check reports the rolled-back files as drift until it does.

### Drift checks

After each install step the deploy runs `check-installed-drift.sh`, which compares three links: the
repo (a manifest computed on the build machine), the shipped copy in `/opt/rotary-phone/deploy/`, and
the installed copy. Run it by hand at any time:

```bash
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group bridge
```

| Group | Files |
|---|---|
| `alarm` | `~/bin/gv-session-alarm.sh`, `gv-session-alarm.{service,timer}` |
| `bridge` | `~/bin/gv-bridge-ensure.sh`, `gv-bridge-restart.sh`, `gv-keyring-unlock.py`, and the two GNOME extension files |
| `relogin` | `~/bin/gv-auto-relogin.sh`, `gv-auto-relogin-breaker.sh`, `gv-cdp.py`, `gv-auto-relogin.{service,timer}`; `gv-relogin-signin.py` is optional |

Exit codes: `0` everything matches, `1` drift, `2` cannot determine (missing file or manifest). The
success line reads `[drift-check] <group>: N/N installed files match repo → shipped → installed.` For
the `bridge` group, fix drift with `install-gv-bridge.sh`; the check's own ACTION text still names
`setup-gvbridge.sh`, which is the older, broader installer.

## Keyring unlock (one-time credential setup)

The box logs in automatically, so no typed password ever unlocks the GNOME login keyring. Chrome keeps
its cookie encryption key there, so on a locked keyring it blocks on an unlock
prompt behind the kiosk. Before launching Chrome, `gv-bridge-ensure.sh` unlocks the keyring from an
encrypted credential:

```
sudo -n systemd-creds decrypt --name=radio-keyring /etc/credstore.encrypted/radio-keyring.cred -
  | timeout 20 python3 ~/bin/gv-keyring-unlock.py
```

This runs only on the launch path, never when the bridge is already up, and adds up to about 20 s. It
is best effort: if anything fails, the keyring stays as it was and Chrome prompts as before. A failed
unlock logs `ensure: keyring unlock failed (Chrome may prompt)` to `~/.local/state/gv-bridge-restart.log`.

### Create the credential

Run once on the box, as the desktop user. The password is the login (keyring) password:

```bash
sudo mkdir -p /etc/credstore.encrypted
read -rs PW; printf '%s' "$PW" | sudo systemd-creds encrypt --name=radio-keyring - /etc/credstore.encrypted/radio-keyring.cred; unset PW
sudo chmod 600 /etc/credstore.encrypted/radio-keyring.cred
```

- `printf '%s'`, not `echo`: the credential must not end in a newline, or the unlock is sent the wrong
  password.
- `systemd-creds` binds the credential to this machine's TPM2 and host key, so only root on this box
  can decrypt it. **Re-run these commands if Secure Boot or firmware changes**, since that can make the
  TPM refuse to decrypt it.

Verify it decrypts to the right length (the number of characters in the password), without printing it:

```bash
sudo systemd-creds decrypt --name=radio-keyring /etc/credstore.encrypted/radio-keyring.cred - | wc -c
```

### Requirements and cautions

- **The desktop user needs passwordless `sudo`.** The ensure script uses `sudo -n` (non-interactive)
  for `test` and `systemd-creds decrypt`. If `sudo -n` cannot run them, the unlock is skipped without a
  log line.
- `gv-keyring-unlock.py` talks to the **already running** `gnome-keyring-daemon` over the session bus.
  Exit codes: `0` unlocked or already unlocked, `1` still locked (wrong password), `2` nothing on stdin,
  `3` D-Bus or daemon error.
- **Do not use `gnome-keyring-daemon --unlock` for this.** On gnome-keyring 46 it starts a second daemon
  on the same control directory, leaves the login keyring locked, and deletes the control directory when
  it exits (observed on the box 2026-10-04).

## The bridge window is covered by the kiosk

The bridge window sits **behind** Radio Console's fullscreen kiosk. That is the intended stacking; the
window is not off-screen.

### The GNOME Shell extension

On Wayland, nothing outside the compositor can restack another client's window, and mutter raises and
focuses every newly mapped window. Before October 2026 each bridge relaunch (watchdog, nightly restart,
login autostart) therefore landed on top of the kiosk.

The RotaryPhone-owned extension `gv-bridge-behind@rotaryphone` fixes that. When a normal window is
shown, it checks whether the owning process's command line carries the bridge profile marker
(`--user-data-dir=~/.config/gv-bridge-chrome`). Only then does it **lower** the window (never minimize
it, so the page keeps rendering) and activate the topmost remaining window, normally the kiosk. Every
other window, the kiosk included, is ignored.

- `install-gv-bridge.sh` installs it, adds it to `org.gnome.shell enabled-extensions`, and keeps
  **`org.gnome.shell disable-user-extensions` set to `false`**. With that setting `true`, GNOME loads no
  user extension at all. It was `true` on the box, origin unknown; the owner chose on 2026-10-04 to keep
  it `false` and announced that in the boundary doc. Today this is the only user extension on the box.
- **GNOME Shell on Wayland loads a new or changed extension only at the next login.** After the first
  install, or after an update to `extension.js`, log out and back in or reboot.

Check it from a terminal in the desktop session:

```bash
gnome-extensions info gv-bridge-behind@rotaryphone      # expect Enabled: Yes and State: ACTIVE
gsettings get org.gnome.shell disable-user-extensions   # expect false
```

### Chrome flags

`gv-bridge-ensure.sh` is the one place the bridge's Chrome command line is defined
(`gv-bridge-restart.sh` delegates to it). Print it without side effects:

```bash
~/bin/gv-bridge-ensure.sh --print-config
```

Flags worth knowing about:

| Flag | Why |
|---|---|
| `--remote-debugging-port=9224`, `--remote-allow-origins=*` | **Load-bearing.** The API reads the session cookies over CDP. Without them SMS and voicemail lists come back empty |
| `--user-data-dir=~/.config/gv-bridge-chrome` | The dedicated profile, and the marker the watchdog, the restart script and the extension use to find the bridge |
| `--mute-audio` | Keeps Google Voice's own ringer out of the console speakers |
| `--hide-crash-restore-bubble` | A nightly kill or a reboot otherwise leaves a "Restore pages?" dialog on every launch. The bridge always opens `voice.google.com` fresh (2026-10-04) |
| `--disable-backgrounding-occluded-windows` | Stops Chrome demoting a covered window's pages to `OCCLUDED`. Hardening only; see below |
| `--disable-background-timer-throttling`, `--disable-renderer-backgrounding` | Keep a background page's timers and renderer priority up |
| `--ozone-platform=wayland` | Native Wayland |
| `--window-position=10000,10000` | A no-op under Wayland, kept because the running process carries it |
| `--load-extension=/opt/rotary-phone/ChromeExtension` | Ignored by Chrome since v137; kept so the command line matches the running process |

`deploy/tests/check-bridge-chrome-flags.sh` pins the CDP flags, `--disable-backgrounding-occluded-windows`
and the absence of `--password-store`.

### Exit codes and the launch lock

Radio Console's kiosk launcher (KIOSK-2) runs `~/bin/gv-bridge-ensure.sh` and reads its exit code, so the
exit codes are a cross-repo contract: the script has exactly two outcomes, *already up* and *launched*,
and **both exit 0**. The exit code is not a health signal. A failed launch also exits 0.

`gv-bridge-ensure.sh` and `gv-bridge-restart.sh` serialize on a lock file
(`~/.config/gv-bridge-chrome.lock`). On a held lock, ensure **waits up to 60 s** and then runs its normal
liveness check; on timeout it logs and carries on unlocked. It never adds a third "lock held" outcome
(owner decision 4, 2026-10-04). The cost is that an invocation can block for up to 60 s while a restart
or another launch is in progress. The restart script also waits up to 60 s, and exits 1 if it cannot
take the lock. See ADR
[`2026-09-08-gv-bridge-ensure-exit-code.md`](architecture/decisions/2026-09-08-gv-bridge-ensure-exit-code.md),
§9. Do not change an exit code without first announcing it in the boundary doc's Change Log.

### Rendering a covered window: what the occlusion flag does and does not do

`--disable-backgrounding-occluded-windows` was added on 2026-09-25 after the first unattended
auto-relogin (2026-09-26 02:54Z, kiosk up) reached Google's password page and the driver's "password
input is rendered" check stayed false for 15 s. In the attended spike, with the window in front, the
same input measured 348×52.

**The occlusion hypothesis is not supported, and the flag is hardening, not the fix.** A second run
(2026-09-26 03:23Z) with the kiosk deliberately in front recorded `document.visibilityState = 'visible'`
throughout, with the `Passwd` input present but **0×0**. The page was never hidden, so a flag that only
keeps a page from being demoted out of `VISIBLE` cannot address it. That auto-relogin failure remains
**undiagnosed**.

What source reading (Chromium `main`, September 2026, not the box's exact release) suggests:

- The switch's only production reader is `WebContentsImpl::UpdateWebContentsVisibility`
  ([`web_contents_impl.cc`](https://github.com/chromium/chromium/blob/main/content/browser/web_contents/web_contents_impl.cc)),
  which rewrites `OCCLUDED` to `VISIBLE`. `OCCLUDED` comes from a native occlusion tracker on Windows
  and X11; **nothing under `ui/ozone/platform/wayland` reports occlusion**. So on this box a covered page
  is probably `visible` with or without the flag.
- **The more likely limit is the compositor.** mutter does not send `wl_surface.frame` callbacks to a
  fully obscured surface ([mutter MR 918](https://gitlab.gnome.org/GNOME/mutter/-/merge_requests/918))
  and marks a window covered for 3 s as suspended
  ([MR 3019](https://gitlab.gnome.org/GNOME/mutter/-/merge_requests/3019)). Chrome's Wayland frame
  manager waits for a callback before committing the next frame, and its bypasses apply only during
  active tab or video capture
  ([`wayland_frame_manager.cc`](https://github.com/chromium/chromium/blob/main/ui/ozone/platform/wayland/host/wayland_frame_manager.cc);
  see also [mutter#3663](https://gitlab.gnome.org/GNOME/mutter/-/issues/3663)). No Chrome switch changes
  this.
- `--disable-features=CalculateNativeWinOcclusion` is Windows-only. There is no Linux/Wayland equivalent.

**Not established:** whether a covered page's frames slow or stop entirely, whether a CDP screencast
counts as capture, and whether Google's sign-in page is frame-driven at all.

To test whether a covered bridge page is frame-starved, evaluate this in the voice tab over CDP
(port 9224, `Runtime.evaluate` with `awaitPromise: true`) while the kiosk covers it:

```js
new Promise(r => { let n = 0; const t0 = performance.now(); setTimeout(() => r(n), 5000);
  (function f(){ n++; performance.now() - t0 < 2000 ? requestAnimationFrame(f) : r(n); })(); })
```

About 120 means frames are flowing. A single-digit result means the frame-callback limit applies. The
`setTimeout` guard is required, because CDP's own timeout does not bound a promise wait.

If a fix is needed, the untested options (all the owner's call) are: run the bridge under
`--ozone-platform=x11`, where the flag does apply; hold a CDP screencast open during sign-in, if that
counts as capture; or uncover the window briefly during an attended relogin.

### Does the Google session survive a Chrome restart?

The evidence says yes, but it is not conclusive. Chrome has been relaunched on this profile many times
since 2026-08-23, including after the weekly reboots of 2026-09-13 and 2026-09-20, and cookie refresh
later reported `validated against Google and persisted` with no sign-in in between.

Each of those relaunches was also followed by **5 to 6 hours** of
`CDP: WebSocket cookie extraction failed` (a 10 s `Network.getCookies` timeout every 20 minutes) before
recovering, for reasons not known. **Expect the first refresh after a restart to fail, and do not read
that as a lost session.** Retry after one cron cycle (20 minutes) before concluding anything.
`gv-bridge-restart.sh` sends SIGTERM and waits 3 s before SIGKILL, so Chrome can flush its cookie store.

## Verify the bridge

```bash
# 1. The bridge browser is running and CDP answers
pgrep -af "user-data-dir=$HOME/.config/gv-bridge-chrome" | grep -v -- --type=
curl -s http://localhost:9224/json/version

# 2. Cookie extraction works end to end
curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser \
  -H 'Content-Type: application/json' -d '{}'
# Expected: 200 {"refreshed":true,...}

# 3. Status and diagnostics
curl -s http://localhost:5004/api/gvbridge/status
curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool
curl -s http://localhost:5004/api/diagnostics/sip-registrations
```

`refresh-from-browser` validates the extracted cookies against Google **before** adopting them, so it
never overwrites a working set with a dead one. Its status codes point to different fixes:

| Code | Meaning | Do |
|---|---|---|
| 200 | Cookies validated and adopted | Nothing |
| 502 | Google tested the cookies and refused them | Sign in again in the bridge window |
| 503 | Chrome was unreachable; the Google login was **not** tested | Check the bridge is running and CDP answers |
| 202 | Cookies passed, but re-activating the call adapter failed | Investigate the call path; do not sign in again |
| 500 | Local storage error | Check the disk and `data/` |

### Status fields

`GET /api/gvbridge/status` reports the GVApi adapter's real state. The field names are a contract with
Radio Console; new fields are only ever appended.

| Field | Meaning |
|---|---|
| `available` | The adapter is usable. Deliberately stays `true` during a short auth blackout, so the adapter can run its own recovery. Bind UI health to `degraded` or `authBlackout` instead |
| `activeMode` | `GVApi` in production (the `DefaultMode`) |
| `sipRegistered` | `true` only when registered **and** the SIP WebSocket is open |
| `wsConnected` | The SIP WebSocket is open |
| `lastConnectedAt` | UTC of the last successful REGISTER `200 OK`. A new value after a gap means a reconnect |
| `cookiesValid` | The last probe passed **and** no real API call has since been refused |
| `degraded` | Not (cookies valid and registered) |
| `lastHealthyAt` | Last time both held |
| `throttledUntil`, `throttleReason` | Set while a REGISTER cooldown is active after Google throttled the account; `null` otherwise |
| `authBlackout` | A real data-plane call was just refused. May be true for well under a second, because recovery is fast |
| `lastApiSuccessAt`, `lastApiAuthFailureAt` | Written by real SMS and voicemail calls, not by a probe |
| `psidtsMintedAtUtc` | When Google minted the credential now held. **`null` means unknown, not fresh** (for example, cookies taken from the browser). PSIDTS lives about 11 minutes and the server re-mints every 8 |
| `browserSessionValidatedAt`, `browserSessionAgeSeconds` | Last time cookies from the bridge Chrome actually worked, and its age. A steadily climbing age with everything else green means the browser session may have died while the server keeps its own credentials alive |
| `browserSessionStale` | Chrome answered and Google refused its cookies: sign in again. Reads `false` when Chrome is unreachable or signed out, so also read `browserRefreshOutcome` |
| `browserRefreshOutcome` | Last browser refresh: `NotAttempted`, `Unreachable`, `Stale`, `Succeeded`, `TornDown` or `SignedOut` |

`psidtsAgeSeconds` was removed on 2026-09-08. It measured when cookies were loaded, not when they were
minted, so it read low for credentials that were days old. Use `psidtsMintedAtUtc`.

**Keep-alive and reconnect:** the SIP transport reads Google's `keep=` interval from the REGISTER
response and sends a double-CRLF ping every `max(15, keep/2)` s (log: `Keep-alive armed: keep=…`). If the
socket drops, the log shows `SIP WebSocket dropped unexpectedly … reconnecting`, a capped backoff
(1, 2, 4, 8, 16, 30 s with jitter), then `SIP reconnect succeeded` and a new `lastConnectedAt`.

## Configuration

The server reads `appsettings.json`, then the box's `/opt/rotary-phone/appsettings.Production.json`,
which the deploy never touches. The `GVBridge` defaults in the repo's `appsettings.json` suit the box;
the values operators most often need are:

| Key | Default | Notes |
|---|---|---|
| `GVBridge:DefaultMode` | `GVApi` | The live call path. Other modes (`BluetoothHfp`, `SipTrunk`, `GVBrowser`) are not used in production |
| `GVBridge:GvPhoneNumber` | — | Set it in the box's production file |
| `GVBridge:CookieRefreshIntervalMinutes` | `8` | In-process credential refresh. `0` disables it |
| `GVBridge:ChromeCdpPort` | `9224` | Must match the bridge's `--remote-debugging-port` |
| `GVBridge:EnableSmsSend`, `EnableMarkRead` | `false` | Feature gates for writes to Google |
| `GVBridge:InterServiceAuthKey` | empty | When set, `/api/gvbridge/*` requires the `X-RotaryPhone-Auth` header |
| `RotaryPhone:Phones[].HT801IpAddress` | — | The only HT801 address key. See [HT801-ADDRESS.md](HT801-ADDRESS.md) |

There is no `GVBridge:HT801Ip` key; it was removed.

### Key files on the box

| Path | Purpose |
|------|---------|
| `/opt/rotary-phone/` | The application |
| `/opt/rotary-phone/appsettings.Production.json` | Box-owned configuration, never shipped by the deploy |
| `/opt/rotary-phone/deploy/` | Shipped deploy scripts and the drift manifest (`.shipped-manifest.sha256`) |
| `~/bin/gv-bridge-ensure.sh` | Launch-if-down. Idempotent; the watchdog runs it every 2 minutes |
| `~/bin/gv-bridge-restart.sh` | Nightly recycle. Kills the bridge, then delegates the relaunch to ensure |
| `~/bin/gv-keyring-unlock.py` | Unlocks the login keyring from stdin, over D-Bus |
| `~/bin/*.bak-<stamp>` | Backups made by `install-gv-bridge.sh` |
| `/etc/credstore.encrypted/radio-keyring.cred` | The keyring credential, mode 600, root-only to decrypt |
| `~/.local/share/gnome-shell/extensions/gv-bridge-behind@rotaryphone/` | The stacking extension |
| `~/.config/systemd/user/gv-bridge-watchdog.{service,timer}` | 2-minute liveness check |
| `~/.config/systemd/user/gv-bridge-restart.{service,timer}` | Nightly 04:00 recycle |
| `~/.config/autostart/gv-bridge-chrome.desktop` | Runs the ensure script at login |
| `~/Desktop/GV-Bridge.desktop` | Desktop shortcut. Must be mode 755: GNOME silently refuses to launch a group-writable `.desktop` file |
| `~/.config/gv-bridge-chrome/` | Chrome profile holding the authenticated session |
| `~/.config/gv-bridge-chrome.lock` | Launch lock shared by ensure and restart |
| `~/.local/state/gv-bridge-restart.log` | Launch, restart and keyring-unlock log |
| `/opt/rotary-phone/refresh-gv-cookies.sh` | Box-side cron (`*/20`) that calls `refresh-from-browser`. Not in this repo. Keep it: it is the only mechanism observed revalidating a stale browser session unattended |
| `/opt/rotary-phone/ChromeExtension/` | Superseded extension source. Passed to Chrome but not loaded |
| `~/bin/gv-session-alarm.sh` | Session alarm (`install-gv-session-alarm.sh`; timer off until `--enable`) |
| `/opt/rotary-phone/gv-account.conf` | Auto-relogin credential, mode 600, owner-populated |
| `~/bin/gv-auto-relogin.sh`, `gv-auto-relogin-breaker.sh`, `gv-cdp.py` | Auto-relogin actuator, circuit breaker and CDP helper |
| `~/bin/gv-relogin-signin.py` | The owner-written sign-in driver, shipped from `deploy/` and installed by `install-gv-auto-relogin.sh`. Without it, auto-relogin does nothing |
| `~/.local/state/gv-auto-relogin.state` | Breaker state. Inspect with `gv-auto-relogin.sh --status`; only `--reset` (a human) arms it |

### Auto-relogin (installed, inert until the owner enables it)

The deploy installs auto-relogin, including the sign-in driver `~/bin/gv-relogin-signin.py`, with its
timer **disabled** and its breaker **not armed**. Without the driver every cycle logs "not installed"
and changes nothing. The first unattended attempts (2026-09-26) failed on Google's password page with
the kiosk in front, and the cause is undiagnosed (see
[the occlusion flag](#rendering-a-covered-window-what-the-occlusion-flag-does-and-does-not-do)). The interface the
driver must meet is [`docs/gv-relogin-driver-contract.md`](gv-relogin-driver-contract.md). Design and
plan: [`docs/archive/gv-auth/`](archive/gv-auth/).

```bash
~/bin/gv-auto-relogin.sh --status          # breaker state, today's counts, driver installed or not
~/bin/gv-auto-relogin.sh --print-config    # resolved paths; never a credential value
~/bin/gv-auto-relogin.sh --reset           # a human arms the breaker (counters are kept)
bash /opt/rotary-phone/deploy/install-gv-auto-relogin.sh --enable   # the owner's step; refuses without
                                           # gv-account.conf (600, ours), the alarm, and the driver
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group relogin
```

### gv-account.conf (auto-relogin credential)

Read by `gv-auto-relogin.sh` **as data**: `KEY=value` lines, the value being everything after the first
`=`, verbatim (no quotes removed, no whitespace trimmed); `#` lines and blank lines ignored; exactly the
two keys below, once each; **LF line endings** (a CR would become part of the password). A malformed
file stops the breaker without any sign-in attempt.

```
# /opt/rotary-phone/gv-account.conf — mode 600, owned by the desktop user.
# Populated by the owner, on the box, by hand. Nothing in this repo writes, reads back,
# echoes or transports these values. The password is passed to the sign-in driver on
# stdin, never as an argument, because /proc/<pid>/cmdline is world-readable.
GV_ACCOUNT_EMAIL=
GV_ACCOUNT_PASSWORD=
```

- **Mode 600**, never group- or world-readable.
- **Protected from the deploy in both branches** of `deploy/Deploy-ToLinux.ps1`: the tar branch
  excludes it from the archive (never overwritten), and the rsync branch excludes it from `--delete`
  (never deleted). `deploy/tests/repro-tar-clobber.sh` proves both.
- **No template is committed and nothing in the repo creates this file.** A template in the publish
  tree would be a file the deploy could ship.

## Diagnostics

### Web UI

Open `http://<radio-box>:5004/diagnostics` for the SIP message log, HT801 health, and the call
timeline. `/gvbridge` shows the bridge status and SMS.

### API endpoints

```bash
curl http://localhost:5004/api/diagnostics/status                 # full snapshot
curl "http://localhost:5004/api/diagnostics/sip-log?count=20&method=INVITE"
curl http://localhost:5004/api/diagnostics/sip-registrations      # where INVITEs will actually go
curl http://localhost:5004/api/diagnostics/timeline
curl http://localhost:5004/api/diagnostics/audio-bridge
curl http://localhost:5004/api/diagnostics/ht801/config           # expected vs actual HT801 config
curl -X POST http://localhost:5004/api/diagnostics/test-ring      # rings the phone; see the caution below
```

### Logs

Use bounded reads on this box. It is shared with Radio Console, and heavy journald churn has
correlated with audible audio distortion there, so avoid `journalctl -f` and `tail -f`.

```bash
journalctl -u rotary-phone --since '-30min' -n 500 --no-pager
journalctl --user -u gv-bridge-watchdog.service --since '-1h' --no-pager
tail -n 50 ~/.local/state/gv-bridge-restart.log
systemctl --user list-timers 'gv-*'
```

## Troubleshooting

### Phone doesn't ring

1. **HT801 registration:** `curl http://localhost:5004/api/diagnostics/status` → `ht801.isRegistered`
   should be `true`, and `/api/diagnostics/sip-registrations` should show the HT801's address.
2. **SIP to Google:** `curl http://localhost:5004/api/gvbridge/status` → `sipRegistered` and
   `wsConnected` should be `true`. If `sipRegistered` is `false` with `cookiesValid:false`, the Google
   session is the problem; see [When the Google session is signed out](#when-the-google-session-is-signed-out).
3. **Test INVITE:** `POST /api/diagnostics/test-ring` and check the SIP log for `100`/`180`/`200`.
4. **INVITE times out:** the HT801 may need a factory reset (see [HT801 ATA](#ht801-ata)).

Whether Google Voice rings *in the browser* is irrelevant: incoming calls are signalled over SIP, and
the browser is not in the ring path.

### SMS or voicemail lists are empty

Almost always the CDP link to the bridge, not the API.

1. Is the bridge running? `pgrep -af "user-data-dir=$HOME/.config/gv-bridge-chrome"`
2. Is CDP answering? `curl -s http://localhost:9224/json/version`
3. Does a manual refresh succeed? (See the status-code table in [Verify the bridge](#verify-the-bridge).)
4. Is the window still signed in? Raise it from the GNOME overview.
5. Watchdog history: `tail ~/.local/state/gv-bridge-restart.log`

If the browser is up but CDP is silent, it was launched without `--remote-debugging-port=9224
--remote-allow-origins=*`. Kill it and run `~/bin/gv-bridge-ensure.sh`, which always supplies both.

### When the Google session is signed out

A dead Google session takes down SIP registration as well as SMS and voicemail, because SIP credentials
come from the same authenticated client. Typical status: `sipRegistered:false`, `cookiesValid:false`,
`refresh-from-browser` → 502, or `browserRefreshOutcome: SignedOut`.

The bridge tab's title and URL can be stale cached renders. To confirm, navigate the tab to
`https://voice.google.com/u/0/voicemail`: a redirect to `workspace.google.com/products/voice/` means
signed out.

Recovery: sign in again in the bridge window, confirm the URL stays on `voice.google.com`, then
`POST /api/gvbridge/cookies/refresh-from-browser`. Verify `sipRegistered:true` and
`/api/gvbridge/sms/threads` → 200.

### Chrome shows a keyring prompt behind the kiosk

The login keyring is locked. Check the restart log for `keyring unlock failed`, then check the
credential decrypts (`... | wc -c`, above), that `sudo -n systemd-creds --version` runs without a
password prompt, and that `~/bin/gv-keyring-unlock.py` is installed. If Secure Boot or firmware changed,
recreate the credential.

### The bridge window is on top of the kiosk

Check the extension is enabled and active and that `disable-user-extensions` is `false` (see
[The GNOME Shell extension](#the-gnome-shell-extension)). A freshly installed or updated extension only
loads after the next login.

### The extension is no longer in the path

Chrome has ignored `--load-extension` since v137, and the live profile lists only Chrome's built-in
extensions (verified 2026-08-18). Calls work regardless: audio runs on the SIPSorcery DTLS-SRTP path
inside the server, and answer and hang-up go over SIP rather than through the browser page. A live call
on 2026-08-18 reported `inboundFramesSent: 345`, `outboundFramesReceived: 341`,
`bidirectionalAudio: true` and zero errors. Do not write code that assumes the extension is loaded.

### After a reboot

Everything starts automatically. Allow a minute or two for the HT801 to re-register; if it does not,
reboot the HT801 (its registration timer can otherwise take up to 60 minutes). The bridge Chrome starts
15 s after login, unlocks the keyring first, and is lowered behind the kiosk by the extension. Expect
the first cookie refresh after a reboot to fail (see
[Does the Google session survive a Chrome restart?](#does-the-google-session-survive-a-chrome-restart)).

## Current status and limitations

**Working:** inbound calls end to end (ring, answer, two-way DTLS-SRTP audio, hang-up from either end);
caller hang-up while ringing stops the ringer (PR #95, 2026-10-03); rotary hang-up ends the Google Voice
leg promptly; outbound calls from the rotary dial (2026-06-13); decline of a ringing call through
`POST /api/phone/decline`; SMS and voicemail read through Google's HTTP API; the diagnostics UI and
REST API.

**Limitations:** see [docs/KNOWN-ISSUES.md](KNOWN-ISSUES.md). In particular, declining a call stops the
rotary ringer but not the linked cell phone, and unattended auto-relogin has not yet
worked (its timer is disabled).

**HT801 caution:** `test-ring` has in the past left the HT801 stuck after its BYE drew a `481`
response, needing an HT801 reboot. This has not been re-checked since the SIP dialog fixes in PR #95, so
reboot the HT801 if calls stop ringing after a test-ring.
