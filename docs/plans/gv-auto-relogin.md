# Plan — GV auto-relogin: automate the routine case, and stop hard when it stops being routine

**Spec:** [`../superpowers/specs/2026-09-09-gv-auto-relogin-design.md`](../superpowers/specs/2026-09-09-gv-auto-relogin-design.md) —
read it first. Its §2 prerequisites (no 2FA, dedicated account) and its §5 placement rationale are settled and
are **not** re-opened here.
**Depends on:** the GV session alarm ([`gv-session-alarm.md`](gv-session-alarm.md), merged as PR #85) and the
cannibalisation falsification test started 2026-09-09 17:33 EDT.
**Date:** 2026-09-09. **Status:** planned, not started.
**Branch:** `feat/gv-auto-relogin`.

> **There is no work queue in this repo.** `docs/BUILDER_QUEUE.md` and `docs/ROADMAP.md` do not exist and are
> not being created. This plan file **is** the handoff artefact: whoever builds this executes the tasks below
> in order. Nothing needs to be added anywhere else.

⛔ **THE CIRCUIT BREAKER IS THE FEATURE.** Spec §3 and §6. A version of this that logs in reliably but retries
freely is **worse than no automation at all**, because the failure it produces — a locked Google account — is
the catastrophic event this work exists to prevent, arriving with the phone down and no fallback. The breaker
is therefore built **first** (Phase 2), before a single line of login code exists, tested hardest, and its
acceptance checks are the most adversarial in this document.

⛔ **TASK 4 IS A SPIKE, NOT AN IMPLEMENTATION.** Spec §8. Nothing here has been tested against Google's actual
sign-in page. Selectors, the two-step email→password flow, and the shape of a challenge are all unknown, and
that assumption carries the entire design. **The honest outcome of the spike may be "this design does not
work."** Phase 2 onward exists only if the spike says it can.

---

## 0. What changed between the spec and this plan

**Twelve findings.** Six contradict the spec or correct an instrument it prescribes; three are measurements the
spec assumes without having taken; three are confirmations worth having. They are stated first because the
phase structure below is shaped by them, and because two of them are hard gates the spec does not name.

Everything measured below was measured on `radio` on **2026-09-09 between 19:30 and 19:40 EDT**, read-only, in
a session separate from the one monitoring the cannibalisation test.

---

### 0.1 ⛔ THE ALARM IS MERGED BUT NOT INSTALLED, AND THERE IS NO TOKEN — the escalation path this design rests on does not exist on the box today

This is the most consequential finding in the document and it changes what may ship.

```
$ ls -l ~/bin/gv-session-alarm.sh
ls: cannot access '/home/mmack/bin/gv-session-alarm.sh': No such file or directory

$ systemctl --user list-unit-files 'gv-session-alarm.*'
UNIT FILE STATE PRESET
0 unit files listed.

$ ls -l ~/.rotaryphone-env
ls: cannot access '/home/mmack/.rotaryphone-env': No such file or directory
```

PR #85 merged at **21:32Z today**. No deploy has run since. So:

| Spec §1 says | On the box |
|---|---|
| *"the alarm shipped in PR #85 becomes the escalation path"* | the alarm is **not installed** |
| *"Most of the escalation half already exists and is tested"* | true of the **repo**; false of the **box** |
| §6: *"Tripping the breaker fires the alarm"* | there is nothing to fire, and no token to fire it with |

⛔ **The entire safety case of this design is "the breaker trips → the alarm reaches a human." Today the
breaker would trip into silence.** An actuator shipped onto this box in its current state is an automation
whose only failure path is one nobody can hear — which is a strictly worse posture than the manual re-login it
replaces, because the manual process at least has an owner attached to it.

⭐ **This is instance 11 of the boundary doc's class**, and it is the third link of *merged ≠ deployed ≠
INSTALLED* (`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md:663`) caught in the act, one day after the
document that catalogues it was written. It is not this plan's defect to fix — `gv-session-alarm.md` Tasks 5,
16 and 17 are unstarted and own it — but it **is** this plan's gate. See Task 2.

---

### 0.2 ⛔ THE SPEC'S DEPLOY EXCLUSION IS HALF A FIX — the rsync path DELETES, it does not overwrite

Spec §4: *"Excluded from the deploy archive, exactly as `appsettings.Production.json` is."* There is no single
exclusion to copy. There are **two, in two different branches, defending against two different verbs.**

```powershell
# Deploy-ToLinux.ps1:126-131 — the RSYNC branch
rsync -az --delete `
  --exclude 'appsettings.Production.json' `
  --exclude 'data/' `
  --exclude 'logs/' `
```

```powershell
# Deploy-ToLinux.ps1:247 — the TAR branch
" tar --null --exclude=./appsettings.Production.json -czf - -T - |" +
```

| Branch | The exclusion prevents | If `gv-account.conf` is unexcluded |
|---|---|---|
| **tar** | an **overwrite** — the member is never in the stream | nothing; the archive has no such member, so the file survives |
| **rsync** | a **deletion** — `--delete` removes every destination file not in the source | ⛔ **the credential file is deleted**, silently, on the next deploy |

⚠ **And the deleting branch is imminent, not theoretical.** `gv-session-alarm.md` §0.10 recorded that rsync has
never been on the workstation's `PATH`, so the tar branch is the one that has always run — and the owner is
installing rsync. Measured on the box: `rsync` is **present** at `/usr/bin/rsync`, so the remote half is ready
the moment the local half appears.

⛔ **The proof that this is real, and it is already sitting there:**

```
$ ls -l /opt/rotary-phone/refresh-gv-cookies.sh
-rwxrwxr-x 1 mmack mmack 587 May 25 20:56 /opt/rotary-phone/refresh-gv-cookies.sh
$ crontab -l | grep -v '^#'
*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh
```

The **load-bearing 20-minute cron's own script** lives in the deploy target, is not in the publish output, and
is not excluded. The first rsync deploy deletes it. So does every `.bak` file in that directory. 📌 **Recorded,
not scoped** — it is pre-existing and belongs to whoever fixes the deploy — but it is the demonstration that
"a file in `/opt/rotary-phone` that the deploy does not ship" is a **deletion target**, and the credential file
would be one of them.

**Consequence for this plan:** Task 7 adds **both** exclusions, and Task 8's test proves **both** — one case per
branch. A test that only inspects the tar member list would pass while the rsync branch eats the file.

---

### 0.3 ⛔ SPEC ACCEPTANCE 6 PRESCRIBES AN INSTRUMENT THIS REPO MEASURED AS UNSOUND — hours before the spec was written

Spec §9 acceptance 6: *"The password appears in no log, no journal entry, no alarm body, and no process command
line — **checked by inspecting `/proc/<pid>/cmdline` during a run**."*

The alarm shipped that same afternoon already contains the refutation, at
`deploy/tests/repro-gv-session-alarm.sh:224-227`:

> ⚠ Asserted against the SOURCE, because the call is far too short-lived to catch by sampling `/proc` — **a
> sampling test here would pass by missing it, which is worse than no test at all.**

A sampler that never happens to catch the process mid-call returns a green light **by failing to look**. That
is the boundary doc's first neighbour — a check that cannot fail — dressed as diligence.

**Measured, because the replacement rests on it rather than on memory:**

```
$ P=$(pgrep -f 'remote-debugging-port=9224' | head -1)
$ ls -l /proc/$P/cmdline /proc/$P/environ
-r--r--r-- 1 mmack mmack 0 Sep  9 11:38 /proc/155098/cmdline     <- 0444, WORLD-readable
-r-------- 1 mmack mmack 0 Sep  9 11:23 /proc/155098/environ     <- 0400, owner only

$ ps -o user= -p $(pgrep -f 'radio-kiosk-chrome' | head -1)
mmack                                                            <- SAME UID as us
```

| Channel | Exposure on this box |
|---|---|
| **argv** | every uid: `beszel` (a metrics agent that collects process data), `avahi`, `colord`, `polkitd`, `root` | ⛔ never |
| **environ** | owner-only — so **not** a boundary against Radio Console, which runs as `mmack` too; **is** a boundary against every other uid | acceptable |
| **stdin / socket** | the process's own file descriptors | ⭐ correct |

⭐ **So the ordering is stdin > environ > argv, and the spec's "source the file into the environment, **or** feed
it on stdin" is not a free choice — stdin is strictly better and it is available.** The alarm already
established the pattern: it feeds curl's bearer header through `curl --config -` on stdin
(`repro-gv-session-alarm.sh:229-231` asserts it). Task 9 does the same for the password, and Task 12's check is
a **source assertion**, not a sampler.

---

### 0.4 ⛔ THE SPIKE NEEDS A SIGNED-OUT SESSION, AND MANUFACTURING ONE IS DESTRUCTIVE — the spec does not say this

You cannot drive a sign-in against a session that is signed in. Right now the box's session is **healthy**:
`browserRefreshOutcome` is not `Stale`, and the cannibalisation log shows `stale=false` across every sample.

Three ways to obtain the state the spike needs. Only one is honest:

| Route | Verdict |
|---|---|
| **(a) wait for a natural sign-out** | ⛔ unbounded. The cannibalisation test exists precisely because sign-outs may have *stopped*. If it succeeds, there may be no natural sign-out for months, and the spike never runs |
| **(b) sign out deliberately, attended, owner present** | ✅ **the plan's choice** |
| **(c) a fresh throwaway profile** | ⛔ forbidden by spec §5 — *and useless as evidence.* A fresh profile is far **more** likely to be challenged, so it answers a different question than the one being asked. It would tell us the design fails when it does not, or worse, would not |

**The blast radius of (b), stated so it is a decision and not a surprise:**

- The **phone keeps working.** The service mints its own `__Secure-1PSIDTS` every 8 minutes and runs on a
  lineage independent of Chrome's (`docs/handoffs/2026-09-08-rotaryphone-auth-lineage-fixes.md:89-93`).
- The 20-minute cron's `refresh-from-browser` **cannot downgrade the good set.** The 2026-09-08 hardening
  validates a candidate against Google before adopting and, on refusal, logs *"REJECTED a cookie set … The
  working on-disk set was NOT overwritten"* and returns without touching it (`KNOWN-ISSUES.md`, ✅ IMPLEMENTED
  2026-09-08).
- **The fallback is what happens today anyway:** the owner re-logs in by hand at `voice.google.com`, which is a
  two-minute action they have already performed twice this month.
- ⚠ **What is genuinely lost if the spike fails:** the re-derivation floor, until the owner performs that
  re-login. That is a real cost and it is why the spike is **attended** — the owner is at the box, not notified
  afterwards.

---

### 0.5 ⛔ ORDER THE SPIKE'S TWO SIGN-INS: CORRECT FIRST, WRONG LAST

Spec §8 asks for *"at least one deliberate wrong-password attempt to capture the rejection shape."* Correct, and
the **order is load-bearing in a way the spec does not state.**

A credential rejection can itself raise the account's risk posture. A wrong-password attempt performed *first*
therefore risks measuring a **post-rejection** sign-in and recording it as the baseline — learning the shape of
a challenged flow and encoding it as the normal one. The correct sequence is:

1. **Correct password.** Record the whole flow: selectors, step boundaries, timings, the settled URL.
2. **Sign out again.**
3. **Exactly one wrong password.** Record the rejection shape.
4. **Correct password.** Restore the session, and record whether step 3 changed anything about step 4.

⚠ **And the honest limit of what the spike can ever produce, stated because it shapes the classifier.** One
wrong-password attempt tells us what **one** rejection looks like. It cannot tell us what the second looks
like, and **we will never find out** — because the breaker guarantees there is never a second one. The rejection
detector is therefore built on a sample of size one, permanently. That is the design working as intended, and it
is exactly why §0.6's default matters more than the pattern itself.

---

### 0.6 ⛔ THE DEFAULT FOR AN UNRECOGNISED OUTCOME IS *TRIP*, NOT *RETRY* — this is the single most important line in the design

It follows directly from §0.5. If the classifier is built on one sample of a rejection, then the case it will
most often meet in the wild is **an outcome it does not recognise**.

| If unknown maps to | What happens the first time Google changes a string |
|---|---|
| `transport` (retryable) | ⛔ the actuator retries a rejected credential, on a schedule, until the account locks. **This is the catastrophic path, reached by a one-line default.** |
| `challenged` (terminal) | the actuator stops, alarms, and a human looks. Cost: one manual re-login, which is the status quo |

⛔ **Therefore: the classifier's `*)` branch is `challenged`. Not `transport`. Not "retry once to be sure."**
`transport` must be reached only by a *positively identified* transport failure — the CDP websocket refused,
the target vanished, the navigation never produced a load event. Everything else, including silence, is
terminal.

---

### 0.7 ⚠ THE PARKED `workspace.google.com` TAB IS ALREADY IN THE TARGET LIST WHILE THE SESSION IS HEALTHY

Measured now, with the session demonstrably healthy:

```
$ curl -s http://127.0.0.1:9224/json/list | jq -r '.[] | "\(.type)\t\(.url)"'
page    https://voice.google.com/u/0/voicemail                    <- title "Voice - (99+) Voicemail"
iframe  about:srcdoc
iframe  https://accounts.google.com/RotateCookiesPage?...
page    https://workspace.google.com/products/voice/              <- ⚠ PARKED, and the session is FINE
...
```

⛔ **So "is there a `workspace.google.com` target?" answers `yes` on a healthy session.** Any verification that
scans the target list is a check that runs, passes, and answers a different question — the boundary doc's fourth
neighbour, and the exact trap `KNOWN-ISSUES.md:16-22` describes: *"both stale cached renders."*

Spec §5 step 6 is right in substance and must be implemented precisely:

- navigate **one target, addressed by its own `targetId`**, and
- read **that target's own** resulting location, via `Runtime.evaluate: window.location.href` **after** the load
  event fires,
- **never** from `/json/list`'s cached `.url`, and **never** by asking whether some tab somewhere matches.

⭐ This is the second time in two days that both this repo and Radio Console have misread that tab, in opposite
directions. The instrument, not the reader, is what has to change.

---

### 0.8 ⭐ THE AUTHORITY ON "DID THE LOGIN WORK" IS NOT THE URL — it is Google, via `refresh-from-browser`

Spec §5 calls step 6 (the forced navigation) *"the whole point"* and step 7 (the cookie refresh) a follow-up.
**In strength it is the other way round**, and saying so changes the acceptance criteria:

| Step | What it actually asks | Authority |
|---|---|---|
| 6 — forced navigation | *does this profile still get served the app shell?* | a **gate**: cheap, local, and enough to decide whether step 7 is worth attempting |
| 7 — `POST /api/gvbridge/cookies/refresh-from-browser` | *does **Google** accept cookies harvested from this profile?* | ⭐ **the outcome.** `GVApiAdapter.TryValidateCandidateAsync` adopts in memory, probes live against Google, and persists only on success |

⭐ **And this is what makes a step-6 false positive SAFE, which is worth knowing before building on it.** Since
the 2026-09-08 hardening, a rejected candidate leaves the good set intact. So the cost of believing we logged in
when we did not is **one `[ERR] REJECTED` line and an unchanged `Stale`** — not a destroyed credential. The
2026-08-01 downgrade path is closed. Auto-relogin is being built on top of a `refresh-from-browser` that can no
longer hurt us; it would have been unsafe to build this six weeks ago.

⚠ **Consequence for acceptance, and it is not a small one:** `browserRefreshOutcome == Succeeded` **on its own
does not prove we did it.** The 20-minute cron produces `Succeeded` too, on its own schedule, and may land
between our poll and our check. Every acceptance below binds to `browserSessionValidatedAt` **moving across our
own POST**, with the pre-POST value recorded. See Task 11.

---

### 0.9 ⛔ THE ALARM'S NEW CONDITION MUST NOT SHARE THE SESSION CONDITION'S TRACK — or the alarm goes mute in the state it exists for

Spec §7: *"Auto-relogin adds **one new condition** to it — auto-relogin unavailable — and otherwise changes
nothing."* Right, and the placement is not free.

The alarm posts on transition of a **single** `LAST_POSTED_CONDITION` (`gv-session-alarm.sh:470`). Put
`relogin_unavailable` into that same `case` and this happens:

```
t0   breaker trips           condition=relogin_unavailable   POSTED
t1   session dies (Stale)    condition=relogin_unavailable   <- breaker still tripped, wins the case
t2   ...                     condition=relogin_unavailable   <- nothing posted, ever
```

⛔ **A genuine session death produces no message, because the condition string never changed.** The alarm goes
silent in exactly the state it was built for — and it does so *because* the automation broke, which is the
worst possible correlation.

**Therefore: a second, independent track.** `LAST_POSTED_RELOGIN_STATE`, its own dedupe key, posting into the
open incident thread when there is one and opening its own when there is not. PR #85's session track is
**byte-unchanged**, which is also the most literal reading of *"adds one condition and otherwise changes
nothing."* Task 14's harness asserts the two tracks do not interfere.

---

### 0.10 ⚠ THE ALARM READING THE BREAKER FILE IS STILL TRANSPORT — but only if the breaker file carries the words

