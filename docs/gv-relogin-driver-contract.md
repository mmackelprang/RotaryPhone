# GV auto-relogin — the sign-in driver contract

**For:** the owner, who writes the sign-in driver.
**Date:** 2026-09-25 · **Branch:** `feat/gv-auto-relogin` (PR #88)
**Enforced by:** `deploy/gv-auto-relogin.sh` (the actuator), `deploy/tests/repro-gv-relogin.sh` (its harness),
and `deploy/tests/check-relogin-driver.sh` (a checker you run against your driver).

---

## 1. Who writes what

| Component | File | Written by | What it does |
|---|---|---|---|
| **Sign-in driver** | `deploy/gv-relogin-signin.py` | **the owner** | Drives one Google sign-in in the bridge's Chrome and reports one verdict word |
| Actuator | `deploy/gv-auto-relogin.sh` | this branch | Decides whether an attempt is allowed, hands the driver its inputs, reads the verdict, then checks the result independently |
| Circuit breaker | `deploy/gv-auto-relogin-breaker.sh` | this branch | Bounds attempts to 1 per hour and 3 per day. A rejection, a challenge or anything unrecognised stops it until a human runs `--reset` |
| CDP helper | `deploy/gv-cdp.py` | this branch | Lists pages, reads a page's own location, and navigates. The actuator uses it to choose the page and to verify the result |

The driver is the only component that types into Google's sign-in page. **Nothing on this branch does.** Until
`deploy/gv-relogin-signin.py` exists, every actuator cycle logs *"auto-relogin not installed"* and exits without
touching the breaker, the browser or the credential file. That is the safe resting state, and it raises no
alarm.

---

## 2. Where the file goes

| Stage | Path | Put there by |
|---|---|---|
| Repo | `deploy/gv-relogin-signin.py` | you (commit it) |
| Shipped | `/opt/rotary-phone/deploy/gv-relogin-signin.py` | `Deploy-ToLinux.ps1`. It ships `deploy/*.py` as well as `deploy/*.sh` since this branch. There is no `-Recurse`, so the file must be directly in `deploy/` |
| Installed | `~/bin/gv-relogin-signin.py`, mode 755 | `install-gv-auto-relogin.sh`, which the deploy runs |
| Checked | `check-installed-drift.sh --group relogin` | the deploy. The driver is an optional file: absent everywhere is reported and not failed, and present anywhere is checked repo → shipped → installed |

The actuator runs it as **`python3 ~/bin/gv-relogin-signin.py`**. The shebang and the executable bit are not
used. Runtime on the box: `python3` with `websocket-client` 1.9.0 (measured 2026-09-09). There is no node and
no Playwright, and nothing may be installed on this shared box (plan §0.12).

---

## 3. When it runs

The actuator starts the driver only after **all** of these hold, in this order:

1. The driver file exists.
2. The actuator holds the breaker lock (`~/.local/state/gv-auto-relogin.lock`).
3. The breaker is `ARMED`, and its verdict is exactly `AUTHORISED`. That covers the rate limits, a clock
   running backwards, and a corrupt state file.
4. `GET /api/gvbridge/status` returns `browserRefreshOutcome` equal to **`Stale` or `SignedOut`**.
5. The reauth assist (PR #89) is idle. Its state file must be absent or say exactly `STATE=IDLE`. Any other
   state, including `PREPARED` and `SIGNED_IN_UNCONFIRMED` (a human is mid sign-in), makes the actuator stand
   down. Standing down is not a trip and spends nothing.
6. `/opt/rotary-phone/gv-account.conf` is valid (see §4).
7. Exactly one page in the bridge's Chrome is a candidate, judged by its **live** `window.location.href` with
   the host parsed:
   - First choice: a page on `voice.google.com` or `accounts.google.com`.
   - Otherwise: a lone page on `workspace.google.com/products/voice…`.
   - **No** candidate (for example Chrome's own error page during a network blip) counts as a transport fault:
     no trip, no credential spent, and the hourly spacing applies.
   - **Two or more** candidates stop the breaker with reason `target_unrecognised`, and no attempt is made.
8. The attempt has already been **charged and written to disk** with `BREAKER_LAST_OUTCOME=in_flight`. If
   the actuator is killed while your driver runs, the next run finds that marker and stops the breaker
   (`interrupted`). It never assumes the attempt went well.

---

## 4. Inputs

### 4.1 The credential file — yours to create, on the box, by hand

`/opt/rotary-phone/gv-account.conf`: **mode 600, owned by `mmack`, a regular file (not a symlink), LF line
endings.**

```
GV_ACCOUNT_EMAIL=<the dedicated GV account's address>
GV_ACCOUNT_PASSWORD=<its password>
```

- The value is **everything after the first `=`, verbatim.** No quotes are removed and no whitespace is
  trimmed. A value may contain `=`, spaces, quotes, `$` and backslashes.
- Blank lines and lines starting with `#` are ignored. Any other key, a duplicated key, a missing or empty
  value, or a carriage return stops the breaker **without an attempt**. A CR would become part of the
  password, Google would reject it, and a rejection stops auto-relogin permanently. Error messages give line
  numbers and never repeat an unrecognised key, because such a key could be a mistyped password.
- The file is never sourced, never exported and never passed in argv. Both deploy branches exclude it
  (plan Tasks 7–8), so a deploy does not overwrite or delete it.

⚠ **Your driver must not read this file.** It gets the credential on stdin.

### 4.2 stdin — the only input

Five `key=value` lines, LF-terminated, in this order, then end-of-file:

```
version=1
cdp_port=9224
target_id=<the CDP target id of the ONE page to drive>
email=<GV_ACCOUNT_EMAIL, verbatim>
password=<GV_ACCOUNT_PASSWORD, verbatim>
```

- Take each value as **everything after the first `=`** on its line. No value contains CR or LF; the actuator
  refuses a credential file that would allow one.
- **Read stdin to EOF, then act.** If `version` is not `1`, or any of the five keys is missing, print
  `UNRECOGNISED` and exit 0.
- `cdp_port` is `9224` on the box. The harness uses other ports, so read it rather than assuming it.
- **`target_id` is the only page you may touch.** The actuator chose it from its live location (§3.7), and it
  navigates that same page afterwards to check the result.

### 4.3 What your process gets, and does not get

| Channel | Contents |
|---|---|
| argv | `python3 <path>` and nothing else |
| environment | the systemd user environment. It **never** carries the credential, which is not exported at any point |
| inherited fds | stdin, stdout, stderr. The breaker lock (fd 8) is closed for you, so a helper you leave behind cannot hold the lock |
| cwd | unspecified; do not rely on it |

⛔ **Do not put the password or the email into argv, the environment, a file, or any subprocess.** On this box
`/proc/<pid>/cmdline` is world-readable, and Radio Console runs under the same uid (plan §0.3). The harness
checks the argv and environment of your process and of every ancestor **while the driver runs**.

---

## 5. Output

### 5.1 The verdict — the last line of stdout, and exit 0

The **last line of stdout** must be exactly one of these words, and the exit status must be **0**:

| Word | Legal when | What the actuator does |
|---|---|---|
| `SIGNED_IN` | the target page has settled on `voice.google.com` (spike row 5: `https://voice.google.com/u/0/voicemail`) | **checks the result itself** (§6). Your word is a claim, not the outcome |
| `CREDENTIAL_REJECTED` | Google's **visible** response to the submitted password is the wrong-password state (spike row 6) | stops the breaker, **permanently** (`credential_rejected`) |
| `CHALLENGED` | Google **visibly** shows anything other than a password rejection: CAPTCHA, device or phone verification, "unusual activity", **visible** "Too many failed attempts", a 2-step prompt | stops the breaker, **permanently** (`challenged`) |
| `TRANSPORT` | ⛔ **only** for a fault **before your first interaction with Google's page**: the CDP port refuses the connection, `target_id` is not among the page targets, or the websocket closes before you send your first command to the target | hands the credential attempt back (spec §9.4), counts a transport failure, and keeps the hourly spacing. A later timer tick may try again |
| `UNRECOGNISED` | **anything else.** This is the default, and it includes every fault **after** your first interaction with the page | stops the breaker, **permanently** (`unclassified`) |

The actuator also treats all of the following as **`UNRECOGNISED`**, which stops the breaker:

- a non-zero exit, **even if the last line is a valid word**
- a crash or traceback
- no output
- a last line that is not exactly one of the five words (surrounding whitespace counts)
- being killed at the actuator's **120 s** limit
- a driver file that disappears between the check and the launch

⛔ **Why `TRANSPORT` is so narrow.** It is the only verdict that lets the automation try again. Once your
driver has sent anything to Google's page, Google may have seen a sign-in, and "try again in an hour" becomes
a second password attempt the breaker cannot see. If you are unsure whether a fault came before or after that
point, it came after: print `UNRECOGNISED`. Plan §0.6: *"an unrecognised outcome is not evidence that trying
again is safe."*

### 5.2 Other stdout lines

You may print other lines before the verdict. The actuator **never logs stdout**. It reads only the last line.
Do not print the password there, and preferably not the email either.

### 5.3 stderr — diagnostics, redacted as a second line of defence

stderr goes to the journal (`journalctl --user -u gv-auto-relogin`) through a redactor. The redactor replaces
the exact password and email with markers, cuts each line to 300 characters, and keeps the first 40 lines.
⚠ **This is not permission to print either value.** It cannot catch a fragment of the password, such as
"first four characters for debugging", or an encoded form of it. Log **which state you observed**, not what
you typed.

---

## 6. What happens after `SIGNED_IN`

The actuator does not take your word for it. On the **same `target_id`** it:

1. Navigates to `https://voice.google.com/u/0/voicemail` and reads that page's own `window.location.href`
   after the load event. It checks the page's own location, never the target list's cached URL
   (`KNOWN-ISSUES.md:16-22`, plan §0.7). A host other than `voice.google.com` stops the breaker
   (`verification_failed`), and no cookies are posted.
2. Reads `browserSessionValidatedAt`, then `POST /api/gvbridge/cookies/refresh-from-browser`.
3. Counts the sign-in as restored **only if** the POST returned **200**, `browserRefreshOutcome` is
   `Succeeded`, **and** `browserSessionValidatedAt` moved across that POST. A 202 (written but not proven), a
   502 (Google refused the cookies), or an unmoved timestamp, which would be the 20-minute cron's success
   rather than ours, all stop the breaker.

⛔ So your driver must **not** POST `refresh-from-browser` itself, and must not leave the target on a page other
than where the sign-in settled.

---

## 7. Constraints — each exists because breaking it risks the account

1. **Same Chrome, same profile.** Connect only to `127.0.0.1:<cdp_port>` and drive only `target_id`. Never
   start, restart or close a browser. Never open or close targets. Never clear cookies, storage, cache or the
   profile, so no `Network.clearBrowserCookies` and no `Storage.clear*`. Spec §5: Google already knows this
   device, profile and IP. A fresh profile turns a routine re-auth into an unrecognised-device sign-in, which
   is far more likely to be challenged.
2. **One attempt per run, and no retries inside the driver.** Submit the password **at most once**. Do not
   re-navigate, re-submit or re-type after the submit, and do not loop over "try again". A retry inside the
   driver is one the breaker cannot see or count. The breaker is the only thing that decides when to try again.
3. **Classify on what is VISIBLE, never on what is present.** ⛔ The spike found Google **pre-renders hidden
   templates**:
   - After **one** wrong password, the rejection page's DOM already holds an `aria-live="assertive"` region
     reading **"Too many failed attempts"**. It is `display:none`, 0×0, with no `offsetParent`
     (spike, *"Hidden templates: the classifier must test VISIBILITY, not presence"*).
   - A CAPTCHA input (`#ca`, "Type the text you hear or see") and `img#captchaimg` are **in the DOM on every
     password page but dormant**: 0×0, no rendered ancestor, and an empty `src` (spike row 8).

   A classifier that checks presence would read every wrong password as a lockout and every password page as
   a CAPTCHA. It would also **miss** a real lockout if Google only toggles visibility. Decide on rendered
   state: `offsetParent`, a non-zero bounding box, and the computed `display` and `visibility`.
4. **Unrecognised is the default.** A state you did not positively identify is `UNRECOGNISED`. It is never
   `TRANSPORT` and never "wait and see".
5. **Stay within the time budget** (spike *"Timings"*):
   - **15 s per navigation**, which is 3–5× the worst observed (~3–5 s).
   - **30 s from submit to settled URL.** ⚠ The spike marks this as a guess, because typing time was not
     isolated from settle time.

   A step that exceeds its budget ends the run with `UNRECOGNISED`, not another wait. The whole run is killed
   at 120 s.
6. **Secrets.** Take the password only from stdin, and keep it in memory. Never write it to disk, never pass it
   to a subprocess (use none if you can), never print it, never put it in an exception message, and never
   `repr()` the parsed stdin.
7. **Stay inside your job.** Do not read `gv-account.conf`. Do not touch the breaker state
   (`~/.local/state/gv-auto-relogin.state`), its lock, or the reauth assist's state. Do not call the
   RotaryPhone service. The actuator owns all of those.

---

## 8. The page, as the spike recorded it

Source: `docs/spikes/2026-09-09-gv-signin-cdp-recording.md`, run 2026-09-25 11:04–11:13 EDT on `radio` with the
owner at the box. That was **one** observation on one day. The artefacts are private, in the owner's Rig task
`GV-SIGNIN-SPIKE_cdp_recording/artefacts/`, and are cited by filename. **These are facts about the page, not a
procedure.**

| Spike row | What was observed | Artefact |
|---|---|---|
| 1 | `https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/` **redirects to** `/v3/signin/accountchooser?…&flowName=GlifWebSignIn&flowEntry=ServiceLogin`. The profile remembers the account, so the first screen is an **account chooser**. `accounts.google.com/Logout` lands on the same chooser | `spike-10-email-form.html/.png` |
| 2 | **No email field on this path.** The remembered account is `div[role="link"][data-identifier][data-button-type="multipleChoiceIdentifier"]`: one item, `data-authuser="-1"` (signed out), `data-item-index="0"`. The password page carries the address in a hidden `input#hiddenEmail[name="identifier"][aria-hidden="true"]`. ⚠ The blank-email path ("Use another account") was **not exercised** | `spike-10-email-form.html`, `spike-20-password-form.html` |
| 3 | **Two navigations**: `/v3/signin/accountchooser` → `/v3/signin/challenge/pwd?TL=…&cid=1&continue=…` | `spike-20-password-form.html` |
| 4 | Password field `input[type="password"][name="Passwd"]` (`aria-label="Enter your password"`, `autocomplete="current-password webauthn"`, `aria-describedby="c0"`). Submit: `#passwordNext` | `spike-20-password-form.html` |
| 5 | Settled URL after success: `https://voice.google.com/u/0/voicemail` | `spike-30-after-submit.html/.png` |
| 6 | **Rejection:** the URL path **does not change**; it stays on `/v3/signin/challenge/pwd`. `input[name="Passwd"]` gains `aria-invalid="true"` and is cleared. The `aria-live="polite"` region `#c0`, which is the input's `aria-describedby`, reads *Wrong password. Try again or click "Forgot password?" for more options.* | `spike-40-rejection.html/.png` |
| 8 | **No challenge** on any of the three sign-in screens. The CAPTCHA elements were present but dormant, and stayed dormant after the rejection (§7.3) | `spike-20-password-form.html`, `spike-40-rejection.html` |
| 9 | After the rejection, retyping the correct password on the same page restored the session with no extra step. ⛔ **This does not license a retry inside the driver** (§7.2). It shows that a human's recovery works | `spike-50-restored.html/.png`, `spike-55-verify.json` |
| finding 3 | While Chrome sits on an `accounts.google.com` page the service now reports **`SignedOut`** (PR #90). Before PR #90 it reported `Unreachable`. The actuator acts on `SignedOut` | — |

⚠ **What the spike could not tell you:**

- What a second rejection looks like. The breaker guarantees there is never a second one (plan §0.5).
- What a real challenge looks like.
- What the email-entry path looks like.
- Whether the chooser always appears.

Each of these is a state your driver will not recognise, and §5.1 says what to print for it.

---

## 9. Checklist

### 9.1 Verified by the actuator harness, with a stub in place of your driver

`deploy/tests/repro-gv-relogin.sh`: 115 cases and 26 mutants. It needs no browser and no Google. Each item
below is a named case, and each ⛔ item also has a mutant that must be caught.

- [x] Your driver is started **only** on `Stale`/`SignedOut`, only with the breaker `AUTHORISED`, and only
  while the reauth assist is absent or exactly `IDLE` ⛔
- [x] stdin is **exactly** the five lines of §4.2, with values verbatim, including `=`, spaces, `"`, `\` and `$` ⛔
- [x] The password and the email are in **no argv and no environment**, for your process and every ancestor,
  read while your driver runs ⛔
- [x] Password printed to stderr by the driver → the journal shows `[password redacted]`, never the password ⛔
- [x] The password and every 4-character substring of it are absent from every journal and state file, and
  from `--status` and `--print-config` ⛔
- [x] `CREDENTIAL_REJECTED` → exactly **one** attempt, `TRIPPED`, and a second run makes none ⛔
- [x] `CHALLENGED` → `TRIPPED`, and a second run makes none
- [x] `TRANSPORT` → credential budget handed back, the transport counter goes up, and the hourly spacing
  still applies ⛔
- [x] Any other last line, silence, a crash, a non-zero exit (even with a valid word), or the 120 s limit →
  `TRIPPED` ⛔
- [x] `SIGNED_IN` is **checked**: a redirect to the signed-out page means no POST and `TRIPPED`; an unmoved
  `validatedAt`, a 202 or a 502 means `TRIPPED` ⛔
- [x] The attempt is on disk **before** your driver starts; a run killed mid-driver stops the breaker on the
  next run ⛔
- [x] Two runs at once: the second does nothing ⛔. A helper process your driver leaves behind does not hold
  the lock or stall the run ⛔
- [x] 1 attempt per hour and 3 per day hold

### 9.2 Checked against YOUR driver: `bash deploy/tests/check-relogin-driver.sh`

⛔ **Never run it on `radio`.** Run it on Linux (WSL, or the harness container) once `deploy/gv-relogin-signin.py`
exists. Where `unshare -rn` works (WSL), every driver run gets its own network namespace, so it can reach nothing. Where
it does not (a Docker container), the checker **refuses to run** if the machine looks like the box: hostname
`radio`, `~/.config/gv-bridge-chrome`, or anything listening on `127.0.0.1:9224`. A draft driver that ignored
`cdp_port` could otherwise submit the fixture password to the real account. It reports a loud
SKIP while the file is absent. It **never contacts Google**: the only port it gives your driver is one where
nothing listens. It checks that:

- [ ] the file compiles
- [ ] it reads the credential from `sys.stdin`, and declares no argv option for a secret
- [ ] it names no subprocess, shell, browser launch, profile path, cookie or storage clearing, or target
  create/close, and it does not read `gv-account.conf` or call `refresh-from-browser`
- [ ] **stdin with `version=2`** → last line `UNRECOGNISED`, exit 0
- [ ] **empty stdin** → last line `UNRECOGNISED`, exit 0
- [ ] **a CDP port where nothing listens** → last line `TRANSPORT`, exit 0, within 15 s. The connection is
  refused before any page is touched, which is the one case where `TRANSPORT` is legal
- [ ] in all three runs, the fixture password appears in **neither stdout nor stderr**

### 9.3 Only you, and the attended runs, can verify

- [ ] Rejection and challenge detection test **visibility**, not presence (§7.3). Review this against the
  spike's hidden templates.
- [ ] The password is submitted **at most once** per run, and nothing is retried after the submit (§7.2).
- [ ] Every path past the first page interaction ends in `SIGNED_IN`, `CREDENTIAL_REJECTED`, `CHALLENGED`
  or `UNRECOGNISED`, **never `TRANSPORT`**.
- [ ] Plan Task 17a: one deliberate wrong password → `credential_rejected`, and `credential today` reads
  `1/3`, not `2/3`.
- [ ] Plan Task 17c: one real restore → `validatedAt` moved from the recorded `BEFORE`, with no human touching
  the browser.
