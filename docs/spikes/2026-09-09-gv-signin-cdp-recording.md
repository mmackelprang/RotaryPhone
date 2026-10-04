# Spike recording — one real Google sign-in, driven by hand over CDP (plan Task 4)

**Plan:** `docs/plans/gv-auto-relogin.md` Task 4 · **Spec:** §8 · **Run:** 2026-09-25 11:04–11:13 EDT on `radio`,
owner present at the box and typing every credential into the bridge Chrome window directly.

⭐ **Go/no-go (row 8): Google did not challenge either sign-in from this profile. No CAPTCHA, no device
verification, no phone prompt, no "unusual activity" interstitial appeared on the first correct sign-in, on the
wrong-password attempt, or on the restore. The design is not ruled out by this spike.** That is one observation
on one day; it is not evidence Google will never challenge, which is why the breaker's default stays TRIP (§0.6).

**Wrong-password attempts: exactly 1.**

## Where the artefacts are, and why they are not in this repo

Every DOM dump and screenshot below carries the account's email address and Google account ids, and this repo
is public. They are kept **privately**, outside git, in the owner's Rig task
`GV-SIGNIN-SPIKE_cdp_recording/artefacts/`, and cited here by filename. Selectors and text quoted below are
copied from those files with the address redacted.

Password hygiene, checked before anything was written:

- Both `type="password"` inputs in the dumps (`spike-20-password-form.html`, `spike-40-rejection.html`) carry
  `data-initial-value=""` and **no `value` attribute**.
- `spike-40-rejection.png` shows an empty field. Google clears it on rejection, and "Show password" is
  unchecked.
- The password never passed through a command. The owner typed it into Chrome; `gv-cdp.py eval` was never
  used to type.
- Box `/tmp/spike-*` and `/tmp/gv-cdp.py` are deleted. Box `~/.bash_history` was last modified at 10:37, before
  the spike, and the spike's commands ran over non-interactive ssh, which writes no history.

## Setup, as measured

| | Value |
|---|---|
| Chrome argv | `--remote-debugging-port=9224`, `--remote-allow-origins=*`, `--user-data-dir=/home/mmack/.config/gv-bridge-chrome` |
| websocket-client on box | 1.9.0 |
| Page targets | **One**: `voice.google.com/u/0/voicemail`. ⚠ The parked `workspace.google.com` tab that §0.7 expected was **not** present |
| Baseline | `browserRefreshOutcome=Succeeded`, `stale=false`, `validatedAt=2026-09-25T15:00:02Z` (`spike-00-status.json`, `spike-00-before.png`) |

## The nine rows

