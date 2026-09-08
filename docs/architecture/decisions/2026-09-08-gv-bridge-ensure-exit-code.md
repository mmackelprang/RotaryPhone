# ADR: `gv-bridge-ensure.sh` exit code — it stays 0, and it is not a health signal

- **Status:** Accepted. Closes the open question recorded in the boundary doc's 2026-09-08 Change Log row.
- **Date:** 2026-09-08
- **Author:** Architect
- **Request being answered:** RadioConsole `KIOSK-2` binds a `VOICE` amber row to this script's exit code
  (their `docs/queue/CROSS-REPO-HANDOFFS.md` item 8).
- **Interim guidance already delivered:** `docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md`
  — use `pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"` or `/api/gvbridge/status`; the
  exit-code question is open on our side. **This ADR closes it.**
- **Baseline:** `main` @ `3c2c892`.

---

## 1. Context

RadioConsole's `KIOSK-2` runs `gv-bridge-ensure.sh` to repair a bridge it has found down, then reads the
**exit code** to decide whether its `VOICE` row shows amber. **It cannot work as specified.**

`deploy/gv-bridge-ensure.sh` runs under `set -u` with **no `set -e`**, and exits 0 on every reachable path:

| Path | Line | Exit |
|---|---|---|
| Lock held by the other launcher | `:50` `flock -n 9 \|\| exit 0` | 0 |
| Bridge already up | `:54-56` `pgrep -f "${MARKER}"` | 0 |
| **Launch failed** | `:105-106` | **0** |

The last two statements are the whole problem:

```bash
systemd-run --user --collect google-chrome "${CHROME_ARGS[@]}" >> "${LOG}" 2>&1
echo "$(ts) ensure: bridge was down -> launched" >> "${LOG}"
```

`systemd-run`'s status is discarded, and the script's exit code is the **unconditional `echo`** — which
essentially always succeeds. Verified 2026-09-08 with `google-chrome` absent: it logged
`Failed to find executable google-chrome`, then logged `ensure: bridge was down -> launched`
(**an overclaim**), then exited **0**.

A nonzero status from this script therefore means the *interpreter* failed — file missing, not
executable, a `set -u` violation — **never "the bridge is down."**

The trade the dispatch names: making the exit code meaningful would put `gv-bridge-watchdog.service` —
`Type=oneshot`, no `Restart=`, no `SuccessExitStatus=` (`deploy/systemd/gv-bridge-watchdog.service`) —
into a **failed** state on every failed launch, every 2 minutes (`gv-bridge-watchdog.timer`,
`OnUnitActiveSec=2min`), which also drags the user manager to `degraded`.

---

## 2. The measurement that decides it

Before weighing that trade, the prior question: **would a "meaningful" exit code actually be meaningful?**

Measured on the box, 2026-09-08:

```
$ systemd-run --user --collect definitely-not-a-real-binary-xyz --flag
Failed to find executable definitely-not-a-real-binary-xyz: No such file or directory
systemd-run exit=1

$ systemd-run --user --collect /bin/false
Running as unit: run-rae3f8e196bef423e8eddd048ba17cc74.service
systemd-run exit for a binary that exists but FAILS at runtime = 0
```

**`systemd-run` validates the executable client-side, then returns as soon as the transient unit is
enqueued.** It reports failure only when the binary cannot be found. If the binary exists and the
process dies one millisecond later, `systemd-run` has already returned **0**.

So propagating that status — with `set -e`, or `|| exit 1` — would catch exactly one failure mode:
**`google-chrome` is not installed.** Every failure that actually happens on this box returns 0:

- Chrome present but exits at startup (corrupt profile, surviving `Singleton*` lock, OOM kill)
- Wayland display unavailable or the session not yet up
- Chrome starts, fails to reach `voice.google.com`, and sits there unauthenticated
- Chrome starts and is killed by the nightly recycle a moment later

**A propagated exit code would be a false-negative machine**: it would report success through
essentially every real outage, while *looking* like a health signal — and would therefore be trusted
precisely because it looks meaningful. That is the same failure class this project has already been
bitten by twice and RadioConsole has documented both times: `Succeeded: true` meaning only "the JSON
parsed," and `/api/gvbridge/status` reporting `available:true` straight through a 9-minute auth
blackout. Shipping a third instance deliberately would be indefensible.

There is a deeper reason the exit code can never answer the question. **The script is an idempotent
repair *action*, not a probe.** Chrome self-reparents out of the transient scope (`:12-16`) and takes
seconds to become useful. Even a perfect launch-status would mean "the launch was accepted," never
"the bridge is up" — and certainly never "the bridge is authenticated and CDP cookie extraction is
working," which is what a `VOICE` row actually cares about.

---

## 3. Options considered

- **(a) Propagate `systemd-run`'s status** (`set -e`, or `|| exit 1`). **Rejected.** Per §2 it detects
  only "chrome not installed" while presenting as a general health signal, and it costs a permanently
  `failed` watchdog unit and a `degraded` user manager for as long as the condition persists.
- **(b) Make the exit code genuinely mean "the bridge is up":** `systemd-run --wait`, or a post-launch
  `pgrep` poll loop against `${MARKER}` with a timeout. **Rejected.** It converts a 2-minute idempotent
  watchdog into a blocking probe with a multi-second tail on every cycle, serialized under the same
  `flock` the nightly recycle takes (`:42-51`) — so a slow launch now delays the recycle. It still
  cannot report *authenticated*. And it makes the watchdog unit's failure state track a transient
  condition, which is what `Restart=`/alerting are for, not a oneshot's exit status.