`THIS SCRIPT DETECTS NOTHING` (`gv-session-alarm.sh:6`) has to stay true, and spec §5 is explicit that the alarm
must not gain detection logic. Reading a state file and *deciding what it means* would be detection.

**So the breaker writes the sentence.** At the moment it trips, the actuator persists a human-readable
`BREAKER_REASON_TEXT` saying what happened and what a human must do; the alarm **quotes it verbatim**. That is
the identical relationship the alarm already has with `GVApiAdapter.cs`'s strings — and it gets the identical
protection: a drift guard in the pattern of `AlarmCopyDriftTests.cs` (Task 14).

---

### 0.11 ⚠ THE ACTUATOR AND THE 20-MINUTE CRON CAN RACE, and post-hardening the race is benign

`*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh` is live and load-bearing (measured; and see
`KNOWN-ISSUES.md`'s ⛔ SUPERSEDED block). It can fire while the actuator is mid-login and harvest a half-signed-in
Chrome.

Post-hardening that harvest is REJECTED and harmless (§0.8). Two consequences that are **not** free:

1. The actuator must not read a bare `Succeeded` as its own work — see §0.8's `validatedAt` rule.
2. The actuator must hold its own `flock`, so two timer firings can never overlap each other. `flock` is present
   on the box (`/usr/bin/flock`, measured).

⛔ **And the actuator must not attempt to coordinate with the cron.** That cron is a crontab entry outside this
repo's deploy path; reaching into it is a box-state change with its own rollback story, and the race is benign.

---

### 0.12 ✅ THE BOX ALREADY HAS EVERYTHING THE SPIKE NEEDS — nothing is installed on a shared box

```
python3 -c "import websocket"   -> websocket-client OK 1.9.0
python3 -c "import websockets"  -> websockets OK 16.0
node                            -> MISSING
~/.cache/ms-playwright          -> does not exist
/opt/rotary-phone/.playwright   -> does not exist
curl /usr/bin/curl   jq /usr/bin/jq   flock /usr/bin/flock
curl -s :9224/json/version -> {"Browser": "Chrome/152.0.7977.82", "Protocol-Version": "1.3", ...}
pgrep argv -> --user-data-dir=/home/mmack/.config/gv-bridge-chrome
              --remote-debugging-port=9224
              --remote-allow-origins=*
```

⭐ So the spike and the actuator are **pure `python3` + `websocket-client`**, already present. **No package
install on a box shared with Radio Console**, which is a boundary event this plan does not have to spend.
`--remote-allow-origins=*` is on the *running* argv, not merely in the repo copy — the spec's §2 claim confirmed
against the process rather than the file.

📌 And the corollary: **no Playwright, no node.** Any design that reached for either would require installing a
browser toolchain on the shared box. It does not.

---

### 0.13 📌 THE CANNIBALISATION GATE IS FURTHER ALONG THAN THE DISPATCH SAID — and that is still not the gate concluding

| | At dispatch | Measured 19:34:52-04:00 |
|---|---|---|
| elapsed | 107 min | **121 min** |
| samples | 53 | **60** |
| staleness | zero | **zero** |
| prior session deaths | ~50 min, ~111 min | **both now cleared** |

```
2026-09-09T19:30:52-04:00 stale=false age=650 psidts=null
2026-09-09T19:32:52-04:00 stale=false age=770 psidts=null
2026-09-09T19:34:52-04:00 stale=false age=890 psidts=null
```

The `age` column climbing 650→770→890 is the 20-minute cron's sawtooth, exactly as
`gv-session-alarm.md` Task 15 predicted it would look.

⛔ **Two hours clears two data points. It does not establish "sessions last months."** Spec §3 sets the bar at
*"sessions return to lasting months"* and that is not a claim two hours can support in either direction. **The
owner sets the bar and reads the result** — Task 1, decision **G1**. This plan deliberately proposes no
duration.

---

## 1. How anything gets verified

⛔ **The rule, inherited unchanged from `gv-session-alarm.md` §1 and the boundary doc:** *every acceptance check
reads the **installed** artefact or an **outcome** — never a repo file, never "the component ran."* The
exceptions are unit tests and local harnesses, whose subject genuinely **is** the repo source, and they are
marked as such.

⛔ **And the rule this plan adds, from §0.6:** *an acceptance check for the breaker must be able to fail.* Every
breaker case below is written so that the **unsafe** behaviour is what the assertion catches — not so that the
safe behaviour is what it confirms.

### 1.1 Lanes

| Lane | Where | What it can prove | Needs the box? |
|---|---|---|---|
| **L** — local | WSL / any Linux, `deploy/tests/` | the breaker's whole decision tree, the actuator's classifier, the alarm's second track, the deploy exclusions — against **stubs and a local Chrome** | no |
| **U** — unit tests | `dotnet test` | the breaker-copy drift guard | no |
| **B** — box, read-only or reversible | `radio` over `ssh-mcp` | installed-vs-shipped state, the CDP target shape, the file modes | yes |
| **S** — ⛔ **the spike**: box, attended, destructive, owner present | `radio` | **what Google's sign-in page actually does** | yes, **and the owner** |
| **T** — box, live, token-gated | `radio` after the alarm is delivering | breaker trips reaching a **human**, the end-to-end restore | yes |

⚠ **Lane U cannot run in WSL on this workstation.** `gv-session-alarm.md` §7.7: the projects target `net10.0`
and WSL carries only SDK 8.0.131 / 9.0.115. Use the Windows SDK — `dotnet.exe` from WSL works.

### 1.2 The two owner gates

Both are the owner's, both are recorded here, and **neither is a preference.**

| Gate | Blocks | Decided by |
|---|---|---|
| **G1 — the cannibalisation result** | ⛔ **Task 4, the spike.** A sign-in during the test would confound it: the test is measuring whether sessions still die, and a scripted sign-in is a session event | the **owner**, on the data. §0.13 |
| **G2 — the death rate** | ⛔ **Task 17, shipping the actuator.** Spec §3: if sessions still die hourly, automating login raises the sign-in rate into lockout territory. **The death rate gets fixed first** | the **owner**, on the same data |

⭐ **G1 and G2 read the same measurement and are not the same gate.** G1 asks *"has the test finished?"* — it
gates the spike, which only needs the test not to be running. G2 asks *"what did it say?"* — it gates the
actuator, and a result of *"sessions still die hourly"* satisfies G1 while **failing** G2. The spike is worth
running either way, because what it learns about Google's sign-in page is the same knowledge whether or not the
actuator ships.

### 1.3 Branching

Per repo policy, implementation happens on `feat/gv-auto-relogin` and merges via PR. **This plan and the spec
are preparatory and live on `main`.** No task below commits anything until the branch exists.

---

## 2. Task list

Dependency order. **Phases 0 and 1 build nothing.** Phase 2 is the breaker, alone, before any login code exists.

---

### Phase 0 — the gates, and the ground truth

---

#### Task 1 — Record both gates, and get the owner's bar · lane **O** (owner)

**No code.** Its output is a decision, written down.

Present to the owner, in this shape:

| | Question | What is known |
|---|---|---|
| **G1** | Is the cannibalisation test concluded, so a scripted sign-in will not confound it? | 121 min, 60 samples, zero staleness, past both prior deaths (~50, ~111 min). §0.13 |
| **G2** | Does the result clear the actuator to ship — i.e. are sessions no longer dying at a rate that would make automated sign-ins frequent? | ⛔ **not established.** Two hours does not support *"months"* in either direction |

⛔ **Do not propose a duration.** Spec §3's bar is *"sessions return to lasting months"* and spec §10 decision 2
assigns it to the owner. A plausible-looking number here is the same defect as
`gv-session-alarm.md` Task 15's forbidden threshold: it converts a judgement into a fact nobody measured.

**Acceptance:**

- Both gates are recorded in the PR body with the owner's answer and the measurement it was given.
- ⛔ If G1 is not yet met, **Task 4 does not run.** Tasks 3, 5, 6 and 7 are all box-free and proceed regardless.
- ⛔ If G2 is not met, **Task 17 does not run and the timer is never enabled.** Everything up to Task 16 still
  ships — an installed, disabled actuator with a tripped breaker is a safe resting state, and it is the state
  Task 15 leaves the box in by default.

---

#### Task 2 — ⛔ GATE: the escalation path must be LIVE before the actuator ships · lane **B** + **T**

**Depends on:** nothing. **This task implements nothing and adopts nothing.** §0.1.

`gv-session-alarm.md` Tasks 5, 16 and 17 are **unstarted**, and until they are done the alarm is not installed
on the box and has no token. This plan does not adopt that work — re-planning another plan's unfinished tasks
is duplicate work — but it **cannot ship an actuator without it**, because a breaker that trips into silence is
worse than no breaker.

Confirm, on the box, all three:

```bash
# 1. INSTALLED — the artefact, not the repo
ls -l --time-style=long-iso ~/bin/gv-session-alarm.sh
sha256sum ~/bin/gv-session-alarm.sh /opt/rotary-phone/deploy/gv-session-alarm.sh

# 2. ENABLED and firing — plain list-timers, no --all
#    (gv-session-alarm.md §7.2 measured that --all does NOT list a disabled timer,
#     so `list-unit-files` is the instrument for "installed" and this is the one for "firing")
systemctl --user list-unit-files 'gv-session-alarm.*'
systemctl --user list-timers 'gv-session-alarm.*'

# 3. DELIVERING — a message the owner confirms SEEING, not a 202
~/bin/gv-session-alarm.sh; echo "exit=$?"
journalctl --user -u gv-session-alarm -n 20 --no-pager | grep -E 'heartbeat refreshed|NOTIFY'
```

**Acceptance** — this is a gate, so it **fails** rather than warns:

- The two sha256 values match, and `list-unit-files` shows both units.
- `list-timers` (plain) shows a NEXT within 5 minutes.
- ⛔ **The owner confirms having SEEN a delivered message.** Not a 202, not a journal line. `gv-session-alarm.md`
  Task 16 is explicit about this and it is the whole lesson of the arc.
- ⛔ **If any of the three fails, Tasks 15, 16 and 17 are blocked.** Say so in the PR body and stop. Do not
  build a substitute escalation path into the actuator — spec §5 is explicit that the alarm and the actuator
  stay separate *so that a bug in the actuator cannot take down the alarm that would have reported it*, and a
  second notifier inside the actuator would give that away for a schedule.

---

### Phase 1 — ⛔ the spike. Nothing is implemented.

---

#### Task 3 — A CDP driver with no login logic in it · lane **L**

**Depends on:** nothing. Create `deploy/tools/gv-cdp.py`.

⚠ **It lives in `deploy/tools/` deliberately, and it is scp'd to the box by hand for the spike.**
`Deploy-ToLinux.ps1` collects shell scripts with `Get-ChildItem -Path deploy -Filter "*.sh" -File` — **no
`-Recurse`** — so nothing under `deploy/tools/` ever ships (`gv-session-alarm.md` §7.8, which caught exactly
this). That is correct here: **the spike is a one-off attended exercise, not a deployed artefact.** The
*actuator* ships from `deploy/` (Task 9). Do not "fix" this by moving the spike tool up a directory.

⛔ **This file knows nothing about passwords.** It is a transport: connect, navigate, evaluate, screenshot,
dump. Keeping the credential entirely out of it is what lets it be read, reviewed and reused without ever
being a place a secret could leak.

```python
#!/usr/bin/env python3
"""A minimal Chrome DevTools Protocol driver for the GV bridge's existing browser.

⛔ THIS FILE HANDLES NO CREDENTIALS. It navigates, evaluates, screenshots and dumps.
Anything secret is passed by the CALLER, on stdin, and never appears here as a
default, a constant, or an argv parameter.

⛔ IT NEVER LAUNCHES A BROWSER AND NEVER CLEARS A PROFILE. Spec §5: same-profile
re-login is materially safer than a fresh-device sign-in, because Google already
knows this device, profile and IP. Launching a clean browser converts routine
re-auth into an unrecognised-device sign-in, which is far more likely to be
challenged. There is deliberately no code path here that could do it.

Requires only python3 + websocket-client, both already on the box (measured
2026-09-09: websocket-client 1.9.0). No node, no Playwright, nothing installed.

  gv-cdp.py targets
  gv-cdp.py url      --target <id>
  gv-cdp.py navigate --target <id> --url https://...
  gv-cdp.py eval     --target <id> --expr 'document.title'
  gv-cdp.py shot     --target <id> --out /tmp/x.png
  gv-cdp.py dump     --target <id> --out /tmp/x.html
"""
import argparse
import base64
import json
import sys
import urllib.request

import websocket  # websocket-client


DEFAULT_PORT = 9224


def http_json(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=10) as r:
        return json.loads(r.read())


def targets(port):
    # Only real pages. iframes and workers are not navigable subjects and including
    # them is how a caller ends up driving the RotateCookiesPage iframe by accident.
    return [t for t in http_json(port, "/json/list") if t.get("type") == "page"]


class Session:
    """One websocket to one target. Short-lived on purpose: a long-lived connection
    to the bridge's browser is a thing that can be left behind."""

    def __init__(self, ws_url, timeout):
        # ⚠ suppress_origin is NOT set. gv-bridge-ensure.sh:99 launches Chrome with
        # --remote-allow-origins=* (verified on the RUNNING argv, not just the repo
        # copy, 2026-09-09), so the origin check is satisfied. If a future launch
        # narrows that flag this connect is where it will fail — loudly, which is
        # what we want, rather than being papered over here.
        self.ws = websocket.create_connection(ws_url, timeout=timeout)
        self.n = 0

    def send(self, method, **params):
        self.n += 1
        self.ws.send(json.dumps({"id": self.n, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self.n:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get("result", {})

    def wait_for(self, event, timeout):
        self.ws.settimeout(timeout)
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("method") == event:
                return msg.get("params", {})

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def open_target(port, target_id, timeout):
    for t in targets(port):
        if t["id"] == target_id:
            return Session(t["webSocketDebuggerUrl"], timeout)
    raise SystemExit(f"no page target with id {target_id}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["targets", "url", "navigate", "eval", "shot", "dump"])
    ap.add_argument("--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--target")
    ap.add_argument("--url")
    ap.add_argument("--expr")
    ap.add_argument("--out")
    # ⚠ MARKED FOR THE OWNER: 30s is a placeholder, not a measurement. Task 4 records
    # how long the real sign-in flow actually takes and this default is set from that
    # recording. Until then it is a guess wearing a number's clothes.
    ap.add_argument("--timeout", type=float, default=30.0)
    a = ap.parse_args()

    if a.cmd == "targets":
        for t in targets(a.port):
            print(f"{t['id']}\t{t['url']}")
        return

    s = open_target(a.port, a.target, a.timeout)
    try:
        if a.cmd == "url":
            # ⛔ NOT /json/list's cached .url. Measured 2026-09-09: a parked
            # workspace.google.com page sits in the target list while the session is
            # perfectly healthy, and KNOWN-ISSUES.md:16-22 records both the title and
            # the URL as stale cached renders. window.location.href read INSIDE the
            # target is the only reading that means anything.
            r = s.send("Runtime.evaluate", expression="window.location.href",
                       returnByValue=True)
            print(r["result"]["value"])
        elif a.cmd == "navigate":
            s.send("Page.enable")
            s.send("Page.navigate", url=a.url)
            s.wait_for("Page.loadEventFired", a.timeout)
            r = s.send("Runtime.evaluate", expression="window.location.href",
                       returnByValue=True)
            print(r["result"]["value"])
        elif a.cmd == "eval":
            r = s.send("Runtime.evaluate", expression=a.expr, returnByValue=True)
            print(json.dumps(r.get("result", {}).get("value")))
        elif a.cmd == "shot":
            r = s.send("Page.captureScreenshot")
            with open(a.out, "wb") as fh:
                fh.write(base64.b64decode(r["data"]))
            print(a.out)
        elif a.cmd == "dump":
            r = s.send("Runtime.evaluate",
                       expression="document.documentElement.outerHTML",
                       returnByValue=True)
            with open(a.out, "w") as fh:
                fh.write(r["result"]["value"])
            print(a.out)
    finally:
        s.close()


if __name__ == "__main__":
    sys.exit(main())
```

**Acceptance** — lane **L**, against any local Chrome started with `--remote-debugging-port`:

- `targets` lists **only** `type == "page"` entries. ⛔ Assert that an `iframe` present in `/json/list` is
  **absent** from the output — the box's real list contains a `RotateCookiesPage` iframe, and driving it by
  accident is a live possibility, not a hypothetical.
- `navigate --url https://example.com/` prints `https://example.com/`, read from `window.location.href` and not
  from the target list.
- ⛔ **`grep -ci 'password\|passwd\|credential\|secret' deploy/tools/gv-cdp.py` is 0.** This file must have no
  vocabulary for the thing it must never hold.
- ⛔ **`grep -c 'launch\|--user-data-dir\|Popen\|subprocess' deploy/tools/gv-cdp.py` is 0.** There is no code
  path that could start a browser or touch a profile.
- Running any subcommand leaves **no websocket open**: `ss -tnp | grep 9224` after the run shows no lingering
  connection from python.

---

#### Task 4 — ⛔ THE SPIKE: drive one real sign-in by hand and record what actually happens · lane **S**

**Depends on:** Task 3, and ⛔ **gate G1** (Task 1). **Attended. The owner is at the box.** Spec §8.

⛔ **This task's deliverable is a RECORDING, not a script.** It produces
`docs/spikes/2026-09-09-gv-signin-cdp-recording.md` and nothing else. If it produces a working login script as a
side effect, that script is a **draft input to Task 10**, not an output of this task, and it does not ship from
here.

**Before starting, announce and confirm:** this deliberately signs the box's Chrome out (§0.4). The phone keeps
working on its own lineage; the cron cannot downgrade the good set; the fallback is the owner's usual two-minute
manual re-login. Have the owner acknowledge that before the first command.

**Sequence — the order is load-bearing (§0.5):**

```bash
# --- 0. BEFORE. Record the healthy baseline so every later reading has a "before".
scp deploy/tools/gv-cdp.py mmack@radio:/tmp/gv-cdp.py     # the spike tool is hand-carried, not deployed
curl -s localhost:5004/api/gvbridge/status | jq '{browserRefreshOutcome, browserSessionStale, browserSessionValidatedAt, browserSessionAgeSeconds}'
python3 /tmp/gv-cdp.py targets
TID=<the voice.google.com page target id>
python3 /tmp/gv-cdp.py url --target "$TID"
python3 /tmp/gv-cdp.py shot --target "$TID" --out /tmp/spike-00-before.png

# --- 1. SIGN OUT deliberately.
python3 /tmp/gv-cdp.py navigate --target "$TID" --url 'https://accounts.google.com/Logout'
python3 /tmp/gv-cdp.py navigate --target "$TID" --url 'https://voice.google.com/u/0/voicemail'
#   ⭐ THE OUTCOME CHECK, in its first real use: this must now land on
#   workspace.google.com/products/voice/. If it does not, we are not signed out and
#   nothing below is measuring what it claims to measure. STOP if so.

# --- 2. CORRECT PASSWORD, by hand, one step at a time. Record EVERYTHING.
#     Owner types the password. The spike operator does not see it, does not ask for
#     it, and does not put it anywhere. Screenshot and dump at EVERY step boundary.
python3 /tmp/gv-cdp.py navigate --target "$TID" --url 'https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/'
python3 /tmp/gv-cdp.py dump --target "$TID" --out /tmp/spike-10-email-form.html
python3 /tmp/gv-cdp.py shot --target "$TID" --out /tmp/spike-10-email-form.png
#     ... owner drives the email step ...
python3 /tmp/gv-cdp.py dump --target "$TID" --out /tmp/spike-20-password-form.html
#     ... owner drives the password step ...
python3 /tmp/gv-cdp.py dump --target "$TID" --out /tmp/spike-30-after-submit.html
python3 /tmp/gv-cdp.py url  --target "$TID"

# --- 3. VERIFY BY OUTCOME, both halves (§0.8).
python3 /tmp/gv-cdp.py navigate --target "$TID" --url 'https://voice.google.com/u/0/voicemail'
BEFORE=$(curl -s localhost:5004/api/gvbridge/status | jq -r .browserSessionValidatedAt)
curl -sS -X POST localhost:5004/api/gvbridge/cookies/refresh-from-browser -H 'Content-Type: application/json' -d '{}'
curl -s localhost:5004/api/gvbridge/status | jq --arg b "$BEFORE" '{browserRefreshOutcome, before:$b, after:.browserSessionValidatedAt}'

# --- 4. SIGN OUT AGAIN, then ONE wrong password. LAST, and exactly once (§0.5).
python3 /tmp/gv-cdp.py navigate --target "$TID" --url 'https://accounts.google.com/Logout'
#     ... owner drives email step, then ONE deliberately wrong password ...
python3 /tmp/gv-cdp.py dump --target "$TID" --out /tmp/spike-40-rejection.html
python3 /tmp/gv-cdp.py shot --target "$TID" --out /tmp/spike-40-rejection.png

# --- 5. RESTORE. Correct password, and confirm the outcome the same way as step 3.
```

**What the recording must contain — and each row is an input Task 10 cannot be written without:**

| # | Record | Why Task 10 needs it |
|---|---|---|
| 1 | The **exact URL** that presents the email form, and whether it redirects | the entry point |
| 2 | A **selector** for the email field and its submit, from the dumped DOM | to fill it |
| 3 | Whether email and password are **two navigations or one page** | the flow's shape |
| 4 | A **selector** for the password field and its submit | to fill it |
| 5 | The **settled URL** after a successful sign-in | the success predicate |
| 6 | ⛔ **The rejection shape**: the DOM text, any `aria-live` region, the URL, and whether the URL changes at all | **the terminal classifier.** Without this there is no rejection detector, and §0.6's default is the only thing standing between us and a lock |
| 7 | **Timings** for every step boundary | replaces the placeholder timeouts in Tasks 3 and 10 |
| 8 | ⭐ Whether **anything at all resembling a challenge** appeared — device verification, captcha, "unusual activity", a phone prompt, an "is this you" interstitial | the go/no-go |
| 9 | Whether step 4's rejection changed anything about step 5 | §0.5's contamination check |

**⭐ THE OUTCOME THIS TASK MUST BE ALLOWED TO HAVE — plan for it, do not plan around it:**

> ⛔ **If Google challenges a scripted sign-in from this profile, THIS DESIGN DOES NOT WORK, and the honest
> deliverable is to say so.**

What we do then, decided **now** so it is not decided under pressure later:

1. **Write it in the recording, in those words.** A spike that discovers the design is unbuildable has
   succeeded; the artefact is the finding.
2. **Stop.** Tasks 5–17 do not run. The breaker, the credential file, the actuator and the alarm condition are
   all abandoned, not shelved-with-a-workaround.
3. ⛔ **Do NOT add retries, do NOT add a "just once more", do NOT try a different entry URL to route around it.**
   A challenge means Google *already* considers this suspicious (spec §6). Every attempt to get past one
   deepens that, and the thing being risked is the account.
4. ⛔ **Do NOT clear the profile or launch a fresh browser to "get a cleaner run."** That converts routine
   re-auth into an unrecognised-device sign-in — strictly more likely to be challenged, and it destroys the
   profile whose familiarity is the entire safety argument.
5. **What survives:** the alarm from PR #85, which shortens the detection half of an outage and is the thing
   that made this design conceivable. Spec §10's Phase 2 (reachable re-auth: raise the window, confirm the
   re-login took) becomes the next design instead, and it needs no credentials at all.

