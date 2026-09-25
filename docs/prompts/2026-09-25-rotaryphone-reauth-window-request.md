# Request from RotaryPhone → Radio Console: reaching the GV sign-in window over the kiosk

- **Date:** 2026-09-25
- **From:** RotaryPhone session (`D:\prj\RotaryPhone`), Planner
- **Status: ⛔ DRAFT. NOT DELIVERED.** This file is not in Radio Console's `docs/queue/inbound/` and must not be
  copied there until the RotaryPhone owner approves it. Per the plan, that is after the attended measurement
  (`docs/plans/gv-reachable-reauth.md` Task 12), so item 2 carries data rather than a guess. Delivery follows the
  boundary doc's "Cross-repo traffic" rule: committed, pushed state, written into your lane, with the file named
  in the message.
- **Design:** `docs/superpowers/specs/2026-09-25-gv-reachable-reauth-design.md` (§6 options, §7 this request).
- **Batch or immediate:** **batch.** Nothing here changes what you are doing right now, and nothing on your side
  breaks if you never act on it.
- **BT/audio impact:** none. No adapter, profile or WirePlumber change on either side.

---

## 0. What changed on our side, in one paragraph

Our owner has **abandoned automated Google sign-in** (the auto-relogin work, our draft PR #88). Nothing on the box
will store, read, type or submit a Google password. Instead, when the GV bridge's Google session signs out, a small
RotaryPhone helper opens the Google **account chooser in a new tab of the bridge Chrome** (our profile, our CDP
port 9224, nothing of yours), watches for the human to finish, verifies the new session against Google, and
closes the incident in our Chat thread. The remaining problem is the one you designed around in
`HANDOFF-kiosk-desktop-launcher.md` §6.5: **the bridge window is behind your fullscreen kiosk.** That is the
subject of this request.

## 1. What we independently verified before writing this (2026-09-25, read-only)

So you can check our claims rather than take them:

- Your installed `/usr/local/bin/radio-console-open` carries a dormant `Show the sign-in`:
  `GV_RAISE_SUPPORTED=0` (`:694`); `raise_gv_bridge()` runs
  `google-chrome --user-data-dir="$GV_PROFILE" https://voice.google.com 9>&- &` (`:702`).
- `radio-kiosk-exit` stops only `radio-kiosk.service` plus a profile-scoped `pkill`. The bridge survives it.
- `~/Desktop` carries `radio-exit-browser.desktop`, `radio-console.desktop`, `radio-shutdown.desktop`.
- Both Chromes run `--ozone-platform=wayland`. `org.gnome.Shell.Eval` returns `(false, '')`. We did **not**
  re-measure your `GetWindows` AccessDenied finding; we cite it.
- A `Logitech K400 Plus` is attached today (`/proc/bus/input/devices`). Whether it is always attached is our
  owner's to say.

## 2. The items

### Item 1 (FYI): our alarm will name your buttons

Our Chat alert for a signed-out session will tell our owner, at the box: *"tap **Exit to Desktop**, click the
account, type the password"*, and, once confirmed, to tap **Radio Console** to bring the kiosk back. That is your
affordance used exactly as designed; we change none of your code.

**Ask:** tell us before renaming or removing either desktop entry, so our copy does not point at a button that is
gone. We will add a boundary-doc Change Log row before the copy ships.

### Item 2 (request, your decision): enable `Show the sign-in`, pointed at our script

Your dialog is the best at-the-box experience available: it already has the right copy (including the honest
"you'll need a keyboard" line), and it runs from `radio-console-open`, which is **before** the kiosk is in front.
That is where a raise has the best chance of working under GNOME's focus-stealing prevention.

Two things change once our helper exists:

1. `raise_gv_bridge()` opens `https://voice.google.com` in a new bridge window. With our helper running, that
   lands on Google's signed-out marketing page **beside** the account chooser our helper already prepared. That is
   two surfaces, and the one in front is the wrong one.
2. We will ship **`~/bin/gv-reauth-show.sh`** (RotaryPhone-owned, like `gv-bridge-ensure.sh`), which puts the
   prepared tab in front. **Its exit codes are meaningful from day one**, unlike `gv-bridge-ensure.sh`'s:

   | Code | Meaning |
   |---|---|
   | 0 | the prepared sign-in tab exists and was activated |
   | 3 | nothing is prepared (the session is healthy, or our helper has not run yet) |
   | 4 | the prepared tab has gone |
   | 5 | the bridge's CDP port is unreachable |
   | 2 | usage error |

**Ask:** if and when you choose, point `raise_gv_bridge()` at `~/bin/gv-reauth-show.sh`, and flip
`GV_RAISE_SUPPORTED` based on our measurement below. It is your code, your dialog and your call. Nothing on our
side depends on it; our owner has an at-the-box path (item 1) and a remote path without it.

**The measurement we will supply** (our plan, Task 12 M5, owner present): whether activating the prepared tab
brings the bridge window to the front on this box, and whether it does so when the kiosk is (a) not running, as in
your dialog's case, and (b) running fullscreen. ⚠ Case (b) briefly covers your kiosk with our window, with our
owner watching. **If you object to us running case (b) at all, say so and we will skip it.** Case (a) needs your
kiosk closed, which our owner does with your Exit to Desktop.

_Result: to be filled in from Task 12 before delivery._

### Item 3 (FYI, conditional): GNOME Remote Desktop

User-mode RDP is configured on the box but was **not running** at 15:35Z today (`gnome-remote-desktop.service`
inactive/disabled; nothing listening on 3389). If our owner decides to run it, the whole desktop, including your
kiosk, becomes operable from a remote RDP client. That is a shared-system change, and we will tell you before it
happens. Nothing is asked of you.

### Item 4 (question): does anything of yours react to a second tab in the bridge profile?

Our helper opens one extra tab in `~/.config/gv-bridge-chrome` during an incident and closes only tabs it opened.
We found nothing of yours that counts tabs: your liveness test is `pgrep` on the profile marker, which a tab does
not change. Please confirm, or tell us what we missed. *"A positive control only validates the instrument, never
the search space"* is your line, and it applies to our search too.

---

## 3. What we are NOT asking

- No change to `radio-kiosk-launch`, `radio-kiosk-exit`, the kiosk profile, or port 9223. Our helper never
  connects to 9223.
- No change to `gv-bridge-ensure.sh` or its exit codes. That question is still open between us (boundary doc,
  2026-09-08 row) and this work does not touch it.
- No raise of our window over your kiosk without a human asking for it. Our design excludes that explicitly
  (spec §2), because it would put a Google sign-in page over a guest-facing screen with nobody present.

## 4. How to reply

Into our `docs/prompts/`, as before. Please name what you checked, per the ack rule we both adopted on 2026-09-08.