- **(c) Keep exit 0; state the contract explicitly; fix the lying log line. — CHOSEN.**
- **(d) Add a separate `--check` / `--status` mode** that probes and exits meaningfully, leaving the
  default repair path at 0. **Rejected as unnecessary**, not as wrong: it would reimplement, behind a
  RotaryPhone-owned interface we would then have to keep stable, exactly the one-line `pgrep` RadioConsole
  can run directly, plus a liveness answer `/api/gvbridge/status` already gives better. Revisit only if a
  third consumer appears.

---

## 4. Decision

**`gv-bridge-ensure.sh` keeps exiting 0. Its exit code is not a health signal and must not be used as
one. This is now a stated contract, not an accident of the code.**

Three changes, all inside the script, none of which alters its behaviour or its interface:

1. **State the contract in the header comment**, next to the existing "Idempotent by contract" note
   (`:10-11`): the exit code reports only whether the *script* ran, never whether the bridge is up; a
   consumer wanting liveness must test the marker or read `/api/gvbridge/status`.
2. **Stop the log line overclaiming.** `:106` unconditionally writes `ensure: bridge was down -> launched`
   even when `systemd-run` failed. Capture the status and log the truth — launched vs. launch-failed,
   with the status. This gives operators an honest log without turning the exit code into a false health
   signal, and it is the half of the current behaviour that is indefensible on its own terms: the script
   *knew* the launch failed and wrote that it had succeeded.
3. **Leave `set -e` off.** Adding it now would silently convert paths (a) and (b) into hard failures and
   change the exit-code contract as a side effect — the opposite of this decision. Its absence is
   load-bearing and should be commented as such.

**What RadioConsole does instead** — already delivered as interim guidance and now permanent:

- **Liveness:** `pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"` — the same marker the script
  itself uses (`:31`, `:54`), so the two can never disagree.
- **Usefulness (better for a `VOICE` row):** `GET /api/gvbridge/status`, which answers whether the bridge
  is *doing its job* — cookies valid, not in an auth blackout — rather than whether a process exists.
  Bind to `degraded` / `authBlackout`, **not** `available` (boundary doc, 2026-08-01 rows).
- **Treat the script strictly as a repair action whose effect must be re-checked afterwards.**

**Ownership is unchanged.** The script, `gv-bridge-watchdog.timer` and the nightly
`gv-bridge-restart.timer` stay RotaryPhone's. RadioConsole only ever invokes the script.

---

## 5. Consequences

**Good:**
- No new false health signal, and no third instance of the "a status field that reports healthy through
  an outage" failure class.
- `gv-bridge-watchdog.service` stays `active`/`inactive` rather than parking in `failed` every 2 minutes,
  so the user manager's `degraded` state keeps meaning something.
- The log stops lying, which is the actual operator-visible defect here.
- RadioConsole's `KIOSK-2` gets a liveness check that is strictly more accurate than the one it asked for,
  and a health check (`/api/gvbridge/status`) that is more useful than either.

**Bad / costs:**
- **RadioConsole must change `KIOSK-2` before it can work.** Their specified design cannot ship. This is a
  cost on their side and they should be told plainly — though it is a cost they already carry, since the
  binding does not work today either.
- A launch failure remains invisible to `systemctl --failed`; it is visible only in
  `~/.local/state/gv-bridge-restart.log`. **We are choosing log-visibility over unit-visibility**, which
  is the right trade for a 2-minute self-healing watchdog but does mean a persistent
  "Chrome uninstalled" condition has no unit-level alarm. If that case ever needs alarming, it belongs in
  a monitor that checks the marker, not in this script's exit code.

**Neutral:**
- The `flock`-not-taken and already-up paths keep exiting 0. They were always correct — both mean "the
  outcome this script wants is already happening."

---

## 6. Recommended boundary-doc update — one row, and it is an amendment

The boundary doc's **existing 2026-09-08 row already carries this as an open question**: *"Whether to
give the script a meaningful exit code is an open RotaryPhone decision … this row will be updated if
that changes."*

**Recommend amending that row rather than adding a new one** — the contract it pins (path + exit code) is
the thing being settled, and a second row would leave the first one reading as still-open. The amendment
should record: the decision (exit code stays 0, permanently, by contract not by accident), the measured
reason (`systemd-run` returns 0 whenever the binary exists, so a propagated status would detect only
"chrome not installed"), and the two supported checks. This is a **cross-service tooling contract**, so
the Change Log is the right lane even though it is not a BT/audio ownership change — consistent with how
that row was filed in the first place.

⚠ Per the 2026-07-16 and 2026-08-10 precedent — both of which sat uncommitted, one of them the entry that
*established* the rule — **this edit lives in the RotaryPhone repo and must be committed here.**

---

## 7. Related decisions

- Boundary doc `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`, 2026-09-08 row — the open question this
  closes; amend per §6.
- `docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md` — where the interim guidance was
  delivered.
- Boundary doc 2026-08-01 rows — why a `VOICE` row must bind to `degraded`/`authBlackout` rather than
  `available`, and the measured 920 ms blackout that makes a naively-bound boolean useless.

## 8. Open questions

1. **Does anything monitor the user manager's `degraded` state?** If nothing does, option (a)'s unit-state
   cost is smaller than assumed — though §2's false-negative argument rejects it regardless, so this does
   not reopen the decision.
2. **Should a persistent launch failure alarm somewhere?** Out of scope here (the answer is not this
   script's exit code), but "Chrome has been uninstalled/broken for an hour" currently reaches nobody.
   Candidate: fold bridge-process liveness into `/api/gvbridge/status`, which RadioConsole already polls.