**Acceptance:**

- `docs/spikes/2026-09-09-gv-signin-cdp-recording.md` exists and answers all nine rows, each citing a saved
  artefact by filename.
- ⛔ **The wrong-password attempt happened exactly once.** State the count explicitly in the recording. If it
  happened twice for any reason, say so and say why — that is a real event on a real account, not a test detail.
- ⛔ **The password appears nowhere:** not in the recording, not in a screenshot (redact the field), not in the
  DOM dumps (⚠ **grep the dumps before committing them** — a `value=` attribute on a password input would carry
  it into git forever), not in `/tmp` after the run, not in shell history.
- The session is **restored** at the end, confirmed by `browserSessionValidatedAt` moving across a
  `refresh-from-browser` **that the spike ran** — not by a `Succeeded` that the 20-minute cron may have produced
  (§0.8).
- ⛔ The go/no-go on row 8 is stated as a **sentence**, not left to be inferred from the absence of a screenshot.

---

### Phase 2 — ⛔ the circuit breaker, alone, before any login code exists

⭐ **Why this phase precedes the actuator rather than accompanying it.** A breaker written alongside the thing it
restrains gets shaped by that thing's convenience — a retry here, a reset there, each locally reasonable. Built
first, against nothing, it can only be shaped by its own rules. And it is testable to exhaustion without a
browser, a credential or a network.

---

#### Task 5 — `deploy/gv-auto-relogin-breaker.sh`: the breaker, and nothing else · lane **L**

**Depends on:** nothing. **Contains no login code, no credential handling, and no CDP.**

```bash
#!/usr/bin/env bash
# =============================================================================
# THE CIRCUIT BREAKER. This file is the feature.
#
# ⛔ Spec §3: automated login is only safe if login is RARE, and an account lock is
# STRICTLY WORSE than the problem being solved — it is a catastrophic event needing
# the owner urgently, with the phone down and no fallback. A version of this arc
# that logs in reliably but retries freely is worse than no automation at all.
#
# Sourced by gv-auto-relogin.sh. Also runnable directly for --status and --reset,
# which are the ONLY human interfaces it has.
#
# ⛔ FOUR RULES, AND NONE OF THEM IS NEGOTIABLE:
#
#   1. ONE credential rejection stops EVERYTHING, PERMANENTLY. Not a backoff, not a
#      retry-after, not "once more in an hour". A rejected credential is never
#      transient — retrying a wrong password is the single most reliable way to get
#      an account locked.
#   2. A CHALLENGE stops everything, permanently. A challenge means Google ALREADY
#      considers this suspicious; retrying deepens it.
#   3. UNKNOWN IS TERMINAL, NOT RETRYABLE. See breaker_trip's callers: the
#      classifier's default branch trips. The rejection detector is built from a
#      sample of exactly ONE rejection (the spike's, and there will never be a
#      second because of rule 1), so the case this will most often meet is one it
#      does not recognise. Mapping unknown to "retryable" is a one-line path to the
#      catastrophic outcome.
#   4. FAIL CLOSED. A missing or unparseable state file means we cannot account for
#      how many times we have signed in today — which is the ONLY thing this file
#      exists to bound. It reports TRIPPED. Only --reset (a human) creates one.
#
# ⛔ NOTHING IN THIS FILE RE-ARMS ITSELF. There is no timeout, no daily reset, no
# "cool-off". Search for BREAKER_STATE=ARMED: it appears in exactly one function,
# breaker_reset, reachable only from an explicit human --reset.
# =============================================================================
set -uo pipefail

BREAKER_STATE_FILE="${GV_RELOGIN_STATE_FILE:-${HOME}/.local/state/gv-auto-relogin.state}"

# ⚠ MARKED FOR THE OWNER — spec §10 decision 4: "a starting point, not measured."
# Both numbers are the SPEC'S, carried through unchanged. This plan invents neither
# and proposes no others. Revisit once the real re-login frequency is known.
BREAKER_MAX_PER_HOUR="${GV_RELOGIN_MAX_PER_HOUR:-1}"
BREAKER_MAX_PER_DAY="${GV_RELOGIN_MAX_PER_DAY:-3}"
# ⚠ The transport ceiling REUSES the spec's daily number rather than introducing a
# fourth one. It is not a measurement and is not claimed to be.
BREAKER_MAX_TRANSPORT_PER_DAY="${GV_RELOGIN_MAX_TRANSPORT_PER_DAY:-${BREAKER_MAX_PER_DAY}}"

BREAKER_STATE=""
BREAKER_REASON=""
BREAKER_REASON_TEXT=""
BREAKER_TRIPPED_AT=""
BREAKER_LAST_ATTEMPT_AT="0"
BREAKER_DAY_BUCKET=""
BREAKER_DAY_CREDENTIAL_ATTEMPTS="0"
BREAKER_DAY_TRANSPORT_FAILURES="0"
BREAKER_ATTEMPTS_TOTAL="0"
BREAKER_LAST_OUTCOME=""
BREAKER_REFUSAL=""

breaker_log() { printf '%s gv-relogin-breaker[%s]: %s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$$" "$*" >&2; }

breaker_today() { date -u +%Y-%m-%d; }

# --- Load, and FAIL CLOSED --------------------------------------------------
breaker_load() {
    if [ ! -r "$BREAKER_STATE_FILE" ]; then
        # ⛔ ABSENT IS NOT ARMED. Without the file there is no record of how many
        # sign-ins have already happened today, and bounding that count is the only
        # job this file has. An actuator that treats "I lost my memory" as "I may
        # proceed" has no rate limit at all — it has a rate limit that resets
        # whenever anything deletes a file.
        BREAKER_STATE="TRIPPED"
        BREAKER_REASON="state_missing"
        BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file is missing at ${BREAKER_STATE_FILE}. Without it there is no record of how many sign-ins have already been attempted today, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
        return 0
    fi

    # shellcheck disable=SC1090
    if ! . "$BREAKER_STATE_FILE"; then
        BREAKER_STATE="TRIPPED"
        BREAKER_REASON="state_unreadable"
        BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file at ${BREAKER_STATE_FILE} could not be read. The attempt history is unknown, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
        return 0
    fi

    case "$BREAKER_STATE" in
        ARMED|TRIPPED) ;;
        *)
            # An unrecognised state is not a state. Same rule as rule 3, applied to
            # our own file.
            BREAKER_STATE="TRIPPED"
            BREAKER_REASON="state_corrupt"
            BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file records an unrecognised state. The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            ;;
    esac

    # Roll the daily buckets. ⚠ Rolling the DAY does not re-arm a TRIPPED breaker —
    # the two are independent, and conflating them is how a permanent stop quietly
    # becomes a 24-hour pause.
    local today; today="$(breaker_today)"
    if [ "${BREAKER_DAY_BUCKET:-}" != "$today" ]; then
        BREAKER_DAY_BUCKET="$today"
        BREAKER_DAY_CREDENTIAL_ATTEMPTS=0
        BREAKER_DAY_TRANSPORT_FAILURES=0
    fi
    return 0
}

breaker_write() {
    local dir; dir="$(dirname "$BREAKER_STATE_FILE")"
    mkdir -p "$dir" 2>/dev/null || { breaker_log "could not create ${dir}"; return 1; }
    # Atomic replace, same reasoning as the alarm's write_state: a reader must see
    # the whole old file or the whole new one. And mode 600 from birth — umask is
    # not trusted to produce it, because this file records account-level events.
    ( umask 077
      {
        printf '# gv-auto-relogin breaker state, written %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '# Inspect with: gv-auto-relogin.sh --status\n'
        printf 'BREAKER_STATE=%q\n'                   "$BREAKER_STATE"
        printf 'BREAKER_REASON=%q\n'                  "$BREAKER_REASON"
        printf 'BREAKER_REASON_TEXT=%q\n'             "$BREAKER_REASON_TEXT"
        printf 'BREAKER_TRIPPED_AT=%q\n'              "$BREAKER_TRIPPED_AT"
        printf 'BREAKER_LAST_ATTEMPT_AT=%q\n'         "$BREAKER_LAST_ATTEMPT_AT"
        printf 'BREAKER_DAY_BUCKET=%q\n'              "$BREAKER_DAY_BUCKET"
        printf 'BREAKER_DAY_CREDENTIAL_ATTEMPTS=%q\n' "$BREAKER_DAY_CREDENTIAL_ATTEMPTS"
        printf 'BREAKER_DAY_TRANSPORT_FAILURES=%q\n'  "$BREAKER_DAY_TRANSPORT_FAILURES"
        printf 'BREAKER_ATTEMPTS_TOTAL=%q\n'          "$BREAKER_ATTEMPTS_TOTAL"
        printf 'BREAKER_LAST_OUTCOME=%q\n'            "$BREAKER_LAST_OUTCOME"
      } > "${BREAKER_STATE_FILE}.new" ) \
      || { rm -f "${BREAKER_STATE_FILE}.new"; breaker_log "could not write state"; return 1; }
    mv -f "${BREAKER_STATE_FILE}.new" "$BREAKER_STATE_FILE" \
      || { rm -f "${BREAKER_STATE_FILE}.new"; breaker_log "could not replace state"; return 1; }
    chmod 600 "$BREAKER_STATE_FILE" 2>/dev/null
    return 0
}

# --- May we attempt? --------------------------------------------------------
# Returns 0 to authorise, 1 to refuse. On refusal, BREAKER_REFUSAL says why in
# words an operator can read without opening this file.
breaker_may_attempt() {
    BREAKER_REFUSAL=""

    if [ "$BREAKER_STATE" = "TRIPPED" ]; then
        BREAKER_REFUSAL="breaker TRIPPED (${BREAKER_REASON}) at ${BREAKER_TRIPPED_AT:-unknown}; a human must --reset"
        return 1
    fi

    local now; now="$(date -u +%s)"
    local since=$(( now - ${BREAKER_LAST_ATTEMPT_AT:-0} ))
    local window=$(( 3600 / BREAKER_MAX_PER_HOUR ))
    if [ "${BREAKER_LAST_ATTEMPT_AT:-0}" -gt 0 ] && [ "$since" -lt "$window" ]; then
        BREAKER_REFUSAL="rate limit: last attempt ${since}s ago, minimum spacing ${window}s (${BREAKER_MAX_PER_HOUR}/hour)"
        return 1
    fi

    if [ "${BREAKER_DAY_CREDENTIAL_ATTEMPTS:-0}" -ge "$BREAKER_MAX_PER_DAY" ]; then
        BREAKER_REFUSAL="rate limit: ${BREAKER_DAY_CREDENTIAL_ATTEMPTS}/${BREAKER_MAX_PER_DAY} credential attempts already used today (${BREAKER_DAY_BUCKET})"
        return 1
    fi

    if [ "${BREAKER_DAY_TRANSPORT_FAILURES:-0}" -ge "$BREAKER_MAX_TRANSPORT_PER_DAY" ]; then
        BREAKER_REFUSAL="transport ceiling: ${BREAKER_DAY_TRANSPORT_FAILURES}/${BREAKER_MAX_TRANSPORT_PER_DAY} transport failures today; not attempting again until tomorrow"
        return 1
    fi

    return 0
}

# --- Recording outcomes -----------------------------------------------------
# ⛔ THE CREDENTIAL BUDGET IS SEPARATE FROM THE ATTEMPT BUDGET, and spec §9.4
# requires it: "a CDP transport failure retries within the rate limit and does not
# consume the credential budget." A transport failure means we never reached the
# form, so Google saw nothing and no credential was spent. It still consumes the
# hourly spacing (or a broken CDP would spin) and its own daily ceiling.
breaker_record_credential_attempt() {
    BREAKER_LAST_ATTEMPT_AT="$(date -u +%s)"
    BREAKER_DAY_CREDENTIAL_ATTEMPTS=$(( ${BREAKER_DAY_CREDENTIAL_ATTEMPTS:-0} + 1 ))
    BREAKER_ATTEMPTS_TOTAL=$(( ${BREAKER_ATTEMPTS_TOTAL:-0} + 1 ))
}

breaker_record_transport_failure() {
    BREAKER_LAST_ATTEMPT_AT="$(date -u +%s)"
    BREAKER_DAY_TRANSPORT_FAILURES=$(( ${BREAKER_DAY_TRANSPORT_FAILURES:-0} + 1 ))
    BREAKER_LAST_OUTCOME="transport"
}

breaker_record_success() {
    BREAKER_LAST_OUTCOME="succeeded"
    # ⚠ Success does NOT decrement or reset anything. The budget bounds sign-ins,
    # not failures — three successful sign-ins in a day is exactly as much Google
    # traffic as three failed ones, and it is the traffic that is being bounded.
}

# ⛔ THE ONE-WAY DOOR. Everything that reaches here is terminal.
breaker_trip() {
    BREAKER_STATE="TRIPPED"
    BREAKER_REASON="$1"
    BREAKER_REASON_TEXT="$2"
    BREAKER_TRIPPED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    BREAKER_LAST_OUTCOME="$1"
    breaker_log "TRIPPED (${BREAKER_REASON}). No further attempt will be made until a human runs --reset."
}

# --- Human interfaces -------------------------------------------------------
breaker_status() {
    breaker_load
    printf 'state              %s\n' "$BREAKER_STATE"
    printf 'reason             %s\n' "${BREAKER_REASON:-none}"
    printf 'tripped_at         %s\n' "${BREAKER_TRIPPED_AT:-never}"
    printf 'last_attempt_at    %s\n' \
        "$([ "${BREAKER_LAST_ATTEMPT_AT:-0}" -gt 0 ] && date -u -d "@${BREAKER_LAST_ATTEMPT_AT}" +%Y-%m-%dT%H:%M:%SZ || echo never)"
    printf 'today              %s\n' "${BREAKER_DAY_BUCKET:-none}"
    printf 'credential today   %s/%s\n' "${BREAKER_DAY_CREDENTIAL_ATTEMPTS:-0}" "$BREAKER_MAX_PER_DAY"
    printf 'transport today    %s/%s\n' "${BREAKER_DAY_TRANSPORT_FAILURES:-0}" "$BREAKER_MAX_TRANSPORT_PER_DAY"
    printf 'attempts total     %s\n' "${BREAKER_ATTEMPTS_TOTAL:-0}"
    printf 'last outcome       %s\n' "${BREAKER_LAST_OUTCOME:-none}"
    if [ -n "${BREAKER_REASON_TEXT:-}" ]; then
        printf '\n%s\n' "$BREAKER_REASON_TEXT"
    fi
}

# ⛔ THE ONLY PLACE BREAKER_STATE BECOMES ARMED. If a future edit adds a second,
# the breaker has stopped being one.
breaker_reset() {
    breaker_load
    printf 'Clearing:\n'
    printf '  state   %s\n' "$BREAKER_STATE"
    printf '  reason  %s\n' "${BREAKER_REASON:-none}"
    printf '  since   %s\n' "${BREAKER_TRIPPED_AT:-never}"
    BREAKER_STATE="ARMED"
    BREAKER_REASON=""
    BREAKER_REASON_TEXT=""
    BREAKER_TRIPPED_AT=""
    BREAKER_LAST_OUTCOME="reset"
    # ⚠ The COUNTERS are deliberately NOT cleared. A reset says "I have fixed the
    # account", not "today did not happen". Clearing them would let a human hand
    # back a full daily budget by typing one command, which is the loophole that
    # makes a daily limit decorative.
    breaker_write || return 1
    printf 'Breaker ARMED. Counters preserved: %s/%s credential attempts used today.\n' \
        "${BREAKER_DAY_CREDENTIAL_ATTEMPTS:-0}" "$BREAKER_MAX_PER_DAY"
}

# Direct invocation: --status / --reset only.
if [ "${BASH_SOURCE[0]}" = "${0}" ]; then
    case "${1:-}" in
        --status) breaker_status ;;
        --reset)  breaker_reset ;;
        *) echo "usage: $0 --status | --reset" >&2; exit 2 ;;
    esac
fi
```