| # | Record | Finding | Artefact |
|---|---|---|---|
| 1 | Entry URL, and whether it redirects | `https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/` **redirects to** `/v3/signin/accountchooser?continue=…&flowName=GlifWebSignIn&flowEntry=ServiceLogin`. The profile remembers the account, so the first screen is **an account chooser, not an email form.** `accounts.google.com/Logout` lands on the same chooser | `spike-10-email-form.html/.png` |
| 2 | Email field and submit | **There is no email field on this path.** The step is a click on the remembered account: `div[role="link"][data-identifier][data-button-type="multipleChoiceIdentifier"]` (one item, `data-authuser="-1"` = signed out, `data-item-index="0"`). The password page then carries the address in a hidden `input#hiddenEmail[name="identifier"][aria-hidden="true"]`. The blank-email path ("Use another account") was **not** exercised | `spike-10-email-form.html`, `spike-20-password-form.html` |
| 3 | Two navigations or one page | **Two**: `/v3/signin/accountchooser` → `/v3/signin/challenge/pwd?TL=…&cid=1&continue=…` | `spike-20-password-form.html` |
| 4 | Password field and submit | `input[type="password"][name="Passwd"]` (`aria-label="Enter your password"`, `autocomplete="current-password webauthn"`, `aria-describedby="c0"`). Submit: `#passwordNext` | `spike-20-password-form.html` |
| 5 | Settled URL after success | `https://voice.google.com/u/0/voicemail` | `spike-30-after-submit.html/.png` |
| 6 | ⛔ Rejection shape | **The URL path does not change**: it stays on `/v3/signin/challenge/pwd`. `input[name="Passwd"]` gains `aria-invalid="true"` and is cleared. The `aria-live="polite"` region `#c0` (the input's `aria-describedby`) reads: *Wrong password. Try again or click "Forgot password?" for more options.* See the hidden-template warning below | `spike-40-rejection.html/.png` |
| 7 | Timings | See the timings table below | `spike-00-time.txt`, command timestamps |
| 8 | ⭐ Challenge? | **No challenge of any kind, on any of the three sign-in screens.** A CAPTCHA input (`#ca`, "Type the text you hear or see") and `img#captchaimg` are **present in the DOM but dormant**: 0×0, no rendered ancestor, and the image has an empty `src`. They stayed dormant after the rejection too | `spike-20-password-form.html`, `spike-40-rejection.html` |
| 9 | Did the rejection change the restore? | **No.** The owner retyped the correct password on the same rejection page and landed on `voice.google.com/u/0/voicemail` with no extra step. Verified by outcome (below) | `spike-50-restored.html/.png`, `spike-55-verify.json` |

## ⛔ Hidden templates: the classifier must test VISIBILITY, not presence

After **one** wrong password, the rejection page's DOM contains an `aria-live="assertive"` region whose text is
**"Too many failed attempts"**. It is a pre-rendered template (`display:none`, 0×0, no `offsetParent`). The
CAPTCHA input and image are the same kind of thing.

Task 10's classifier must decide on **rendered** state: `offsetParent`, a non-zero bounding box, and computed
`display`/`visibility`. It must not decide on text or element presence. A presence-based detector would read
every wrong password as a lockout and every password page as a CAPTCHA. It would also miss a real lockout if
Google only toggles visibility. Unrecognised → TRIP (§0.6) still stands as the backstop.

## Timings

All times are EDT and read from `date -Is` on the box. "Machine" rows are what Task 10's timeouts should
cover; "human" rows are owner pacing and are not timeouts.

| Step | Start | End | Elapsed | Kind |
|---|---|---|---|---|
| `Logout` navigate → chooser settled | 11:05:50 | 11:05:53 | ~3 s | machine |
| voicemail navigate → `workspace.google.com/products/voice/` (sign-out proven) | 11:05:53 | ≤11:05:58 | ≤5 s | machine |
| `ServiceLogin` navigate → chooser settled | 11:06:13 | 11:06:15 | ~2 s | machine |
| Second sign-out, `Logout` + voicemail | 11:09:07 | 11:09:14 | ~7 s for both | machine |
| `ServiceLogin` → chooser (second time) | 11:09:14 | 11:09:16 | ~2 s | machine |
| `refresh-from-browser` POST → status updated | 11:08:49 | 11:08:49 | <1 s | machine |
| Chooser → password page | 11:06:15 | 11:07:17 | ~1 min | human |
| Password page → settled voicemail | 11:07:17 | ≤11:08:28 | ≤71 s | human |

Suggested Task 10 budgets are **15 s per navigation** (3–5× the worst observed) and **30 s** for
submit-to-settled-URL. The submit-to-settled time was not isolated from typing time here, so it is a guess and
is marked as one.

## Verify by outcome (§0.8), both sign-ins

| | before | after | outcome | refresh the spike ran |
|---|---|---|---|---|
| First sign-in | `15:08:28.06Z` | `15:08:49.94Z` | `Succeeded` | `refreshed:true`, 19 cookies |
| Restore | `15:08:49.94Z` | `15:13:17.46Z` | `Succeeded` | `refreshed:true`, 21 cookies |

⚠ The first `before` (15:08:28Z) is not the baseline 15:00:02Z. Something refreshed on its own within seconds
of the sign-in completing and before the spike's own refresh. The spike's refresh still moved the timestamp,
so the acceptance check holds.

## Findings outside the nine rows

1. ⭐ **The alarm's own notify path delivered, for the first time on record.** At 15:08:02Z it sent the thread
   root and a `browser_unreachable` alert (both `notify DELIVERED http=202`). At 15:13:22Z it sent the RESOLVED
   (http 202).
2. ⛔ **The alarm filed today's alert under the 2026-09-20 incident thread.** `INCIDENT_THREAD_KEY` was
   `rotaryphone-gv-session-20260920T071522Z`, left over from the five-hour 09-20 incident. That incident
   recovered without ever delivering, so the key was never cleared, and today's alert "re-attempted the
   incident thread root" for a five-day-old subject. **This is a defect in `gv-session-alarm.sh`.** An incident
   that closes undelivered must retire its thread key.
3. ⛔ **The alarm labelled a signed-out session `browser_unreachable`.** At 15:08:02Z the only page target was
   on Google's password page (`accounts.google.com/v3/signin/challenge/pwd`). The service reported
   `outcome=Unreachable`, not stale. **Tasks 9–11 must not assume a sign-out always surfaces as `Stale`.**
   While the browser sits on an `accounts.google.com` page, and in particular after a half-finished automated
   sign-in, the signal is `Unreachable`. The alarm's ACTION text for that condition then points the owner at
   the wrong fix.
4. No alarm condition fired for the signed-out windows themselves (11:05:53–11:08:28 and 11:09:14–11:13). Each
   was shorter than the alarm's debounce.
