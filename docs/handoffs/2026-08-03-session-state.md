# Session state — GV auth blackout arc (B1/B2) + open incident

**Saved:** 2026-08-03
**Repo state:** `main` @ `c097a53`. User tree on `diag/gv-srtp-receive` @ `28a9f8a`, untouched all session.
**Status:** Arc shipped. **One live outage, blocked on physical access. One approved task never started.**

---

## 🔴 Read this first — the phone is still down, and the obvious fix is a trap

```
available:false  sipRegistered:false  wsConnected:false  cookiesValid:false  /sms/threads → 502
```

Down since roughly 2026-08-01 evening. **Do not act on the recovery steps from the 08-01 session** — they were
written before the real cause was known, and one of them is now actively dangerous.

### What it actually is

Not (only) a dead Google session. Since the **2026-08-02 03:03:17** boot, **GDM autologin leaves the login
keyring locked**, and gnome-keyring raises an *"Authentication required"* modal that grabs input and blocks
every browser on the box. Radio Console found this during an owner-authorized OSK investigation and wrote it
up in `docs/prompts/radioconsole-keyring-modal-blocks-both-services-request.md` (untracked, in this repo).

It took down **both** services for ~35 hours before anyone noticed. Radio Console fixed their own kiosk with
`--password-store=basic` (their PR #463).

### ⚠️ Do NOT add `--password-store=basic` to the GV bridge

Radio Console tried this on our behalf, with owner authorization, **and reverted it.** Measured:

| | Cookies |
|---|---|
| Before | **45, all `v11`** — every Google session cookie present |
| After the flag | **16, all `v10`** — every Google session cookie gone, Chrome on the logged-out page |

All 45 GV cookies are `v11`, encrypted with the keyring-derived key. `basic` makes that key unobtainable, so
Chrome discards them. They restored the scripts *and* the cookie DB, sha256-verified. **The GV profile is
currently intact: 45 `v11` cookies.** The flag is only safe paired with a planned physical re-login.

(Their snap-Chromium is immune because snap forces `basic` from first run — that profile is `v10` throughout.)

### ⚠️ Do NOT run `POST /api/gvbridge/cookies/refresh-from-browser` right now

This is the synthesis of their finding and ours, and it is the single most important line in this document.

The good cookies are **still on disk**. Chrome is blocked at profile init by the modal, so a refresh would
harvest nothing usable and **overwrite 45 working cookies with dead ones** — precisely the credential-downgrade
path documented in PR #73. That is how the 08-01 incident got worse.

**The cron `*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh` is still running and fires this path every
20 minutes.** It has presumably already done so many times. Verify the on-disk cookie state before assuming
anything survived.

### The real fix — needs physical access

Root cause is that autologin never unlocks the keyring; every browser is a downstream victim. Fix at the source:

1. **PAM auto-unlock** — `pam_gnome_keyring.so` in the GDM stack. Standard answer for a kiosk with autologin.
2. **Or an empty-password login keyring** — cruder, entirely reasonable for an appliance on a private LAN, and
   it keeps the `v11` cookies working exactly as they do today.

Either removes the dialog permanently *and* preserves the session. Both need a password prompt at minimum.
Radio Console's note says the owner is away from the hardware until roughly **2026-08-10**.

They deliberately did **not** restart gnome-keyring: the box is on WiFi (`enp1s0` unavailable), and risking the
only link on an unattended box for a week is the worse trade.

---

## ✅ What shipped this session

| PR | State | What |
|---|---|---|
| **#70** | Merged | B1 — decode `%2F` thread ids so group/MMS threads are readable |
| **#71** | Merged | B2 spec + plan, with 5 owner decisions recorded as resolved |
| **#72** | Merged `738141f` | **B2 implementation** — real refresh cadence, reactive 401 recovery, honest status |
| **#73** | Merged `c097a53` | Incident record: Chrome session death + the credential-downgrade path |

### B2 UAT result (2026-08-01, pre-outage)

**6 of 7 acceptance criteria passed by measurement, 1 partial, 0 failed.**

- **932 requests over ~88 min of window-blind soak — zero 502s.** Pre-fix baseline for the same shape: 31% 502s.
- `api2thread/list returned Unauthorized`: **33/hr → 0**.
- **F7 confirmed fixed on hardware** — `lastHealthyAt` ticked 30m00.01s across six re-activations. Pre-fix the
  30-minute watchdog never fired at all.
- **Reuse path holds** — all six re-activations took Path B, `lastConnectedAt` one distinct value, zero teardowns.
- **AC-2 live** — real 401, all three recovery rungs in order, replay returned 200, **920 ms** end to end.
- **Task 0 answered:** `RotateCookies` is *not* inert. It rotates on fresh cookies, 401s once PSIDTS is stale.
  So rung 1 is an effective **proactive** mechanism and a useless **reactive** one — CDP carries the reactive path.

### Key design decision, ratified mid-flight

Builder overrode the owner's literal "rebuild when credentials changed" rule in favor of **health-only reuse**,
because PSIDTS rotates ~every 11 min against a 20-min cron — the literal rule would have rebuilt on nearly every
fire and reduced no churn at all. Owner ratified after review.

**This turned out to be load-bearing.** The original unconditional-teardown form disposed the old 30-minute health
timer and armed a fresh one on every cron fire — F7's exact starvation shape. PR #72 as first built would not have
fixed F7 while every test passed. One-line switch back to the literal rule is documented at `GVApiAdapter.cs:490`.

---

## 📋 Open items

### 1. Repo-wide phone-number redaction — APPROVED, NEVER STARTED

The owner approved this on 2026-08-01 and the outage preempted it. **This is the one piece of approved work that
did not get done.**

Scope: ~6 committed files carry real third-party phone numbers on a **public** repo. Wanted: scrub to stable
placeholders, add a CLAUDE.md rule so agents don't reintroduce them, and decide whether git history needs
rewriting or the exposure is accepted as sunk.

Context: during this session a subagent posted the real `GvPhoneNumber` to a public PR comment. It was deleted
and reposted redacted within ~20 min (original preserved at `/tmp/uat-recover/uat-comment-original.md`, which
**will not survive a reboot**). No credentials or tokens were ever exposed — that was checked specifically.
Root cause was a coordinator miss: Tester was told to post evidence to the PR without being told the repo is public.

### 2. `psidtsAgeSeconds` is misleading when no session exists — NEW, from Radio Console

It reads **135266 s (~37.6 h)** right now, which is time since *service start*, not cookie age. Radio Console
had been documenting it as a live blackout clock (`<660` healthy / `660–1200` blackout). **That reading is only
valid once a session actually exists.** With no session it reports two orders of magnitude outside both bands.

This matters because we told Radio Console to bind their degraded-state UI to our status fields. Worth deciding
whether to null it, clamp it, or document the precondition.

### 3. Inbound call ringing — never tested

Owner merged #72 knowingly. Tester could not originate a call to the GV number. Task 3 touches `_sipTransport`
teardown, and because the reuse path makes teardown rare, a ringing regression would be intermittent. One manual
inbound call closes this. Logged in `KNOWN-ISSUES.md`.

### 4. AC-3 partial — `authBlackout` never observed live

`available:true` and the 401 latch verified across 411 samples, but `authBlackout:true` was never caught —
recovery (920 ms) is faster than any practical sampling rate. Behavior during a *sustained* blackout remains
unit-test-only. **Radio Console has been told `authBlackout` may be true for under a second** — a banner bound
naively to it will effectively never show.

### 5. M1 — retire the `*/20` cron. Escalated from cleanup to hazard.

Originally deferred "until the in-process refresh is proven." It is proven (33/hr → 0). Then it got worse: B2's
in-process refresh keeps the app's cookie lineage alive **independently of Chrome's jar**, so the two diverge and
the cron becomes a scheduled *downgrade* path. **B2 made the cron dangerous.** See §"do not run refresh-from-browser"
above — this is the same mechanism.

Proposed hardening (in PR #73): validate a cookie set *before* adopting it, keep a last-known-good, never let an
unvalidated refresh overwrite a validated one.

### 6. L3 — publish clobbers `appsettings.Production.json`

Recurs on **every** deploy. Clobbered values include `BluetoothAdapter: hci1`, `UseActualBluetoothHfp`,
`EnableMarkRead`, and the GV number. **This crosses the Radio Console audio boundary** — a silent BT-config change
breaks their audio. Mitigated for now by deploying with rsync and excluding the file; box config sha verified
byte-identical before/after. Wants a permanent fix in the deploy script.

### 7. Two smaller items from Radio Console

- **`--window-position` is a no-op under Wayland.** Both our browsers pass it and both are ignored. The GV browser
  at `10000,10000` is **not off-screen** — several prior sessions have described it that way and they were wrong.
  Visibility is decided purely by window stacking order. Genuine hiding needs a compositor rule or a dedicated
  workspace, not a Chrome flag.
- **Watchdog units are not running.** `gv-bridge-watchdog.service` / `.timer` are inactive and `disabled` — hand-
  started on an earlier boot, did not survive the 08-02 reboot. What actually launches the GV browser today is
  `~/.config/autostart/gv-bridge-chrome.desktop` → `~/bin/gv-bridge-ensure.sh` → `systemd-run --user`. Left as
  found. Worth deciding whether they should be `enable`d.

---

## Environment

- **SSH:** `ssh radio` works from WSL (`~/.ssh/config`, key `~/.ssh/id_ed25519_radio` — a copy of the Windows
  `id_ed25519`, unencrypted). **Owner may want this deleted when the arc closes.** Note the config was migrated
  to a full homelab config on 2026-08-03; `radio` is pinned to `192.168.86.50` by IP deliberately.
- **Box:** Intel N100, 4 cores, shared with Radio Console. All diagnostics must be **bounded and non-streaming** —
  `--since` *and* `-n`, never `-f`/`tail -f`. Unbounded reads have caused audible distortion in their audio.
- **Build:** targets `net10.0`; .NET 10.0.302 in `~/.dotnet`; Linux builds need `-p:EnableWindowsTargeting=true`.
  Deploy with `-p:ContinuousIntegrationBuild=true -p:DeterministicSourcePaths=true` so PDBs don't embed a
  worktree path (source paths normalize to `/_/`).
- **Rollback point:** `/opt/rotary-phone.bak.prefix-uat-20260801-160513` (287 MB, pre-B2 binary). Still in place.
- **Boundary:** RotaryPhone owns Intel AX201 (`hci1`, `10:91:D1:FE:00:46`). Radio Console owns TP-Link UB500
  (`hci0`). `bluetoothctl select 10:91:D1:FE:00:46` before any bluetoothctl command.

## User's working tree — preserve it

`diag/gv-srtp-receive` @ `28a9f8a`, byte-identical since 2026-08-01: 1 modified boundary doc + 7 untracked files.
Every agent this session worked in a throwaway worktree to keep it that way.

**Known collision:** six of those untracked docs now also exist on `origin/main`. The next merge or rebase of main
into this branch will refuse with *"untracked working tree files would be overwritten."* Decide per file; for
`gv-websocket-keepalive-reconnect.md`, main's copy is the good one (SHIPPED banner, LF endings).

---

## Suggested first moves tomorrow

1. **Check whether the cron has destroyed the cookie store.** 45 `v11` cookies were intact as of Radio Console's
   restore; the `*/20` refresh has been firing against a blocked Chrome ever since. This decides whether recovery
   on 08-10 is a re-login or something worse.
2. **Consider disabling just the cron** as a stop-loss — box-side, reversible, and it halts the downgrade path
   without touching code. Weigh against decision 2's original reasoning, which the 08-01 evidence has since overturned.
3. **Start the redaction pass** — approved, doesn't need the box, and is the only shovel-ready item.
4. Everything else waits on physical access (~2026-08-10).