**Acceptance** — lane **L**, and every case is written so the **unsafe** behaviour is what fails it. Task 6's
harness runs them; this task's acceptance is that each is expressible:

- ⛔ `grep -c 'BREAKER_STATE="ARMED"' deploy/gv-auto-relogin-breaker.sh` is **1**, and it is inside
  `breaker_reset`. ⚠ Assert the count *and* the enclosing function — a second assignment anywhere else is the
  breaker ceasing to be one, and a bare count would not say where.
- ⛔ `grep -Ec 'sleep|retry|backoff|attempt_again|re_arm|rearm' deploy/gv-auto-relogin-breaker.sh` is **0**.
  This file has no vocabulary for trying again.
- The state file is mode **600** after every write, including the first.
- ⛔ `--reset` **preserves** the daily counters and says so.

---

#### Task 6 — The adversarial breaker harness · lane **L**

**Depends on:** Task 5. Create `deploy/tests/repro-gv-relogin-breaker.sh`.

⛔ **This is the most adversarial test file in the arc, and its cases are written to catch the DANGEROUS
behaviour, not to confirm the safe one.** A test that asserts "after a rejection, `--status` says TRIPPED" is
weak: it confirms a field. The test that matters asserts **that a second attempt is refused**, which is the
thing that would lock the account.

```bash
#!/usr/bin/env bash
# The circuit breaker, tested adversarially. No box, no network, no credential.
#
# ⛔ EVERY CASE BELOW IS WRITTEN SO THAT THE UNSAFE BEHAVIOUR IS WHAT FAILS IT.
# "TRIPPED is recorded" is a field. "A SECOND ATTEMPT IS REFUSED" is the property.
# Only the second one would have prevented a lockout.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BREAKER="${HERE}/../gv-auto-relogin-breaker.sh"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
export GV_RELOGIN_STATE_FILE="$WORK/breaker.state"

fail=0
check() { if [ "$2" = "$3" ]; then echo "  PASS $1"; else echo "  FAIL $1: expected [$2] got [$3]"; fail=1; fi; }

# Drive the library the way the actuator will, so the harness exercises the real
# entry points rather than a paraphrase of them.
drive() { bash -c '
    set -uo pipefail
    . "$1"
    breaker_load
    shift
    "$@"
' _ "$BREAKER" "$@"; }

echo "=== FAIL CLOSED: absence and corruption are TRIPPED, never ARMED ==="
rm -f "$GV_RELOGIN_STATE_FILE"
check "no state file -> TRIPPED" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
check "no state file -> refuses an attempt" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
printf 'BREAKER_STATE=BANANA\n' > "$GV_RELOGIN_STATE_FILE"
check "corrupt state -> TRIPPED" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
printf 'this is not shell (\n'  > "$GV_RELOGIN_STATE_FILE"
check "unparseable state -> TRIPPED" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"

echo "=== ⛔ ONE CREDENTIAL REJECTION STOPS EVERYTHING, PERMANENTLY ==="
bash "$BREAKER" --reset >/dev/null
# Simulate the actuator's terminal path exactly once.
bash -c '. "$1"; breaker_load; breaker_record_credential_attempt;
         breaker_trip credential_rejected "Google rejected the stored password."
         breaker_write' _ "$BREAKER"
check "after ONE rejection -> TRIPPED" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
# ⛔ THE LOAD-BEARING ASSERTION OF THE WHOLE ARC.
check "⛔ a SECOND attempt is REFUSED" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
# ...and it stays refused across time, a new day, and a reboot.
check "⛔ still refused after 24h of simulated time" "1" \
      "$(TZ=UTC faketime_hours=24 drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
sed -i 's/^BREAKER_DAY_BUCKET=.*/BREAKER_DAY_BUCKET=1970-01-01/' "$GV_RELOGIN_STATE_FILE"
check "⛔ a NEW DAY does NOT re-arm a tripped breaker" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
check "⛔ ...and still refuses" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"

echo "=== ⛔ A CHALLENGE STOPS EVERYTHING, and is distinguishable from a rejection ==="
bash "$BREAKER" --reset >/dev/null
bash -c '. "$1"; breaker_load; breaker_record_credential_attempt;
         breaker_trip challenged "Google presented a verification challenge."
         breaker_write' _ "$BREAKER"
check "challenge -> TRIPPED" "TRIPPED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
check "challenge -> refuses" "1" "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
check "challenge reason is NOT credential_rejected" "challenged" \
      "$(bash "$BREAKER" --status | awk '$1=="reason"{print $2}')"

echo "=== TRANSPORT is the ONLY retryable class, and it spends no credential budget ==="
bash "$BREAKER" --reset >/dev/null
bash -c '. "$1"; breaker_load; breaker_record_transport_failure; breaker_write' _ "$BREAKER"
check "transport failure -> still ARMED" "ARMED" \
      "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"
check "⛔ transport spends NO credential budget" "0/3" \
      "$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "transport spends its own budget" "1/3" \
      "$(bash "$BREAKER" --status | awk '$1=="transport"{print $3}')"
check "...but is still rate-limited this hour" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"

echo "=== RATE LIMITS bound the Google-facing traffic ==="
bash "$BREAKER" --reset >/dev/null
sed -i 's/^BREAKER_LAST_ATTEMPT_AT=.*/BREAKER_LAST_ATTEMPT_AT=0/' "$GV_RELOGIN_STATE_FILE"
check "a fresh armed breaker authorises" "0" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
bash -c '. "$1"; breaker_load; breaker_record_credential_attempt; breaker_write' _ "$BREAKER"
check "⛔ a second attempt within the hour is REFUSED" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
# Age the last attempt past the window without waiting an hour.
sed -i "s/^BREAKER_LAST_ATTEMPT_AT=.*/BREAKER_LAST_ATTEMPT_AT=$(( $(date -u +%s) - 3700 ))/" "$GV_RELOGIN_STATE_FILE"
check "an attempt an hour later is authorised" "0" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"
# Burn the daily budget.
for _ in 1 2 3; do
  bash -c '. "$1"; breaker_load; breaker_record_credential_attempt; breaker_write' _ "$BREAKER"
  sed -i "s/^BREAKER_LAST_ATTEMPT_AT=.*/BREAKER_LAST_ATTEMPT_AT=$(( $(date -u +%s) - 3700 ))/" "$GV_RELOGIN_STATE_FILE"
done
check "⛔ the 4th credential attempt today is REFUSED even with time available" "1" \
      "$(drive breaker_may_attempt >/dev/null 2>&1; echo $?)"

echo "=== --reset is a HUMAN action and does not hand back a budget ==="
before="$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
bash "$BREAKER" --reset >/dev/null
after="$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "⛔ --reset preserves the daily counter" "$before" "$after"
check "--reset arms" "ARMED" "$(bash "$BREAKER" --status | awk '$1=="state"{print $2}')"

echo "=== the state file is INSPECTABLE and PRIVATE ==="
check "mode 600" "600" "$(stat -c %a "$GV_RELOGIN_STATE_FILE")"
check "⛔ --status explains WHY without reading code" "yes" \
      "$(bash "$BREAKER" --status | grep -q 'reason' && echo yes || echo no)"
check "no .new debris" "0" "$(ls "$WORK"/*.new 2>/dev/null | wc -l)"

exit "$fail"
```

⚠ **`faketime_hours` above is a placeholder for whatever time-travel the builder chooses** — `libfaketime` if
present, otherwise the `sed` on `BREAKER_LAST_ATTEMPT_AT` used elsewhere in the file. Pick one and use it
consistently; do **not** make the harness sleep for an hour, and do **not** drop the case.

**Acceptance:**

- `bash deploy/tests/repro-gv-relogin-breaker.sh` exits **0** and prints a `PASS` for every case.
- ⛔ **Each of the four rules is proven by breaking it first.** Before the harness passes, deliberately
  introduce each defect and watch the corresponding case **fail**:
  1. make `breaker_load` default to `ARMED` on a missing file → the fail-closed block fails;
  2. make `credential_rejected` set `ARMED` → *"a SECOND attempt is REFUSED"* fails;
  3. make the day-roll clear `BREAKER_STATE` → *"a NEW DAY does not re-arm"* fails;
  4. make `breaker_record_transport_failure` also call `breaker_record_credential_attempt` → *"transport spends
     NO credential budget"* fails.
  ⭐ **A breaker nobody has watched fail is a breaker nobody knows is wired up** — the same rule the arc applied
  to the DTO order pin and the copy-drift guard, applied to the thing where it matters most.
- The harness leaves no temp directory and no `.new` file.

---

### Phase 3 — the credential store

---

#### Task 7 — `/opt/rotary-phone/gv-account.conf`: shape, mode, and BOTH deploy exclusions · lane **L** + **B**

**Depends on:** nothing. ⛔ **No task in this arc asks for, receives, transports, echoes or logs the password.**
The owner populates the file directly on the box. This task ships the *protection*, not the content.

