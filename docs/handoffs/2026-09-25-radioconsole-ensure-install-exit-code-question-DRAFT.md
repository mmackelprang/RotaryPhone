# DRAFT, NOT DELIVERED: installing the current `gv-bridge-ensure.sh`, and its exit code

- **From:** RotaryPhone (Builder session, branch `fix/bridge-chrome-occlusion-flag`)
- **To:** Radio Console
- **Status:** ⛔ **Draft for the owner. Not in Radio Console's `docs/queue/inbound/`.** Per the boundary
  doc's 2026-09-09 delivery rule, a file in *our* `docs/handoffs/` is a record, not a delivery. The owner
  decides whether and when to send it. It must go from committed, pushed state.
- **Urgency under the batching rule:** 📦 batch. Nothing breaks until someone installs the script, and
  RotaryPhone will not install it before your answer.

---

## Why we are asking now

RotaryPhone wants to add one Chrome flag, `--disable-backgrounding-occluded-windows`, to the GV bridge
browser. The bridge window sits behind your fullscreen kiosk, and our auto-relogin driver could not see a
password field render while the window was covered. **Your kiosk launcher already passes this flag**
(`/usr/local/bin/radio-kiosk-launch`, `CHROME_FLAGS`).

The flag only reaches the box when `~/bin/gv-bridge-ensure.sh` is replaced. The installed copy is the
2026-08-18 13-line script (sha256 `fd04f1ff…`). Replacing it also brings in everything the repo copy has
gained since then, and one of those changes touches the contract you consume.

## What changes for a caller of `~/bin/gv-bridge-ensure.sh`

| | Installed today (Aug 18) | After the install |
|---|---|---|
| Bridge already up | exit 0 | exit 0 |
| Launch attempted (success **or** failure) | exit 0 | exit 0 |
| **Another launcher holds the lock** | *(no lock exists)* | **exit 0, immediately, without launching** |
| Blocks? | no | no (`flock -n`, never waits) |
| Path | `~/bin/gv-bridge-ensure.sh` | unchanged |

The lock is held by the 2-minute watchdog while it launches, and by `gv-bridge-restart.sh` across its kill,
which takes about 4 s plus one launch. The nightly restart timer is installed but **disabled**.

## What we read on your side (installed launcher, measured 2026-09-25, read-only)

`/usr/local/bin/radio-console-open`:

- `:414` `start_voice() { … timeout 20 "$s"; }`
- `:473` `if start_voice; then wait_for probe_voice_process 20 && STATE[VOICE]=started || STATE[VOICE]=starting`
- `:188` `probe_voice_process() { pgrep -f -- "--user-data-dir=$GV_PROFILE" …; }`

As we read it, exit 0 means "the action was accepted, now re-check by `pgrep` for up to 20 s", and a
nonzero exit means `failed`. That is the reading ADR
`docs/architecture/decisions/2026-09-08-gv-bridge-ensure-exit-code.md` asked for. If it is right, the
lock-held 0 is harmless to you: the process the lock-holder is launching shows up inside your 20 s
window, or it was already up.

⚠ **We have not tested this against your launcher.** It is a reading of your code, which is why this is a
question and not a notice.

## The questions

1. **Is our reading of `:414` and `:473` right?** In particular, does anything in `KIOSK-2` still treat a
   bare exit 0 as "VOICE is up" without the `pgrep` re-check?
2. **Is anything else of yours invoking `gv-bridge-ensure.sh` and reading its exit code?** For example a
   systemd unit, a setup script, or a test fixture.
3. **Do you accept the lock-held path as specified**, meaning exit 0 immediately, no launch by this caller,
   and liveness to be re-checked? Or do you need it to be distinguishable?
4. **The ADR's decision is that the exit code stays 0 by contract and is not a health signal.** Our own
   2026-09-09 spec (§8, §11 decision 4) and the boundary doc's 2026-09-08 row both still call that
   question open. Do you consider it settled? If you do, we will amend the 2026-09-08 row in one edit, as
   the ADR's §6 recommends, and stop calling it open.

If the answer to 1–3 is "yes, fine", nothing on your side needs to change. The owner installs the two
scripts during an attended session and announces it in the boundary Change Log.

## What does NOT change

No BT or audio change. `hci0`/`hci1` ownership, profiles and WirePlumber configs are untouched. The window
stacking and focus are unchanged: the bridge stays behind the kiosk and is never raised. Your kiosk
profile, its CDP port 9223 and the kiosk process are untouched.
