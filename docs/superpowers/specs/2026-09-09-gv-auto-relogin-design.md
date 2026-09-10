# GV auto-relogin — design

**Date:** 2026-09-09 (evening)
**Status:** shape approved by owner; not started
**Depends on:** `2026-09-09-gv-session-alarm-design.md` (shipped as PR #85) and the
cannibalisation falsification test started 17:33 EDT.

---

## 1. Goal, in the owner's words

> *"I'd prefer to have the login credentials in a configuration file stored only on the radio
> device. I want to not have me in the loop at all except for catastrophic problems."*

⭐ **The shape that delivers it:** auto-relogin handles the routine case silently; **the alarm shipped
in PR #85 becomes the escalation path**, firing only when auto-relogin itself fails. Routine is
invisible; catastrophic reaches a human. Most of the escalation half already exists and is tested.

## 2. Prerequisites — established with the owner 2026-09-09, not assumed

| Fact | Value | Why it matters |
|---|---|---|
| Second factor on the GV account | **None** | The only case that automates without a second device. No TOTP seed, no prompt, no key. |
| Account identity | **Dedicated to the phone** | A leaked credential costs the phone, not the owner's email/Drive/Photos. This is what makes storing it on a shared box acceptable. |
| CDP control of the bridge | **Proven 2026-09-09** | `Runtime.evaluate` and `Page.captureScreenshot` both succeeded against `:9224`. |
| DevTools origin check | **Not an obstacle here** | `gv-bridge-ensure.sh:99` already sets `--remote-allow-origins=*`, marked LOAD-BEARING. The websocket connected first try, without `suppress_origin`. |

⚠ **If any of these changes — 2FA added, account merged into the primary, the origin flag narrowed —
this design's safety case changes with it.** Re-check them before shipping, not once at design time.

## 3. ⛔ The risk that shapes the whole design

**Automated login is only safe if login is RARE.** Google's abuse detection keys on repeated
programmatic sign-ins. If the session dies hourly and we re-login hourly, that is ~24 sign-ins/day
from one IP through a debug-flagged browser — a strong lockout signal.

⛔ **And an account lock is strictly worse than the problem being solved.** It is a catastrophic
event requiring the owner urgently, with the phone down and no fallback — the exact state this work
exists to prevent. Automation that raises the rate of sign-ins is therefore not a safety-neutral
convenience; it is a trade against account survival.

⭐ **Consequence:** the circuit breaker (§6) is not a nicety bolted on at the end. It is the feature.
A version of this that logs in reliably but retries freely is worse than no automation at all.

⚠ **Sequencing:** the cannibalisation test running since 17:33 EDT decides which regime we are in. At
105 minutes it had not gone stale, against prior deaths at ~50 and ~111 minutes. **If sessions return
to lasting months, this design is safe. If they still die hourly, do not ship the actuator — fix the
death rate first.** That is a gate, not a preference.

## 4. Credential store

**Path:** `/opt/rotary-phone/gv-account.conf` — mode `600`, owner `mmack`, group-readable never.

```
GV_ACCOUNT_EMAIL=...
GV_ACCOUNT_PASSWORD=...
```

- ⛔ **Excluded from the deploy archive**, exactly as `appsettings.Production.json` is, so a deploy
  can neither clobber it nor ship it off the box. Add it to the same exclusion, and add a test that
  proves the exclusion holds — a config-protection test already exists to model.
- ⛔ **Never passed in argv.** The bearer-token finding of 2026-09-09 applies verbatim: a secret in a
  command line is world-readable in `/proc/<pid>/cmdline` on a box shared with Radio Console. Source
  the file into the environment, or feed it on stdin.
- **Never logged, never echoed, never included in an alarm body.** The alarm quotes service wording;
  it must not gain a path to this file.
- The file is populated **by the owner, directly on the box.** No part of this work asks for, receives,
  or transports the password.

## 5. The driver

A standalone script on the box, driven by its own systemd user timer, separate from the alarm.

⭐ **Placement rationale:** the established architecture is *shell/systemd owns the browser lifecycle;
the C# service observes and reports*. Driving a browser through a login is a lifecycle action, so it
belongs on the shell side. This keeps the boundary Radio Console's KIOSK-2 contract assumes, and keeps
Google credentials out of the service process. It also keeps the alarm transport-only — PR #85's
`THIS SCRIPT DETECTS NOTHING` stays true, and a bug in the actuator cannot take down the alarm that
would have reported it.

**Flow, against the bridge's existing Chrome on `:9224` — no new browser, nothing killed, same profile:**

1. Poll `/api/gvbridge/status`; act only on `browserRefreshOutcome == Stale`.
2. Check the circuit breaker (§6). If tripped, do nothing but alarm.
3. `Page.navigate` to the Google sign-in URL.
4. Fill email, submit; fill password, submit.
5. Wait for navigation to settle.
6. ⭐ **Verify by outcome:** `Page.navigate` to `https://voice.google.com/u/0/voicemail` and read where
   it actually lands. Success is *not* redirecting to `workspace.google.com/products/voice/`.
7. On success, `POST /api/gvbridge/cookies/refresh-from-browser` and confirm
   `browserRefreshOutcome` becomes `Succeeded` and `browserSessionValidatedAt` moves.

⚠ **Step 6 is the whole point.** `KNOWN-ISSUES.md:16-22` records that the tab's title and URL are
stale cached renders that lie about login state — and on 2026-09-09 both this session and Radio
Console read that tab as evidence, in opposite directions, and both were wrong. A forced navigation
is the only reading that means anything.

⚠ **Same-profile re-login is materially safer than a fresh-device sign-in.** Google already knows this
device, profile and IP. Do not "clean up" by clearing the profile or launching a fresh browser — that
converts a routine re-auth into an unrecognised-device sign-in, which is far more likely to be
challenged.

## 6. ⛔ The circuit breaker

Non-negotiable, and the acceptance criteria must prove each one by outcome.

| Rule | Rationale |
|---|---|
| **Max 1 attempt/hour, 3/day**, persisted across reboots | Bounds the sign-in rate below anything that looks like credential stuffing. |
| ⛔ **Never retry a rejected password.** One rejection → stop permanently, alarm, require human reset | Retrying a wrong password is the single most reliable way to get an account locked. A rejected credential is never transient. |
| **Distinguish three failures**, never one generic retry: *credential rejected*, *Google challenged us* (device verification / captcha / unusual-activity), *transport error* (CDP down, network) | Only the third is safely retryable. The first is terminal. The second must stop and escalate — a challenge means Google already considers this suspicious, and retrying deepens it. |
| **Breaker state is a first-class, inspectable file** | An operator must be able to see why it stopped without reading code. |
| **Tripping the breaker fires the alarm** | This is the "catastrophic problem" path in the owner's sentence. |

## 7. Escalation — reuse, do not rebuild

The alarm from PR #85 already: polls status, classifies outcomes, threads by incident, quotes service
wording, and carries a gateway heartbeat that alarms when the alarm itself stops. Auto-relogin adds
**one new condition** to it — *auto-relogin unavailable* — and otherwise changes nothing.

⚠ **Do not fold detection into the actuator or vice versa.** They are separately testable and were
separately reviewed; the value of the alarm is that it still works when the actuator does not.

## 8. ⚠ The unvalidated assumption, stated plainly

**Nothing here has been tested against Google's actual sign-in page.** The design assumes the login
form can be driven over CDP — field selectors, the two-step email-then-password flow, and what a
challenge looks like are all unknown. That assumption carries the entire design.

⛔ **Therefore the first task is a spike, not an implementation**: drive one sign-in by hand over CDP
against the live page and record what actually happens, including at least one deliberate wrong-password
attempt to capture the rejection shape. **If Google challenges a scripted sign-in from this profile,
this design does not work and the honest outcome is to say so rather than to add retries.**

⚠ Run the spike only after the cannibalisation test concludes — a sign-in during the test would
confound it.

## 9. Acceptance criteria

Each names an outcome and how it is observed. None is satisfied by "the component ran."

1. A stale session is restored end-to-end without human action, proven by `browserSessionValidatedAt`
   moving and `browserRefreshOutcome` becoming `Succeeded` — **not** by the script reporting success.
2. A deliberately wrong password causes **exactly one** attempt, a permanent stop, and an alarm.
   Verified by attempt-count in the breaker file, not by reading the code.
3. A simulated Google challenge stops and escalates without retrying.
4. A CDP transport failure retries within the rate limit and does not consume the credential budget.
5. `gv-account.conf` is absent from a built deploy archive, and a deploy does not overwrite it.
6. The password appears in no log, no journal entry, no alarm body, and no process command line —
   checked by inspecting `/proc/<pid>/cmdline` during a run.
7. Killing the relogin timer entirely still leaves the PR #85 alarm reporting staleness.

## 10. Open decisions

| # | Decision | Owner |
|---|---|---|
| 1 | Populate `gv-account.conf` on the box. No part of this work handles the password. | **Owner** |
| 2 | Ship the actuator only if the cannibalisation test shows sessions lasting well beyond hours (§3). | **Owner**, on the data |
| 3 | Whether a Google challenge should permanently disable auto-relogin or merely pause it. Default: permanently, until a human clears it. | Planning |
| 4 | Rate-limit numbers (1/hr, 3/day) are a starting point, not measured. Revisit once the real re-login frequency is known. | After §2's data |