**7a — the documented shape.** Add to `docs/DEPLOYMENT.md` (or the repo's equivalent operator doc) and to the
installer's `--help`:

```
# /opt/rotary-phone/gv-account.conf — mode 600, owner mmack.
#
# ⛔ POPULATED BY THE OWNER, ON THE BOX, BY HAND. No part of the RotaryPhone repo,
# no deploy, no agent and no script ever writes, reads back, echoes or transports
# these values. They exist in exactly one place.
#
# This file is sourced into the actuator's environment and the password is handed
# to the CDP driver ON STDIN. It is never an argv parameter: /proc/<pid>/cmdline is
# mode 0444 on this box (measured 2026-09-09) and `radio` is shared — beszel,
# avahi, colord and polkitd all run here, plus Radio Console under the same uid.
#
GV_ACCOUNT_EMAIL=
GV_ACCOUNT_PASSWORD=
```

**7b — the tar exclusion** (prevents an **overwrite**). `Deploy-ToLinux.ps1:247`:

```powershell
      " tar --null --exclude=./appsettings.Production.json --exclude=./gv-account.conf -czf - -T - |" +
```

**7c — ⛔ the rsync exclusion** (prevents a **DELETION**, and it is the one the spec's wording misses — §0.2).
`Deploy-ToLinux.ps1:126-131`:

```powershell
  rsync -az --delete `
    --exclude 'appsettings.Production.json' `
    # ⛔ NOT THE SAME KIND OF EXCLUSION AS THE TAR ONE, and the difference is the point.
    # The tar exclusion above keeps a member OUT OF THE STREAM, so the file is never
    # overwritten. THIS one defends against `--delete`, which removes every destination
    # file the source does not have — and the source is the publish output, which will
    # never contain a credential. Without this line the first rsync deploy DELETES the
    # box's Google password, silently, and the actuator then trips its breaker with
    # "credential file missing" on a box that is otherwise perfectly healthy.
    # ⚠ Not hypothetical: /opt/rotary-phone/refresh-gv-cookies.sh — the load-bearing
    # 20-minute cron — is in the same position today and has no such line.
    # See docs/plans/gv-auto-relogin.md §0.2.
    --exclude 'gv-account.conf' `
    --exclude 'data/' `
    --exclude 'logs/' `
```

**7d — the installer refuses to proceed without it.** Covered in Task 15; noted here so the pair is visible.

**Acceptance:**

- ⛔ **Both** exclusions are present, and a reviewer can point at which verb each one defends against.
- The mode is documented as **600** in every place the file is named.
- ⛔ `grep -rn 'GV_ACCOUNT_PASSWORD' --include=*.ps1 --include=*.cs deploy/ src/` returns **0**. The password's
  name may appear in the actuator and in docs; it may not appear in the deploy or in the service.
- ⚠ The file is **not created** by this task, by the installer, or by anything else in the repo. An empty
  `gv-account.conf` committed as a template would be a file the deploy could then ship, which is the failure the
  exclusions exist to prevent.

---

#### Task 8 — The test that proves BOTH exclusions hold · lane **L**

**Depends on:** Task 7. Extend `deploy/tests/repro-tar-clobber.sh`, which already models exactly this for
`appsettings.Production.json` and is the precedent spec §4 points at.

⛔ **Two cases, one per branch. A test that only inspects the tar member list passes while the rsync branch eats
the file** — that is §0.10 of the alarm plan's *"a check that ran, passed, and answered a different question"*,
and it is the specific shape this test must not take.

```bash
echo "=== gv-account.conf survives the TAR branch (not overwritten) ==="
# Build the member list exactly as Deploy-ToLinux.ps1:246-247 does.
( cd "$FIXTURE_PUBLISH" && find . -mindepth 1 -path ./.playwright -prune -o \( -type f -o -type l \) -print0 ) \
  | tar --null --exclude=./appsettings.Production.json --exclude=./gv-account.conf \
        -czf "$WORK/archive.tgz" -T - -C "$FIXTURE_PUBLISH"
check "gv-account.conf is NOT a member of the archive" "0" \
      "$(tar -tzf "$WORK/archive.tgz" | grep -c 'gv-account.conf')"
# And the outcome, not the mechanism: extract over a box-like tree and read the file.
printf 'GV_ACCOUNT_PASSWORD=THE-OWNERS-SECRET\n' > "$WORK/box/gv-account.conf"
chmod 600 "$WORK/box/gv-account.conf"
tar -xzf "$WORK/archive.tgz" --unlink-first -C "$WORK/box"
check "⛔ the box's credential SURVIVED the extract" "GV_ACCOUNT_PASSWORD=THE-OWNERS-SECRET" \
      "$(cat "$WORK/box/gv-account.conf")"
check "...and kept mode 600" "600" "$(stat -c %a "$WORK/box/gv-account.conf")"

echo "=== ⛔ gv-account.conf survives the RSYNC branch (not DELETED) ==="
# ⛔ THE CASE THE SPEC'S WORDING MISSES. --delete removes every destination file the
# source lacks; the publish output will never contain a credential. Without the
# --exclude this assertion fails, and it fails by the file being GONE, not stale.
rsync -a --delete \
  --exclude 'appsettings.Production.json' \
  --exclude 'gv-account.conf' \
  --exclude 'data/' --exclude 'logs/' \
  "$FIXTURE_PUBLISH/" "$WORK/box/"
check "⛔ the box's credential SURVIVED rsync --delete" "GV_ACCOUNT_PASSWORD=THE-OWNERS-SECRET" \
      "$(cat "$WORK/box/gv-account.conf" 2>/dev/null)"

echo "=== the test can FAIL — proven, not assumed ==="
# ⚠ Run the same rsync WITHOUT the exclusion and confirm the file is destroyed. A
# protection test that has never seen the unprotected case is a check that cannot
# fail, which is the first of the boundary doc's four neighbours.
cp "$WORK/box/gv-account.conf" "$WORK/keep.conf"
rsync -a --delete --exclude 'appsettings.Production.json' \
  --exclude 'data/' --exclude 'logs/' "$FIXTURE_PUBLISH/" "$WORK/box/"
check "⛔ WITHOUT the exclusion the credential is DELETED" "missing" \
      "$([ -f "$WORK/box/gv-account.conf" ] && echo present || echo missing)"
cp "$WORK/keep.conf" "$WORK/box/gv-account.conf"
```

**Acceptance:**

- Both branch cases pass, and the **negative control** (the last block) demonstrates the unprotected file being
  destroyed.
- ⛔ The assertion reads the **file's contents**, not the archive's member list. A member-list check is a
  mechanism check; the content is the outcome.
- ⚠ If `rsync` is not installed on the machine running the harness, the rsync cases must **skip loudly with a
  named reason and a non-silent marker** — never pass by absence. A silently-skipped protection test is exactly
  the *"check that goes quiet when its subject is missing"* the repo corrected in its deploy gate on
  2026-09-09.

---

### Phase 4 — the actuator

⛔ **Every task in this phase is blocked on Task 4 reporting that the design works.** If the spike found a
challenge, none of it is written.

---

#### Task 9 — `deploy/gv-auto-relogin.sh`: poll, gate, lock — and no secret in argv · lane **L**

**Depends on:** Tasks 4, 5. **This task's script does not log in.** It polls, consults the breaker, takes the
lock, sources the credential, and stops. Logging in is Task 10. Splitting it this way means the *gating* is
provable before any credential is in flight.

⚠ **It ships from `deploy/`, not `deploy/tools/`** — the deploy's shell glob has no `-Recurse`
(`gv-session-alarm.md` §7.8). Task 3's spike tool is the deliberate exception.

```bash
#!/usr/bin/env bash
# =============================================================================
# GV auto-relogin — the ACTUATOR. Handles the routine case silently so the owner
# is not in the loop; escalates through the PR #85 alarm when it cannot.
#
# ⛔ THIS SCRIPT IS SUBORDINATE TO ITS BREAKER. Read gv-auto-relogin-breaker.sh
# first. Nothing here may attempt a sign-in that breaker_may_attempt refused, and
# nothing here may set BREAKER_STATE.
#
# ⛔ IT NEVER CLEARS THE PROFILE AND NEVER LAUNCHES A BROWSER. Spec §5: same-profile
# re-login is materially safer than a fresh-device sign-in. Google already knows
# this device, profile and IP; a fresh browser converts routine re-auth into an
# unrecognised-device sign-in, which is far more likely to be challenged. Do not
# "clean up" by doing either.
#
# ⛔ IT DETECTS NOTHING ABOUT THE SESSION. The service already does that, and the
# alarm already transports it. This script reads browserRefreshOutcome and acts;
# it does not form its own opinion about whether the session is healthy.
#
# EXIT CODES:
#   0  the cycle completed — including "the breaker refused and we said so"
#   1  the cycle did not complete: config missing, state unwritable, lock lost
# =============================================================================
# ⚠ NOT `set -e`. Same reasoning as the alarm: a script whose failures must be
# recorded cannot die on the first one.
set -uo pipefail

VERSION="1"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

STATUS_URL="${GV_RELOGIN_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
ACCOUNT_FILE="${GV_RELOGIN_ACCOUNT_FILE:-/opt/rotary-phone/gv-account.conf}"
CDP_PORT="${GV_RELOGIN_CDP_PORT:-9224}"
LOCK_FILE="${GV_RELOGIN_LOCK_FILE:-${HOME}/.local/state/gv-auto-relogin.lock}"

now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log() { printf '%s gv-auto-relogin[%s]: %s\n' "$(now_utc)" "$$" "$*" >&2; }
die() { log "FATAL: $*"; exit 1; }

# shellcheck source=deploy/gv-auto-relogin-breaker.sh
. "${HERE}/gv-auto-relogin-breaker.sh" || die "could not source the breaker at ${HERE}/gv-auto-relogin-breaker.sh. Refusing to run unrestrained."

case "${1:-}" in
    --status) breaker_status; exit 0 ;;
    --reset)  breaker_reset;  exit $? ;;
    --print-config)
        # Side-effect free and runs before anything is required, so the deploy's
        # post-install gate can interrogate an unconfigured install.
        printf 'gv-auto-relogin version=%s\n' "$VERSION"
        printf '  script       %s\n' "$0"
        printf '  breaker      %s\n' "${HERE}/gv-auto-relogin-breaker.sh"
        printf '  state_file   %s (%s)\n' "$BREAKER_STATE_FILE" \
            "$([ -r "$BREAKER_STATE_FILE" ] && echo present || echo absent)"
        # ⛔ Reports READABILITY and MODE. Never the contents, never a prefix, never
        # a length — a "first two characters" convenience is how a secret ends up in
        # a deploy log.
        printf '  account_file %s (%s, mode %s)\n' "$ACCOUNT_FILE" \
            "$([ -r "$ACCOUNT_FILE" ] && echo readable || echo MISSING)" \
            "$(stat -c %a "$ACCOUNT_FILE" 2>/dev/null || echo unknown)"
        printf '  status_url   %s\n' "$STATUS_URL"
        printf '  cdp_port     %s\n' "$CDP_PORT"
        exit 0 ;;
esac

for tool in curl jq flock python3; do
    command -v "$tool" >/dev/null 2>&1 || die "${tool} is not on PATH. Refusing to run half-equipped."
done

# --- One at a time -----------------------------------------------------------
# ⛔ The 20-minute cron fires refresh-from-browser independently (§0.11) and this
# timer can fire while a previous run is mid-login. Two overlapping sign-in drives
# against one Chrome profile is exactly the traffic pattern the breaker exists to
# prevent, and it would spend the daily budget in one minute.
mkdir -p "$(dirname "$LOCK_FILE")" 2>/dev/null
exec 9>"$LOCK_FILE" || die "could not open the lock at ${LOCK_FILE}"
if ! flock -n 9; then
    log "another gv-auto-relogin run holds the lock; exiting without acting."
    exit 0
fi

# --- Consult the breaker BEFORE anything else --------------------------------
# ⛔ Before the poll, before the credential, before CDP. The breaker is not a check
# performed on the way to acting; it is the thing that decides whether acting is on
# the table at all, and putting it first means no later code path can route round it.
breaker_load
if ! breaker_may_attempt; then
    log "not attempting: ${BREAKER_REFUSAL}"
    breaker_write || exit 1
    exit 0
fi

# --- Poll: act only on Stale -------------------------------------------------
resp="$(curl -sS --max-time 10 -w $'\n%{http_code}' "$STATUS_URL" 2>/dev/null)"
if [ $? -ne 0 ]; then
    log "status poll failed; nothing to act on. The alarm reports this condition, not us."
    exit 0
fi
http="${resp##*$'\n'}"; body="${resp%$'\n'*}"
[ "$http" = "200" ] || { log "status returned http=${http}; not acting."; exit 0; }

outcome="$(printf '%s' "$body" | jq -r '.browserRefreshOutcome // "FIELD_MISSING"')"
validated_before="$(printf '%s' "$body" | jq -r '.browserSessionValidatedAt // ""')"

# ⛔ ONLY `Stale`. Not Unreachable — that means Chrome is GONE, so there is nothing
# to drive and a sign-in attempt would fail as transport, spending an attempt to
# learn what the status already said. Not FIELD_MISSING — that means the box runs a
# build older than PR #85 and we cannot read the signal at all. Not NotAttempted,
# not TornDown, and emphatically not a value we do not recognise.
if [ "$outcome" != "Stale" ]; then
    log "outcome=${outcome}; not a signed-out session. Nothing to do."
    exit 0
fi

log "outcome=Stale — the browser session is signed out. Breaker authorises an attempt."

# --- The credential, and where it may travel ---------------------------------
# ⛔ SOURCED, NEVER ECHOED, NEVER IN ARGV. Measured on this box 2026-09-09:
#   /proc/<pid>/cmdline  -r--r--r--   world-readable
#   /proc/<pid>/environ  -r--------   owner only
# `radio` is shared: beszel (a metrics agent that reads process data), avahi,
# colord and polkitd all run here under other uids, and Radio Console runs under
# OURS. So environ is a boundary against everyone but Radio Console; argv is a
# boundary against nobody. The password reaches the CDP driver ON STDIN (Task 10),
# which is neither.
if [ ! -r "$ACCOUNT_FILE" ]; then
    breaker_trip account_file_missing \
        "Auto-relogin is stopped because its credential file is missing or unreadable at ${ACCOUNT_FILE}. It is populated by the owner, on the box, by hand — no deploy creates it. Re-create it (mode 600), then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
fi
# shellcheck disable=SC1090
. "$ACCOUNT_FILE" || { breaker_trip account_file_unreadable \
    "Auto-relogin is stopped because its credential file at ${ACCOUNT_FILE} could not be sourced. Check its syntax and mode (600), then run: gv-auto-relogin.sh --reset"
    breaker_write; exit 0; }

for required in GV_ACCOUNT_EMAIL GV_ACCOUNT_PASSWORD; do
    if [ -z "${!required:-}" ]; then
        # ⚠ Names the VARIABLE, never the value. This message reaches the journal.
        breaker_trip account_file_incomplete \
            "Auto-relogin is stopped because ${required} is unset or empty in ${ACCOUNT_FILE}. Complete the file, then run: gv-auto-relogin.sh --reset"
        breaker_write || exit 1
        exit 0
    fi
done

# Task 10 continues from here.
```

**Acceptance** — lane **L**, against a stub status endpoint:

| Status endpoint serves | Required behaviour |
|---|---|
| `Succeeded` | no attempt; exit 0; journal says "not a signed-out session" |
| `Unreachable` | ⛔ **no attempt** — Chrome is gone, there is nothing to drive |
| `NotAttempted` / `TornDown` | no attempt |
| a payload with **no** `browserRefreshOutcome` | ⛔ **no attempt** — an old build is not a licence to guess |
| `"Hibernating"` (a future value) | ⛔ **no attempt** |
| connection refused | no attempt; exit 0 |
| `Stale` **and breaker TRIPPED** | ⛔ **no attempt**, and the journal quotes `BREAKER_REFUSAL` |
| `Stale`, breaker ARMED, **no account file** | breaker **TRIPS** with `account_file_missing`; exit 0 |

- ⛔ **`grep -c 'GV_ACCOUNT_PASSWORD' deploy/gv-auto-relogin.sh` counts only the two allowed uses** — the
  `for required in` loop and the stdin hand-off in Task 10. Assert that **no occurrence sits after a `-` flag,
  inside a `"$@"`, or in a `--arg`**. ⚠ This is a **source assertion**, deliberately, per §0.3: sampling
  `/proc/<pid>/cmdline` during a run *passes by missing it*, which is worse than not testing.
- ⛔ **Two concurrent runs: the second exits 0 without acting**, and the breaker's counters advance **once**.
- ⛔ **`--print-config` never prints a credential value, a prefix, or a length.** Assert the output does not
  contain the fixture password, and does not contain any substring of it longer than 3 characters.
- With a `Stale` status and an ARMED breaker, the run reaches the end of this task's code and stops there
  cleanly — the split is real, not notional.

---

#### Task 10 — The login drive and the three-way classifier — written FROM the spike · lane **L**

**Depends on:** Tasks 4, 9. Appends to `deploy/gv-auto-relogin.sh` and adds
`deploy/gv-relogin-signin.py`.

⛔ **Every selector, URL, step boundary and timeout in this task comes from Task 4's recording, cited by
artefact filename.** This plan deliberately does **not** supply them. A plausible-looking selector written from
memory of what Google's login page looks like is the same defect as a plausible-looking threshold, and it fails
in the one place where failure is expensive.

**10a — the sign-in driver.** `deploy/gv-relogin-signin.py`. ⛔ **The password arrives on stdin as JSON and is
never a parameter.**

```python
#!/usr/bin/env python3
"""Drive ONE Google sign-in through the GV bridge's existing Chrome, on its
existing profile, and report a CLASSIFIED outcome.

⛔ THE CREDENTIAL ARRIVES ON STDIN, AS JSON: {"email": "...", "password": "..."}
It is never an argv parameter and never an environment variable read here.
/proc/<pid>/cmdline is mode 0444 on this box and `radio` is shared (measured
2026-09-09). stdin is the process's own fd and reaches nobody.

⛔ IT PRINTS EXACTLY ONE LINE OF JSON TO STDOUT and nothing else, ever:
    {"class": "succeeded|credential_rejected|challenged|transport", "detail": "..."}
`detail` is a short, human-readable phrase for the breaker's reason text. It is
built from page STRUCTURE, never from page text that could contain the account
email, and never from anything typed.

⛔ THE DEFAULT CLASS IS `challenged`. See docs/plans/gv-auto-relogin.md §0.6: the
rejection detector is built from a sample of exactly ONE rejection and there will
never be a second, so the case this most often meets is one it does not recognise.
Mapping unknown to a retryable class is a one-line path to a locked account.
"""
import json
import sys

# ⛔⛔ SPIKE_REQUIRED — every constant below is filled from Task 4's recording and
# each carries the artefact filename it came from. A value here that does not cite
# a spike artefact has been guessed, and guessing is the failure mode this whole
# file is shaped around. The build MUST NOT proceed while any SPIKE_REQUIRED
# remains: deploy/tests/repro-gv-relogin.sh asserts there are none.
SIGNIN_URL      = "SPIKE_REQUIRED"   # <- spike-10-email-form.html
EMAIL_SELECTOR  = "SPIKE_REQUIRED"   # <- spike-10-email-form.html
EMAIL_SUBMIT    = "SPIKE_REQUIRED"   # <- spike-10-email-form.html
PASSWORD_SELECTOR = "SPIKE_REQUIRED" # <- spike-20-password-form.html
PASSWORD_SUBMIT   = "SPIKE_REQUIRED" # <- spike-20-password-form.html
REJECTION_MARKER  = "SPIKE_REQUIRED" # <- spike-40-rejection.html  (a STRUCTURAL
                                     #    marker: an element id or aria role, not
                                     #    a sentence Google may reword)
SUCCESS_URL_PREFIX = "SPIKE_REQUIRED"  # <- spike-30-after-submit.html
# ⚠ Timeouts are MEASURED in Task 4, not chosen here.
STEP_TIMEOUT_S  = "SPIKE_REQUIRED"   # <- the timings row of the recording


def classify(page):
    """Map an observed page state to one of the four classes.

    ⛔ READ THE ORDER. Rejection and challenge are tested FIRST and positively.
    Transport is reachable only from an explicitly-identified transport fault
    raised by the caller — never from this function. And the fallthrough is
    `challenged`, which STOPS.
    """
    if page.settled_url.startswith(SUCCESS_URL_PREFIX):
        return "succeeded", "signed in; the sign-in flow settled on the expected page"
    if page.has(REJECTION_MARKER):
        return "credential_rejected", "Google rejected the stored password"
    if page.is_challenge():
        return "challenged", f"Google presented a verification challenge ({page.challenge_kind})"
    # ⛔ THE DEFAULT. Not transport. Not "retry once to be sure."
    return "challenged", (
        "the sign-in flow ended in a state this actuator does not recognise; "
        "treated as a challenge and stopped, because an unrecognised outcome is "
        "not evidence that retrying is safe"
    )


def main():
    try:
        creds = json.load(sys.stdin)
    except Exception:
        # ⚠ Not a transport failure — a caller that cannot hand us a credential is
        # a configuration fault, and configuration faults must not be retried into
        # a rate limit.
        print(json.dumps({"class": "challenged",
                          "detail": "the credential could not be read from stdin"}))
        return 0
    # ... drive the flow using Task 3's Session, filling EMAIL then PASSWORD,
    #     waiting for each step boundary, then classify(). Every fill uses
    #     Input.insertText / Runtime.evaluate over the WEBSOCKET — never a shell,
    #     never a subprocess, never a temp file.
    #
    # ⛔ ONE PASS. There is no loop here. If a step boundary does not arrive within
    # STEP_TIMEOUT_S the driver reports its class and exits; it does not re-submit,
    # re-navigate or re-type. Retrying inside the driver would be a retry the
    # breaker cannot see or count.
    ...


if __name__ == "__main__":
    sys.exit(main())
```

**10b — the hand-off, in the actuator.** Appended to `deploy/gv-auto-relogin.sh`:

```bash
# --- Drive the sign-in -------------------------------------------------------
# ⛔ The credential goes down a PIPE. Note what is NOT here: no --password, no
# GV_ACCOUNT_PASSWORD="$x" python3 ..., no temp file, no here-string that would
# show in `ps`. jq builds the JSON so a password containing quotes, backslashes or
# newlines cannot break the encoding — and jq reads its inputs with --arg, which is
# argv, so ⛔ the VALUE is fed to jq on stdin too, via --rawfile on /dev/stdin.
breaker_record_credential_attempt

signin_json="$(
    printf '%s\0%s\0' "$GV_ACCOUNT_EMAIL" "$GV_ACCOUNT_PASSWORD" \
    | jq -Rs --arg port "$CDP_PORT" --arg tid "$TARGET_ID" \
         'split(" ") | {email: .[0], password: .[1], port: ($port|tonumber), target: $tid}'
)"

classified="$(printf '%s' "$signin_json" \
    | python3 "${HERE}/gv-relogin-signin.py" 2>>/dev/null)"
signin_rc=$?
unset signin_json GV_ACCOUNT_PASSWORD

if [ "$signin_rc" -ne 0 ] || [ -z "$classified" ]; then
    # ⚠ The driver dying is NOT automatically transport. It could be a driver bug,
    # a python fault, or a killed process — none of which is evidence that another
    # attempt is safe. Treated as terminal, consistent with §0.6.
    breaker_trip driver_failed \
        "Auto-relogin is stopped because its sign-in driver exited abnormally and reported no outcome. Nothing is known about whether a sign-in was attempted or how Google responded, so no further attempt can be authorised. Read: journalctl --user -u gv-auto-relogin -n 100 — then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
fi

class="$(printf '%s' "$classified" | jq -r '.class // "challenged"')"
detail="$(printf '%s' "$classified" | jq -r '.detail // "no detail reported"')"
log "sign-in class=${class}"

case "$class" in
  succeeded)
      : ;;  # Task 11 verifies. A driver saying "succeeded" is not the outcome.
  credential_rejected)
      # ⛔ ONE REJECTION. NOT A RETRY. Spec §6: retrying a wrong password is the
      # single most reliable way to get an account locked, and a rejected
      # credential is never transient.
      breaker_trip credential_rejected \
          "Auto-relogin is STOPPED PERMANENTLY: Google rejected the stored password. This is never a transient failure and it will not be retried — retrying a rejected credential is the most reliable way to get the account locked. A human must check the password in /opt/rotary-phone/gv-account.conf, re-login by hand at voice.google.com, and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
  challenged)
      # ⛔ A challenge means Google ALREADY considers this suspicious. Retrying
      # deepens it. Spec §6, spec §10 decision 3 (default: permanently).
      breaker_trip challenged \
          "Auto-relogin is STOPPED PERMANENTLY: Google presented a verification challenge instead of signing in (${detail}). A challenge means Google already treats this sign-in as suspicious, so it will not be retried. A human must re-login by hand at voice.google.com in the box's Chrome, and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
  transport)
      # The ONLY safely retryable class, and it spends no credential budget.
      breaker_record_transport_failure
      log "transport failure (${detail}); no credential was offered to Google. Will retry within the rate limit."
      breaker_write || exit 1
      exit 0 ;;
  *)
      breaker_trip unclassified \
          "Auto-relogin is STOPPED PERMANENTLY: its sign-in driver returned an outcome class this script does not recognise. An unrecognised outcome is not evidence that retrying is safe. A human must read the journal and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
esac
```

**Acceptance** — lane **L**, against a **local** Chrome serving fixture pages captured by Task 4 (never against
Google):

- ⛔ **`grep -c SPIKE_REQUIRED deploy/gv-relogin-signin.py` is 0**, and every constant carries a comment naming
  the spike artefact it came from. ⚠ The harness asserts the count; a reviewer asserts the citations.
- ⛔ **The default is `challenged`.** Serve a page matching **nothing** — not the success URL, not the rejection
  marker, not a challenge — and assert `class == "challenged"`. Then **change the default to `transport` and
  watch this case fail.** A default nobody has watched fail is the one line that would lock the account.
- Serving the captured rejection fixture yields `credential_rejected`, and the actuator's breaker is `TRIPPED`
  with that reason, and ⛔ **a second run is refused.**
- Serving the captured success fixture yields `succeeded` and does **not** trip.
- ⛔ **The driver makes exactly ONE pass.** Serve a page that never produces the next step boundary; assert the
  driver exits within `STEP_TIMEOUT_S` and that the fixture server recorded **one** navigation, not two.
- ⛔ **No credential in argv, proven at the source:** `grep -nE 'python3 .*(--password|--pass|\$GV_ACCOUNT_PASSWORD)' deploy/gv-auto-relogin.sh`
  is empty, and the only occurrence of `GV_ACCOUNT_PASSWORD` outside the `for required` loop is inside the
  `printf | jq | python3` pipeline.
- `unset GV_ACCOUNT_PASSWORD` runs on **every** path out of the sign-in block, including the terminal ones.

---

#### Task 11 — Verify by outcome: navigate, refresh, and the `validatedAt` delta · lane **L**

**Depends on:** Task 10. Appends to `deploy/gv-auto-relogin.sh`.

⛔ **`succeeded` from the driver is a mechanism report. This task produces the outcome.** §0.7 and §0.8.

```bash
# --- Verify by OUTCOME, in two stages ----------------------------------------
# ⛔ STAGE 1 — the forced navigation. KNOWN-ISSUES.md:16-22: the tab's title and URL
# are STALE CACHED RENDERS that lie about login state, and on 2026-09-09 this
# session and Radio Console both read that tab as evidence, in OPPOSITE directions,
# and both were wrong. A forced navigation is the only reading that means anything.
#
# ⚠ AND IT MUST BE READ FROM THE TARGET, NOT FROM THE TARGET LIST. Measured
# 2026-09-09: a parked workspace.google.com page sits in /json/list while the
# session is perfectly healthy. "Is there a workspace tab?" answers YES on a good
# session — a check that runs, passes, and answers a different question.
landed="$(python3 "${HERE}/../tools/gv-cdp.py" navigate \
            --port "$CDP_PORT" --target "$TARGET_ID" \
            --url 'https://voice.google.com/u/0/voicemail' 2>/dev/null)"
case "$landed" in
  https://workspace.google.com/products/voice/*)
      # We are NOT signed in, whatever the driver said. Do NOT post cookies.
      breaker_trip verification_failed \
          "Auto-relogin is STOPPED PERMANENTLY: the sign-in driver reported success, but a forced navigation to voice.google.com redirected to the signed-out marketing page. The session is not signed in and the driver's report cannot be trusted. A human must re-login by hand at voice.google.com in the box's Chrome, and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
esac
log "forced navigation settled on ${landed} — not the signed-out redirect."

# ⭐ STAGE 2 — GOOGLE ADJUDICATES, and this is the actual outcome. The service
# adopts the candidate in memory, probes it live against Google, and persists ONLY
# on success (GVApiAdapter.TryValidateCandidateAsync). A false positive at stage 1
# is therefore SAFE: since the 2026-09-08 hardening a rejected set leaves the good
# one intact and logs "REJECTED ... The working on-disk set was NOT overwritten".
curl -sS --max-time 30 -X POST "${STATUS_URL%/status}/cookies/refresh-from-browser" \
     -H 'Content-Type: application/json' --data-binary '{}' >/dev/null 2>&1

after="$(curl -sS --max-time 10 "$STATUS_URL" 2>/dev/null)"
outcome_after="$(printf '%s' "$after" | jq -r '.browserRefreshOutcome // "FIELD_MISSING"')"
validated_after="$(printf '%s' "$after" | jq -r '.browserSessionValidatedAt // ""')"

# ⛔ BOTH CONDITIONS, AND THE SECOND IS WHY. `Succeeded` alone does not prove WE did
# it: the 20-minute cron fires refresh-from-browser independently and can produce a
# Succeeded between our poll and our read (§0.11). Requiring browserSessionValidatedAt
# to have MOVED ACROSS OUR OWN POST is what ties the outcome to this run.
if [ "$outcome_after" = "Succeeded" ] && [ "$validated_after" != "$validated_before" ]; then
    breaker_record_success
    breaker_write || exit 1
    log "RESTORED: browserRefreshOutcome=Succeeded and browserSessionValidatedAt moved ${validated_before} -> ${validated_after}."
    exit 0
fi

breaker_trip verification_failed \
    "Auto-relogin is STOPPED PERMANENTLY: it drove a sign-in and believed it worked, but the service did not confirm it — browserRefreshOutcome is ${outcome_after} and browserSessionValidatedAt did not move. Google did not accept cookies harvested from the browser. The previously-working cookie set was NOT overwritten. A human must re-login by hand at voice.google.com in the box's Chrome, and then run: gv-auto-relogin.sh --reset"
breaker_write || exit 1
exit 0
```

**Acceptance** — lane **L**, against stub status + a local Chrome:

| Case | Stub serves | Required |
|---|---|---|
| **true success** | outcome `Succeeded`, `validatedAt` **changed** across the POST | breaker records success, stays ARMED, exit 0 |
| ⛔ **the cron's success, not ours** | outcome `Succeeded`, `validatedAt` **unchanged** | ⛔ **breaker TRIPS** `verification_failed` — the check must not accept someone else's work as its own |
| ⛔ **driver lied** | forced navigation lands on `workspace.google.com/products/voice/` | ⛔ **no `refresh-from-browser` POST is made at all**, and the breaker trips |
| **Google refused the cookies** | outcome stays `Stale` after the POST | breaker trips `verification_failed` |
| **service died mid-verify** | status unreachable after the POST | breaker trips `verification_failed` |

- ⛔ **The second row is the load-bearing one**, and it is the row a naive implementation gets wrong. Prove it
  by removing the `validated_after != validated_before` clause and watching the case fail.
- ⛔ **The third row asserts an ABSENCE that must be positively observed:** the stub records every request it
  receives, and the assertion is that its log contains **zero** `refresh-from-browser` entries — not that our
  code "did not reach" the POST.
- The message on every failure path names the state, the remedy, and `--reset`. ⚠ None of them contains the
  password, the email, or a substring of either — asserted against the journal.

---

#### Task 12 — The actuator harness · lane **L**

**Depends on:** Tasks 9–11. Create `deploy/tests/repro-gv-relogin.sh`, in the shape of
`deploy/tests/repro-gv-session-alarm.sh` (stub status endpoint, fixture pages, assert on what the stubs
recorded).

Additions specific to this arc:

```bash
echo "=== ⛔ the credential never reaches argv — asserted at the SOURCE ==="
# ⚠ SOURCE, NOT SAMPLING, and the spec says otherwise. Spec §9 acceptance 6 asks for
# "inspecting /proc/<pid>/cmdline during a run". repro-gv-session-alarm.sh:224-227
# already measured that instrument as unsound in this exact situation: the call is
# far too short-lived to catch by sampling, and "a sampling test here would pass by
# missing it, which is worse than no test at all."
check "no --password style flag anywhere" "0" \
      "$(grep -cE -- '--password|--pass[ =]|--credential' "$ACTUATOR" "$SIGNIN")"
check "the password reaches python3 only through a PIPE" "1" \
      "$(grep -cE 'printf .*\| *jq .*\| *python3' "$ACTUATOR")"
check "no here-string carrying the password" "0" \
      "$(grep -cE '<<< *"\$GV_ACCOUNT_PASSWORD' "$ACTUATOR")"
check "the driver reads stdin, not argv or env" "1" \
      "$(grep -c 'json.load(sys.stdin)' "$SIGNIN")"
check "the driver has no argparse entry for a secret" "0" \
      "$(grep -ciE 'add_argument.*(pass|secret|cred)' "$SIGNIN")"

echo "=== ⛔ the password is not in the journal, the state file, or any artefact ==="
FIXTURE_PW='ZZ-fixture-password-ZZ'
# ... run every failure path with that password in the fixture account file ...
check "password absent from the journal"     "0" "$(grep -c "$FIXTURE_PW" "$WORK/err.txt")"
check "password absent from breaker state"   "0" "$(grep -c "$FIXTURE_PW" "$GV_RELOGIN_STATE_FILE")"
check "password absent from --print-config"  "0" "$(bash "$ACTUATOR" --print-config | grep -c "$FIXTURE_PW")"
check "password absent from --status"        "0" "$(bash "$ACTUATOR" --status | grep -c "$FIXTURE_PW")"
# ⚠ And a substring check, because a "first four characters for debugging" is how
# this leaks in practice.
check "no 4-char substring of the password leaks" "0" \
      "$(grep -c "${FIXTURE_PW:0:4}" "$WORK/err.txt")"
```

**Acceptance:**

- `bash deploy/tests/repro-gv-relogin.sh` exits **0**, printing a `PASS` for every case in Tasks 9–11.
- ⛔ **The breaker harness (Task 6) and this one both run, and neither subsumes the other.** Task 6 tests the
  breaker with no actuator; this one tests the actuator's *use* of it. A single merged harness would let an
  actuator change quietly weaken a breaker case.
- The harness leaves no listener, no temp directory, and no `gv-account.conf` fixture behind.

---

### Phase 5 — the alarm's one new condition

---

#### Task 13 — `relogin_unavailable`, on its own track · lane **L**

**Depends on:** Task 5 (for the state file's shape). Modifies `deploy/gv-session-alarm.sh`.

⛔ **PR #85's session-condition track is byte-unchanged.** §0.9: putting this into the existing
`LAST_POSTED_CONDITION` case would make the alarm go silent on a genuine session death while the breaker is
tripped — the alarm failing in the state it exists for, *because* the automation broke.

⛔ **And the alarm still detects nothing.** §0.10: it reads the breaker's `BREAKER_STATE` and quotes the
breaker's own `BREAKER_REASON_TEXT`. It does not decide what a tripped breaker means, exactly as it does not
decide what `Stale` means.

**13a — a second track in the state file.** Add to the alarm's declared state and to `write_state`:

```bash
# ⛔ A SECOND, INDEPENDENT TRACK — not a value of LAST_POSTED_CONDITION.
# The alarm posts on transition of a single condition string. If the breaker's state
# competed in that same case, then once it tripped the condition would stop changing
# and a genuine session death that followed would produce NO MESSAGE — the alarm
# going mute in the state it exists for, correlated with the automation breaking.
# See docs/plans/gv-auto-relogin.md §0.9.
LAST_POSTED_RELOGIN_STATE=""
```

…and the matching `printf 'LAST_POSTED_RELOGIN_STATE=%q\n' "$LAST_POSTED_RELOGIN_STATE"` in `write_state`.

**13b — read the breaker, quote it, and post on transition.** Appended **after** the existing decide-and-post
block and **before** the dead-man:

```bash
# --- The one new condition: auto-relogin unavailable ---------------------------
# ⛔ TRANSPORT, NOT DETECTION. THIS SCRIPT DETECTS NOTHING stays true: the breaker
# writes BREAKER_REASON_TEXT at the moment it trips, in words aimed at a human, and
# this block quotes it verbatim — the same relationship this script already has with
# GVApiAdapter.cs's strings, and it gets the same drift guard (Task 14).
#
# ⚠ ABSENT IS NOT HEALTHY, and it is not unhealthy either. If auto-relogin is not
# installed on this box there is no breaker and nothing to report; the alarm must
# not invent a condition out of a missing file. But a breaker file that EXISTS and
# is UNREADABLE is a fault, and the breaker itself already reports that state as
# TRIPPED with its own reason text — so this block does not need to decide.
RELOGIN_STATE_FILE="${GV_ALARM_RELOGIN_STATE_FILE:-${HOME}/.local/state/gv-auto-relogin.state}"

relogin_state="not_installed"
relogin_reason_text=""
if [ -e "$RELOGIN_STATE_FILE" ]; then
    relogin_state="$(grep -m1 '^BREAKER_STATE=' "$RELOGIN_STATE_FILE" 2>/dev/null \
                     | cut -d= -f2- | tr -d "'\"")"
    relogin_reason_text="$(grep -m1 '^BREAKER_REASON_TEXT=' "$RELOGIN_STATE_FILE" 2>/dev/null \
                     | cut -d= -f2- | sed "s/^'//; s/'$//")"
    [ -n "$relogin_state" ] || relogin_state="unreadable"
fi

if [ "$relogin_state" = "TRIPPED" ]; then
    if [ "$LAST_POSTED_RELOGIN_STATE" != "TRIPPED" ]; then
        # Reply into the open incident if there is one, so the owner reads
        # "the session is dead" and "and automation will not fix it" in one place.
        # Open our own thread if there is not — a tripped breaker on a HEALTHY
        # session is still an account-level event the owner must act on.
        relogin_thread="$INCIDENT_THREAD_KEY"
        if [ -z "$relogin_thread" ]; then
            relogin_thread="${SOURCE_NAME}-gv-relogin-$(date -u +%Y%m%dT%H%M%SZ)"
        fi
        post_notify "alert" \
            "[${SOURCE_NAME}] GV auto-relogin — stopped, needs a human" \
            "$(now_utc) · relogin_unavailable
Automatic re-login has stopped and will not resume on its own. In the actuator's own words:

> ${relogin_reason_text}

⚠ This is reported **separately from** the session's own state above: a stopped actuator and a dead session
are two different facts, and either can be true without the other." \
            "gv-auto-relogin.sh --status ; then --reset once the account is fixed" \
            "${SOURCE_NAME}-gv-relogin-unavailable" \
            "$relogin_thread"
        if [ "$NOTIFY_FAILED" -eq 0 ]; then
            LAST_POSTED_RELOGIN_STATE="TRIPPED"
        fi
    else
        log "auto-relogin still TRIPPED; already posted, nothing to say."
    fi
elif [ "$relogin_state" = "ARMED" ] && [ "$LAST_POSTED_RELOGIN_STATE" = "TRIPPED" ]; then
    # A human cleared it. Quiet lane, and it threads under the alert it closes.
    post_notify "info" \
        "[${SOURCE_NAME}] GV auto-relogin — re-armed" \
        "$(now_utc) · RESOLVED
The auto-relogin breaker has been re-armed by a human. Automatic re-login is available again.
Action: none." \
        "" \
        "${SOURCE_NAME}-gv-relogin-rearmed-$(date -u +%Y%m%d)" \
        "${INCIDENT_THREAD_KEY:-${SOURCE_NAME}-gv-relogin-unavailable}"
    [ "$NOTIFY_FAILED" -eq 0 ] && LAST_POSTED_RELOGIN_STATE="ARMED"
else
    log "auto-relogin state=${relogin_state}; nothing to post."
fi
```

**Acceptance** — lane **L**, extending `deploy/tests/repro-gv-session-alarm.sh`:

| Case | Setup | Required |
|---|---|---|
| ⛔ **not installed** | no breaker state file | **nothing posted.** The alarm must not invent a condition from a missing file |
| **trips once** | write a TRIPPED state file | exactly **one** `alert`, quoting the breaker's reason text verbatim |
| **stays tripped** | five more runs, unchanged | ⛔ **no further message** — transition only |
| ⛔ **THE LOAD-BEARING ONE** | TRIPPED breaker **and** a session that goes `Succeeded` → `Stale` | ⛔ **both** messages arrive: the session `alert` **and** the relogin `alert`. Prove PR #85's track still fires while the relogin track is latched |
| **re-armed** | flip the file to ARMED | one quiet `info`, threaded under the alert it closes |
| **threading** | an incident already open | the relogin alert carries the **same `thread_key`** as the session alert |

- ⛔ **Row 4 is the case §0.9 exists for.** Implement it the wrong way first — put `relogin_unavailable` into
  the main `case` — and watch the session `alert` **fail to arrive**. That is the defect, demonstrated once so
  its shape is known.
- ⛔ **PR #85's session track is byte-unchanged:** `git diff` on `gv-session-alarm.sh` shows only additions, and
  every pre-existing line in the decide-and-post block is untouched. Assert with
  `git diff --stat` and a reviewer reading the hunk boundaries.
- Every existing case in `repro-gv-session-alarm.sh` still passes, unmodified.

---

#### Task 14 — The breaker-copy drift guard · lane **U**

**Depends on:** Tasks 5, 13. Extend `src/RotaryPhoneController.GVBridge.Tests/Alarm/AlarmCopyDriftTests.cs`.

⚠ **The alarm now quotes a second source.** `BREAKER_REASON_TEXT` lives in `gv-auto-relogin-breaker.sh` and in
`gv-auto-relogin.sh`, and the alarm reproduces it into a delivered message. That is another quotation with
nothing connecting it to its source — the same defect the existing test was written for, one file over.

⛔ **A C# test, not an MSBuild `Exec`.** `AlarmCopyDriftTests.cs`'s own remarks record why: the plan's original
`Condition="IsOSPlatform(Linux)"` wiring *"would have run NOWHERE"* — no CI in this repo, the owner builds on
Windows, the box has no SDK. Follow the shipped precedent, not the plan that preceded it.

```csharp
    /// <summary>
    /// Phrases the alarm reproduces from the auto-relogin breaker's own reason text.
    /// The alarm is TRANSPORT: it quotes rather than derives, which is the only reason
    /// "THIS SCRIPT DETECTS NOTHING" survives auto-relogin. A quotation that has
    /// silently stopped matching its source attributes words to the actuator that the
    /// actuator does not say, inside a message an operator will act on at the worst
    /// possible moment.
    /// </summary>
    private static readonly string[] BreakerQuotes =
    [
        "gv-auto-relogin.sh --reset",
        "retrying a rejected credential is the most reliable way to get the account locked",
        "A challenge means Google already treats this sign-in as suspicious",
        "The previously-working cookie set was NOT overwritten",
    ];

    [Fact]
    public void TheAlarmsReloginCopyStillMatchesTheActuatorsOwnWords()
    {
        var root = RepoRoot();
        var actuator = Path.Combine(root, "deploy", "gv-auto-relogin.sh");
        var breaker  = Path.Combine(root, "deploy", "gv-auto-relogin-breaker.sh");

        // ⛔ Test for PRESENCE, not absence. If auto-relogin has not shipped yet these
        // files do not exist, and the test SKIPS EXPLICITLY rather than passing —
        // a guard that goes quiet when its subject is missing is the defect this repo
        // corrected in its deploy gate on 2026-09-09.
        Assert.True(File.Exists(actuator) || File.Exists(breaker),
            "neither auto-relogin file was found; if auto-relogin has shipped this guard is broken, " +
            "and if it has not, delete this test rather than letting it pass by absence");

        var corpus = Flatten(File.ReadAllText(actuator)) + Flatten(File.ReadAllText(breaker));
        var missing = BreakerQuotes.Where(q => !corpus.Contains(q)).ToList();
        Assert.True(missing.Count == 0,
            "the alarm quotes phrases the actuator no longer says: " + string.Join(" | ", missing));
    }
```

**Acceptance:**

- `dotnet test` (Windows SDK — §1.1) is green, including the existing
  `TheAlarmScriptQuotesTheServiceVerbatim_AndBothCopiesStillAgree`.
- ⛔ **Proven by breaking it.** Change one word in the breaker's reason text, run `dotnet test`, watch it fail
  **naming the phrase**, restore it. A guard nobody has seen fail is a guard nobody knows is wired up.
- ⛔ The existing `Quotes` array is **unchanged**. This test adds a second corpus; it does not renegotiate the
  first.

---

### Phase 6 — install, deploy, and the on-box gate

---

#### Task 15 — A narrow auto-relogin installer, and a third drift group · lane **L**

**Depends on:** Tasks 5, 9–11. Create `deploy/install-gv-auto-relogin.sh`, mode 755, modelled on
`deploy/install-gv-session-alarm.sh` (which already carries the `install_atomic` rationale and the
"why not `setup-gvbridge.sh`" header — reuse both arguments, do not re-derive them).

Differences that matter:

```bash
# ⛔ THE TIMER IS NOT ENABLED BY DEFAULT, AND FOR A STRONGER REASON THAN THE ALARM'S.
# The alarm's installer defers enabling because a missing token would make it fail
# loudly 288 times a day. This one defers because ENABLING IT IS THE ACT OF ARMING AN
# AUTOMATION THAT TYPES A PASSWORD AT GOOGLE. That is owner gate G2 (spec §3: if
# sessions still die hourly, do not ship the actuator — fix the death rate first).
# --enable is a separate, deliberate, gated step.

# ⛔ AND IT REFUSES --enable ON THREE COUNTS, each of which would otherwise produce a
# silent failure:
#   1. no /opt/rotary-phone/gv-account.conf  -> the actuator would trip on its first
#      Stale and the owner would learn about it from an alarm that may not exist
#   2. gv-account.conf is not mode 600       -> a credential readable by Radio Console
#   3. no ~/bin/gv-session-alarm.sh          -> ⛔ THE ESCALATION PATH IS ABSENT. §0.1.
#      An actuator whose breaker trips into silence is worse than no actuator.
if [ "$ENABLE_TIMER" -eq 1 ]; then
    [ -r /opt/rotary-phone/gv-account.conf ] \
        || fail "refusing --enable: /opt/rotary-phone/gv-account.conf is missing. The owner populates it on the box by hand; no deploy creates it."
    mode="$(stat -c %a /opt/rotary-phone/gv-account.conf)"
    [ "$mode" = "600" ] \
        || fail "refusing --enable: /opt/rotary-phone/gv-account.conf is mode ${mode}, not 600. This box is shared with Radio Console under the same uid."
    [ -x "${HOME}/bin/gv-session-alarm.sh" ] \
        || fail "refusing --enable: the GV session alarm is not installed at ${HOME}/bin/gv-session-alarm.sh. It is the ONLY escalation path this actuator has — a breaker that trips with no alarm installed stops silently, which is worse than no automation. Install and prove the alarm first (docs/plans/gv-session-alarm.md Tasks 5, 16, 17)."
    systemctl --user enable --now gv-auto-relogin.timer
fi

# ⛔ THE BREAKER STARTS TRIPPED, AND THE INSTALLER DOES NOT ARM IT.
# breaker_load treats an absent state file as TRIPPED (fail closed), so a fresh
# install cannot attempt anything until a human runs --reset. That is deliberate:
# arming an automation that types a password should be a human act with a name on
# it, not a side effect of a deploy.
log "breaker state: $("${BIN_DIR}/gv-auto-relogin.sh" --status | head -1)"
log "The breaker starts TRIPPED. Arm it deliberately with: gv-auto-relogin.sh --reset"
```

**Units.** `deploy/systemd/gv-auto-relogin.{service,timer}`, in the alarm's shape:

```ini
# gv-auto-relogin.timer
[Timer]
# ⚠ 5 minutes is the ALARM'S cadence, reused rather than newly chosen. This plan
# invents no numbers. And the effective latency is dominated by something else
# entirely: browserRefreshOutcome only becomes `Stale` when the 20-minute cron
# attempts a refresh and Google refuses it, so polling faster than that buys
# nothing.
OnBootSec=5min
OnUnitActiveSec=5min
AccuracySec=30s
# ⚠ Persistent=false. A missed window must NOT fire a burst of catch-up runs at
# boot: each would consult the breaker, and a burst is precisely the traffic
# pattern the rate limit exists to prevent.
Persistent=false
Unit=gv-auto-relogin.service
[Install]
WantedBy=timers.target
```

**Drift group.** Add a `relogin` group to `deploy/check-installed-drift.sh`'s `case "$GROUP"`, and call it from
`Deploy-ToLinux.ps1` beside the existing `alarm` group — ⚠ **non-fatally**, like the `bridge` group, because a
missing actuator must not abort a deploy that is otherwise fine.

**Acceptance** — lane **L**, fake `HOME`, no box:

- The four files install at their stated modes; `--enable` is **not** the default.
- ⛔ `--enable` refuses, **non-zero and saying which**, for each of the three counts, tested one at a time.
- ⛔ **A fresh install's breaker reports `TRIPPED`**, and a `Stale` status produces **no attempt** until a human
  `--reset`.
- `check-installed-drift.sh --group relogin` passes the same six-case matrix as the `alarm` group, including
  the **shipped-stale** row that a two-link check cannot see.
- ⛔ Running the installer leaves `~/bin/gv-session-alarm.sh` and `~/bin/gv-bridge-ensure.sh`
  **byte-identical** — this installer touches only its own four files.

---

#### Task 16 — Deploy, and prove it is INSTALLED · lane **B**

**Depends on:** Task 15. No new code. This is the on-box observation, and it is **not** gated on G2 — installing
a disabled actuator with a tripped breaker is safe and is the resting state Task 1 describes.

```bash
pwsh deploy/Deploy-ToLinux.ps1          # a normal deploy, no special flags

# On the box — the INSTALLED artefacts, never the repo
sha256sum ~/bin/gv-auto-relogin.sh /opt/rotary-phone/deploy/gv-auto-relogin.sh
systemctl --user list-unit-files 'gv-auto-relogin.*'      # <- list-unit-files, NOT list-timers --all
~/bin/gv-auto-relogin.sh --print-config
~/bin/gv-auto-relogin.sh --status
```

**Acceptance:**

- The sha256 pair matches; `list-unit-files` shows both units **disabled**.
- ⚠ **`list-unit-files`, not `list-timers --all`.** `gv-session-alarm.md` §7.2 measured that `--all` does **not**
  list a disabled timer that has never started, so the plan's original instrument *"would have failed on a
  correct install."* Use the one that was measured, not the one that was reasoned about.
- `--print-config` runs **from the installed path** and prints its resolved configuration — the check a checksum
  cannot make, catching a bad mode, a partial copy, or the right name over the wrong file.
- `--status` reports **TRIPPED**.
- ⛔ **`/opt/rotary-phone/gv-account.conf` survives the deploy** if the owner has already created it. Record its
  sha256 and mode **before and after**. ⚠ If the deploy took the **rsync** branch, say so explicitly — that is
  the first live exercise of §0.2's exclusion, and it is the branch that deletes.
- ⛔ `~/bin/gv-bridge-ensure.sh` is byte-identical to before, and `gv-bridge-watchdog.timer` is still `active`.

---

#### Task 17 — ⛔ The forced-failure acceptance runs · lane **T**, gated on **G2** and Task 2

**Depends on:** Task 16, ⛔ **gate G2**, and ⛔ **Task 2 passing**. Spec §9 acceptances 1–7.

⛔ **Every one is observed as a delivered message, a read-back breaker state, or a moved `validatedAt`. Not a
log line, not an exit code, not the script's own report.**

Announce to the owner before starting — 17a and 17b deliberately break the phone's re-derivation floor for a
few minutes, and 17c spends a real credential attempt on a real account.

**17a — a wrong password stops everything, exactly once (acceptance 2).** ⭐ **The single most important run in
this arc.**

```bash
# Owner temporarily replaces the password in gv-account.conf with a wrong one.
# ⛔ ONE run, by hand, not by the timer.
sudo -u mmack systemctl --user stop gv-auto-relogin.timer
# ... manufacture a Stale session (17b's sign-out), then:
~/bin/gv-auto-relogin.sh; echo "exit=$?"
~/bin/gv-auto-relogin.sh --status
# ⛔ AND THE ASSERTION THAT MATTERS — run it AGAIN:
~/bin/gv-auto-relogin.sh; echo "exit=$?"
~/bin/gv-auto-relogin.sh --status
```

Required, all four:
1. `--status` reports **TRIPPED**, reason `credential_rejected`.
2. ⛔ **`credential today` reads `1/3`, not `2/3`.** The second run made **no attempt**. *This is spec
   acceptance 2's real content: verified by the attempt count in the breaker file, not by reading the code.*
3. ⛔ **A message ARRIVES** in the chat channel at severity `alert`, titled `GV auto-relogin — stopped, needs a
   human`, quoting the breaker's reason text. Screenshot it.
4. The owner restores the correct password and runs `--reset`; ⛔ **the counter still reads `1/3`** — a reset
   does not hand back a budget.

**17b — a challenge stops and escalates without retrying (acceptance 3).** ⚠ **A real challenge cannot be
manufactured on demand.** Simulate it at the boundary that is honest: point `GV_RELOGIN_SIGNIN_DRIVER` at a
stub that emits `{"class":"challenged",...}`, so the *actuator's* handling is what is tested rather than
Google's behaviour.

Required: TRIPPED with reason `challenged`, **one** attempt recorded, a delivered `alert`, and ⛔ **a second run
makes no attempt**. ⚠ State plainly in the PR body that this tests the actuator's response to a challenge and
**not** whether Google issues one — that question is Task 4's and has only one real sample.

**17c — end-to-end restore (acceptance 1).** ⭐ The one that proves the feature.

```bash
BEFORE=$(curl -s localhost:5004/api/gvbridge/status | jq -r .browserSessionValidatedAt)
# Owner signs the box's Chrome out (as in Task 4). Wait for the 20-minute cron to
# turn browserRefreshOutcome to Stale — or force it with one refresh-from-browser.
~/bin/gv-auto-relogin.sh; echo "exit=$?"
curl -s localhost:5004/api/gvbridge/status | jq --arg b "$BEFORE" '{browserRefreshOutcome, before:$b, after:.browserSessionValidatedAt}'
```

Required: `browserRefreshOutcome` is `Succeeded` **and** `browserSessionValidatedAt` has moved **from the
recorded `BEFORE`** — ⛔ not merely "is recent", because the 20-minute cron produces recent values on its own
(§0.8). And **no human touched the browser** between the sign-out and the check.

**17d — a transport failure retries within the rate limit and spends no credential budget (acceptance 4).**

```bash
GV_RELOGIN_CDP_PORT=9299 ~/bin/gv-auto-relogin.sh   # nothing listens there
~/bin/gv-auto-relogin.sh --status
```
Required: still **ARMED**; `transport today` incremented; ⛔ **`credential today` unchanged**; and a second
immediate run is refused by the hourly spacing.

**17e — the credential is absent from the deploy archive and survives a deploy (acceptance 5).** Covered in
lane L by Task 8; here it is confirmed against the box, ⚠ **naming which branch the deploy took**.

**17f — no password anywhere (acceptance 6).**
⛔ **Asserted at the source and against the artefacts, not by sampling `/proc`** — §0.3, and the repo's own
prior measurement. Grep the journal, the breaker state file, `--status`, `--print-config` and the systemd unit
for the real password and for any four-character substring of it. ⚠ Do this with the owner driving, so nobody
but the owner ever holds the string being searched for.

**17g — killing the relogin timer still leaves the alarm reporting staleness (acceptance 7).**

```bash
systemctl --user stop gv-auto-relogin.timer
systemctl --user disable gv-auto-relogin.timer
# ... manufacture a Stale session ...
systemctl --user start gv-session-alarm.service
```
Required: ⛔ **the session `alert` is DELIVERED** with auto-relogin entirely absent. ⭐ This is spec §7's whole
claim — *"the value of the alarm is that it still works when the actuator does not"* — and it is the only run
that tests it.

**Acceptance for the task as a whole:**

- Seven results, each with the **delivered artefact**: a screenshot, a breaker `--status` read-back, or a
  `validatedAt` delta.
- ⛔ **The account is left in a known-good state:** correct password in `gv-account.conf` at mode 600, session
  signed in, breaker in whatever state the owner chooses **deliberately**, and `gv-bridge-watchdog.timer`
  `active`.
- ⛔ **Total real credential attempts across the whole task are counted and stated** in the PR body. 17a spends
  one wrong and one right; 17c spends one right. ⚠ If the count exceeds four, stop and tell the owner before
  continuing — that is the rate this arc exists to bound, and the acceptance runs are not exempt from it.

---

### Phase 7 — record

---

#### Task 18 — Write back what was learned, and announce nothing that did not ship · lane **L**

**Depends on:** Tasks 4, 17.

**18a — fold the spike's findings into the spec.** Spec §8 says the assumption is untested. After Task 4 it is
tested. Append a `## 11. What the spike found` section to the spec — **additively**, annotating rather than
rewriting, in the pattern of `gv-session-alarm.md` §7. If the spike found a challenge, this section is the
document's most important one and it says the design does not work.

**18b — one row in the boundary doc's Change Log**, and only if something shipped to the box.

```markdown
| 2026-09-XX | RotaryPhone session | **No BT/audio change; hci0/hci1 ownership, profiles and WirePlumber configs untouched.** A new user timer `gv-auto-relogin.timer` drives an **automatic Google re-login** against the GV bridge's **existing** Chrome on `hci`-unrelated CDP port 9224, **same profile, no new browser, nothing killed** — the kiosk profile is never touched and `CookieRetriever`'s profile-scoped kill (PR #85) is unchanged. ⛔ **It is bounded by a circuit breaker that stops permanently on one credential rejection or one challenge**, and its only escalation path is the existing `gv-session-alarm`. ⚠ **Merged ≠ deployed ≠ INSTALLED:** confirm with `ssh mmack@radio "~/bin/gv-auto-relogin.sh --status"` before assuming it is running. |
```

⛔ **No row for anything that did not ship.** `gv-session-alarm.md` Task 18c: the Change Log records changes
that shipped; a row announcing a change nobody made turns a ledger into a wish-list. If G2 blocked the actuator,
the row is not written.

**18c — record the deploy-deletion hazard where it will fire.** §0.2 found that
`/opt/rotary-phone/refresh-gv-cookies.sh` — the load-bearing 20-minute cron — is deleted by the rsync branch.
📌 **Not fixed here**, but add a comment at the rsync exclusion list naming it, so the next person editing that
list sees it at the edit site rather than in a document:

```powershell
    # ⚠ EVERY FILE IN ${TargetPath} THAT THE PUBLISH OUTPUT DOES NOT CONTAIN IS DELETED
    # BY --delete, and the exclusions above are the entire defence. Known unprotected
    # today, found 2026-09-09 and NOT fixed here because it is out of this arc's scope:
    #   /opt/rotary-phone/refresh-gv-cookies.sh   <- the load-bearing 20-minute cookie cron
    #   /opt/rotary-phone/*.bak*                  <- every hand-made config backup
    #   /opt/rotary-phone/ChromeExtension/
    # The tar branch does not delete, which is why this has never been seen: rsync has
    # never been on the deploying workstation's PATH. It is on the box.
```

**Acceptance:**

- The spec gains a §11 that is **additive** — nothing above it is rewritten.
- The boundary doc gains **exactly one** row, and only if the actuator shipped; it states the BT/audio no-op and
  carries the merged≠deployed≠INSTALLED caveat, matching every prior row.
- ⛔ `git diff` for 18c touches **one file** and is comment-only.

---

## 3. Dependency graph and suggested order

```
Phase 0   1 ────────────────┐              (the two owner gates — G1, G2)
          2 ────────────────┤              (⛔ GATE: the alarm must be LIVE. Not this plan's work)
                            │
Phase 1   3 ── 4 ───────────┤              (CDP tool ─ ⛔ THE SPIKE, gated on G1, ATTENDED)
                            │
Phase 2   5 ── 6 ───────────┤              (⛔ THE BREAKER, alone, before any login code)
                            │
Phase 3   7 ── 8 ───────────┤              (credential file ─ BOTH deploy exclusions)
                            │
Phase 4   9 ── 10 ── 11 ── 12              (poll/gate/lock ─ drive+classify ─ verify ─ harness)
                     ⛔ all four blocked on Task 4 saying the design works
Phase 5   13 ── 14                         (the alarm's second track ─ drift guard)
Phase 6   15 ── 16 ── 17                   (install ─ deploy+prove ─ ⛔ TOKEN+G2-GATED runs)
Phase 7   18                               (record; needs 4 and 17)
```

**Suggested order for a build session, and why:**

1. **Tasks 1 and 2 first, always.** They are the two gates and they can both fail. Discovering at Task 16 that
   the alarm was never installed would mean building the whole actuator before learning it has nowhere to
   escalate.
2. **Tasks 3, 5, 6, 7, 8 next** — five independent, box-free, credential-free commits that can be reviewed while
   the gates resolve. ⭐ **Build the breaker (5, 6) even before the spike.** It is the feature, it depends on
   nothing the spike could invalidate, and if the spike kills the design the breaker is the only part worth
   keeping as a record of how this was thought about.
3. **Task 4 when G1 clears**, attended, owner present, and with the whole afternoon rather than the end of one.
4. **Tasks 9–12** as one stretch, driven entirely by Task 4's recording.
5. **Tasks 13, 14** — they touch the alarm and are worth reviewing in isolation from the actuator.
6. **Tasks 15, 16**, then **17 only when G2 clears.**

⚠ **Three things are wall-clock-bound or owner-bound and should be started early rather than optimally:**
Task 1 (the owner's bar), Task 2 (three unstarted tasks in another plan), and Task 4 (needs the owner in the
room). None of them is made faster by having the code ready.

⛔ **The one ordering that is not negotiable: the breaker before the login.** Not because of dependency — the
actuator could technically be written first — but because a restraint written after the thing it restrains gets
shaped by that thing's convenience.

---

## 4. Open questions and decisions

| # | Question | Owner | State |
|---|---|---|---|
| **G1** | Is the cannibalisation test concluded, so a scripted sign-in will not confound it? | **Owner** | ⛔ **open — gates Task 4.** 121 min, 60 samples, zero staleness (§0.13) |
| **G2** | Does the result clear the actuator to ship? Spec §3: if sessions still die hourly, fix the death rate first | **Owner**, on the data | ⛔ **open — gates Task 17 and the timer.** ⚠ Two hours does not support *"months"* in either direction |
| **G3** | ⛔ **The alarm is merged but NOT INSTALLED and there is no gateway token** (§0.1). `gv-session-alarm.md` Tasks 5, 16, 17 are unstarted. The breaker's only escalation path does not exist | **Owner** | ⛔ **open — gates Tasks 15's `--enable`, 16 and 17.** Not this plan's work to do; it is this plan's work to refuse to ship without |
| **Q1** | ⛔ **The credential's location.** Spec §4 puts it at `/opt/rotary-phone/gv-account.conf`. Planned there, with **both** exclusions (§0.2). ⚠ But that directory is the deploy target, and every file in it that the publish output lacks is an rsync deletion target — `refresh-gv-cookies.sh` is in that position today. `~/.rotaryphone-gv-account` (mode 600, beside `~/.rotaryphone-env`) is outside the deploy's reach entirely and needs no exclusion to survive | **Owner** | ⚠ **planned as specced.** Recorded because the evidence arrived after the spec was written, not to re-open a settled decision. Cost of the current choice: two exclusions that a future deploy edit can drop |
| **Q2** | ⛔ **Spec acceptance 6's instrument.** *"checked by inspecting `/proc/<pid>/cmdline` during a run"* is the sampler this repo measured as unsound hours earlier — *"a sampling test here would pass by missing it"* (§0.3). Planned as a **source assertion** plus artefact greps | Owner to ratify | ⚠ **planned as corrected**; say so if you disagree |
| **Q3** | ⛔ **Spec §7's placement.** *"adds one new condition to it"* — planned as a **second, independent track**, because sharing `LAST_POSTED_CONDITION` would make the alarm go mute on a genuine session death whenever the breaker was tripped (§0.9) | Owner to ratify | ⚠ **planned as corrected**; PR #85's track is byte-unchanged |
| **Q4** | **Spec §10 decision 3:** does a Google challenge disable auto-relogin permanently or merely pause it? | Planning | ✅ **settled as PERMANENTLY**, the spec's own default. There is no pause in the design and no timer that re-arms |
| **Q5** | **Rate-limit numbers.** 1/hour and 3/day are the **spec's**, carried through unchanged and explicitly **not measured** (spec §10 decision 4). The transport ceiling reuses the daily number rather than adding a fourth | **Owner**, after real re-login frequency is known | ⚠ **open, and deliberately unmoved.** ⛔ This plan proposes no number the spec did not already state |
| **Q6** | **Step timeouts and selectors.** Every one is `SPIKE_REQUIRED` until Task 4 measures it | — | ⛔ **open by construction.** The harness fails while any sentinel remains |
| **Q7** | 📌 `/opt/rotary-phone/refresh-gv-cookies.sh` and every `*.bak*` in that directory are **deleted by the rsync branch** (§0.2). Real, unfixed, and pre-existing | Owner | 📌 **recorded, not scoped.** Task 18c puts the fact at the edit site |

⭐ **On Q1, the recommendation with its cost.** Keeping the credential at `/opt/rotary-phone/gv-account.conf`
means its survival depends on two exclusion lines in a 33 KB PowerShell script that PR #84 rewrote 275 lines of
last week. Moving it to `~/.rotaryphone-gv-account` means it is **structurally** out of reach — the deploy never
writes to `$HOME` — and it sits beside `~/.rotaryphone-env`, which already holds the alarm's secret under
exactly that argument. **What the current choice gives up:** a protection that cannot be dropped by editing a
line. **What moving it would give up:** consistency with `appsettings.Production.json`, which is the precedent
the spec reasoned from, and a re-decision the owner has already made once today.

---

## 5. Out of scope

| Not doing | Why |
|---|---|
| **Any second factor** | Spec §2: the account has **none**, established with the owner 2026-09-09. No TOTP, no prompt, no SMS, no security key, in any task. ⚠ If a second factor is ever added, this design's safety case changes with it — re-check §2 before shipping, not once at design time |
| **Clearing the profile or launching a fresh browser** | Spec §5. Google already knows this device, profile and IP. A fresh browser converts routine re-auth into an unrecognised-device sign-in, which is far more likely to be challenged. ⛔ There is deliberately no code path that could do either |
| **Folding auto-relogin into the C# service** | Spec §5: shell/systemd owns the browser lifecycle, the service observes. Keeps Google credentials out of the service process and preserves the cross-repo contract Radio Console's KIOSK-2 assumes |
| **Folding detection into the actuator, or the actuator into the alarm** | Spec §7. They are separately testable and separately reviewed, and the value of the alarm is that it still works when the actuator does not. Task 17g is the run that proves it |
| **New detection logic in the alarm** | `THIS SCRIPT DETECTS NOTHING` stays true. The alarm quotes the breaker's own words (§0.10) exactly as it quotes the service's |
| **Any retry of a rejected credential or a challenge** | ⛔ Spec §6. The breaker has no vocabulary for it: `grep -Ec 'retry\|backoff\|sleep'` on the breaker is asserted at **0** |
| **Adapting, re-shaping or re-submitting a failed sign-in** | Same class as the alarm's Q2 deviation: a flow reshaped by a failure handler is one no test covered, and here the thing being risked is the account |
| **Coordinating with the 20-minute cron** | §0.11. It is a crontab entry outside this repo's deploy path; reaching into it is a box-state change with its own rollback story, and the race is benign post-hardening |
| **Fixing the rsync-deletion hazard for `refresh-gv-cookies.sh` and the `.bak` files** | §0.2, Q7. Real, reproduced by reading the script, and pre-existing. Task 18c makes it visible at the edit site; fixing it is someone's PR, not this one |
| **Choosing a `browserSessionAgeSeconds` WARN threshold** | `gv-session-alarm.md` Q3, still open and still deliberately unnumbered |
| **BT/audio, `hci0`/`hci1`, WirePlumber, the kiosk profile** | Governed by the boundary doc. ⛔ Nothing here touches any of them: the actuator drives the **existing** GV bridge Chrome on its **existing** profile and never enumerates, launches or kills a process |

---

## 6. The risk this plan is most likely to realise

⚠ **Not a technical risk. Every one of these is a way the work gets done, reported complete, and leaves the box
in a state that is worse than not having started.**

1. ⛔ **The actuator ships while the alarm is still uninstalled.** §0.1, gate G3. This is the likeliest failure
   because it requires no mistake — only for Task 2 to be read as a formality and Task 15's `--enable` guard to
   be removed as an inconvenience during a live install. **The result is an automation that types a password at
   Google and whose only failure signal is a file nobody reads**, which is strictly worse than the manual
   process it replaced. The guard is in the installer for exactly this reason: it must refuse, not warn.

2. ⛔ **The classifier's default is changed to `transport` — "just in case it's flaky."** §0.6. One line, entirely
   reasonable-looking, and it converts a permanent stop into a retry loop against Google's sign-in page. This is
   the single line in the arc that can lock the account, and the only defence is Task 10's acceptance: change it,
   watch the case fail, change it back.

3. ⛔ **The spike finds a challenge, and the finding is softened into "mostly works, needs a retry."** Task 4's
   go/no-go exists because the pressure at that moment will be to salvage two days of design. Spec §8 is explicit:
   *"the honest outcome is to say so rather than to add retries."*

4. ⚠ **A number gets written where the spec refused to write one.** G2's duration, the rate limits, the step
   timeouts. Each looks like an unfinished cell in a table, and filling one converts a judgement nobody made into
   a fact everyone inherits.

5. ⚠ **Task 17's acceptance runs quietly spend more credential attempts than the design permits.** They are real
   sign-ins on a real account, and "it's just a test" is how an arc built to bound sign-in traffic exceeds it.
   Hence the counted, stated total.

⭐ **The sentence to keep, and this arc earns a second one beside the alarm's:**

> **Read the installed artefact. Not a repo file, not a branch name, and not your own memory of this morning.**
>
> **And when the outcome is one you do not recognise, stop. An unrecognised outcome is not evidence that trying
> again is safe.**
