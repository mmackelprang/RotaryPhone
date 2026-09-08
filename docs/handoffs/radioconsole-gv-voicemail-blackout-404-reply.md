# Reply — XR-6 fixed, and five corrections to the cross-repo board

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.
>
> ⚠️ **This reply exists because the last two did not reach you.** Replies to `XR-2` and `XR-3` were
> written and committed in the RotaryPhone repo on 2026-07-31 (`d4c3b5e`) and 2026-08-01 (`dc01037`)
> and neither is referenced anywhere in `D:\prj\RTest\RTest\docs\`. Please confirm receipt of this
> one on the board, so we can tell "not delivered" from "delivered and not actioned".

---

## TL;DR

Of the six items you have open against us, **one was real and open — `XR-6` — and you had never
filed it.** The other five are stale. That is not a criticism of your analysis: every one of your
findings was **correct on the day it was written**, and two of them you found by reading our source
because our docs did not say otherwise. The gap is on our side and it is structural, not personal —
see *"What actually went wrong"* at the end.

| Item | Your status | Actual status |
|---|---|---|
| `XR-1` | Struck through by you 2026-09-01 | ✅ Agreed, stale |
| `XR-2` (`%2F` thread ids) | Open | ✅ **Fixed 2026-07-31, deployed 2026-08-01** |
| `XR-3` (auth blackout) | Open | ✅ **Fixed and deployed 2026-08-01 (PR #72)** |
| `XR-4` (CDP spam) | Open — "nothing listens on 9224" | ⚠️ **Root cause no longer holds** — 9224 is listening |
| `GV-5` | 🔒 Blocked on "re-derive ADR-028" | ✅ **Unblock it — the premise is false** |
| `XR-6` (`GetAudio` 404) | Filed to your punch list, never sent to us | ✅ **Fixed in this PR** |

---

## 1. `XR-2` is fixed, and has been since before you last reproduced it

Commit **`3103662`**, *fix(gv): decode %2F-encoded thread ids so group/MMS threads are readable*,
**2026-07-31 22:18 EDT**.

- Decode in **both** routes — `GetThreadMessages` and `MarkThreadRead`. Each binds the route value as
  `rawThreadId` and decodes once at the top, so a later edit cannot reach for the still-encoded value
  by habit. In `MarkThreadRead` that covers all four uses: the thread lookup, the `ListMessagesAsync`
  enumeration, the `updateread` write, and the `ReadStateChanged` broadcast payload.
- **`WarnIfNoMessages`** — the per-thread sanity check you recommended. One Warning line when a thread
  that fetched and parsed successfully yields zero messages. It deliberately does not throw, and it
  fires per user action rather than per poll, because journald churn on this box correlates with audio
  distortion.
- **13 regression tests**, keyed on the **slash** rather than on "MMS", using a real id:
  `g.Group Message.d5Mri/NrDUQgXNXNQehOfw` (route value `…d5Mri%2FNrDUQgXNXNQehOfw`).

**Your reproduction was accurate and was superseded about seven hours later.** Your evidence is
timestamped the evening of 2026-07-31; the fix landed 22:18 the same night. Nothing you reported was
wrong — please retest rather than re-file.

**Keep sending exactly what you send today** (single `Uri.EscapeDataString` — `%20` for the space,
`%2F` for the slash). That is the spelling we handle. Do not switch to double-escaping or to a raw
`/`; you already demonstrated both are worse.

## 2. `XR-3` is fixed and deployed

PR **#72**, merged **2026-08-01 19:33**, on the box by **19:44**. It added recover-and-retry on
401/403 at the shared read path, a real proactive PSIDTS refresh, and health derived from the last
real data-plane call rather than a 30-minute-stale probe of a different endpoint.

**`/api/gvbridge/status` is now honest — you have a signal to bind to.** Live from the box while
writing this:

```json
{"available":true,"activeMode":"GVApi","sipRegistered":true,"wsConnected":true,
 "cookiesValid":true,"psidtsAgeSeconds":342,"degraded":false,
 "throttledUntil":null,"throttleReason":null,"authBlackout":false,
 "lastApiSuccessAt":"2026-09-08T14:34:36Z","lastApiAuthFailureAt":"2026-09-08T14:20:29Z"}
```

That should unblock the reconnect banner in `PhoneMessagesPanel.razor`.

⚠️ **Two things to get right when you bind it:**

- **Bind to `degraded` or `authBlackout`, never to `available`.** `available` deliberately stays
  `true` during a blackout — it gates `GetAuthenticatedClient()` *inside* RotaryPhone, so flipping it
  would make the adapter refuse its own recovery retry and turn a short blackout into a hard stop.
  This is the one deliberate deviation from what you asked for.
- **`authBlackout` can be true for well under a second.** Measured on the box during a 90-minute soak:
  the single blackout lasted **920 ms**, and **zero** `authBlackout:true` samples appeared across
  **411** status polls. A banner bound naively to that boolean at a 10-second cadence will effectively
  never appear. Latch it as an *event* with a minimum display window, or derive the UI from
  `lastApiAuthFailureAt` / `lastApiSuccessAt` — timestamps survive between polls; the boolean does not.

## 3. The `rp-deploy` premise is false — please unblock `GV-5`

Your item 2 records, as **✅ SETTLED 2026-07-31**, that *"the deployed tree is `D:\prj\rp-deploy` @
`0a86898`, NOT `D:\prj\RotaryPhone`"*, and blocks `GV-5` pending re-derivation of ADR-028 on that
basis. **That conclusion is wrong, and it is the most expensive item on the board.**

**`rp-deploy` is not a separate repo.** It is an **orphaned git worktree** of `D:\prj\RotaryPhone`.
Its `.git` is a 52-byte pointer file reading `gitdir: D:/prj/RotaryPhone/.git/worktrees/rp-deploy`,
and **that directory does not exist** — so no git command works there at all, which is probably why
it looked like an independent checkout. Its files are frozen at Jul 29–31.

**It is also demonstrably not what runs on the box:**

| | `rp-deploy` working tree | Deployed binary on `radio` |
|---|---|---|
| `GvSmsController.cs` | Jul 29 15:02 | — |
| `DecodeThreadId` present | **0 occurrences** | **present** |
| `RotaryPhoneController.GVBridge.dll` | — | Aug 1 19:44 |

A binary dated Aug 1 19:44 that contains `DecodeThreadId` cannot have been built from a tree that
does not contain `DecodeThreadId`. **The box is not running `rp-deploy`.** (Strictly, that is what the
evidence proves — any tree containing the method would satisfy it. The build is consistent with `main`
at the time, but the load-bearing point for you is simply that `rp-deploy` is not the deployed tree
and is not a repo you need to reason about.)

**Verify it yourself rather than taking our word:**

```bash
ssh mmack@radio 'strings /opt/rotary-phone/RotaryPhoneController.GVBridge.dll | grep -c DecodeThreadId'
```

Expect `1`. Two notes on that command, both learned the hard way:

- Method names live in .NET metadata as UTF-8, so plain `strings` finds them. **Log message literals
  are UTF-16 and plain `strings` silently returns 0 for them** — use `strings -el` if you ever probe
  for a log string. An ASCII grep returning 0 is not evidence of absence.
- The `WarnIfNoMessages` log line — `strings -el … | grep 'resolved to 0 messages'` — also returns 1,
  which is a second, independent confirmation that the post-fix build is deployed.

## 4. Correction: pre-fix mark-read returned **404**, not a silent no-op

Your board describes mark-read on a group thread as *"silently a no-op today"*. It never was. On the
pre-fix code the thread lookup exact-compared, missed, and the route returned **`404 "SMS thread …
not found"`** — a visible failure.

**This matters to you specifically** because you may have been mapping that 404 as permanent, the same
way `GvMediaUnavailableException.IsPermanent` maps `NotFound` on the audio route. If so, a transient
condition was being recorded as a permanent one.

## 5. `XR-4`: the stated root cause no longer holds — please re-check the symptom

Your `XR-4` finding rests on *"nothing is listening on 9224 unless someone separately starts a
GV-session Chrome."* **That premise is now false.** Checked on the box just now:

```
$ ss -ltnp | grep 922
LISTEN 0 10 127.0.0.1:9224 0.0.0.0:*  users:(("chrome",pid=3128,fd=89))
LISTEN 0 10 127.0.0.1:9223 0.0.0.0:*  users:(("chrome",pid=32425,fd=96))

$ tr '\0' ' ' < /proc/3128/cmdline  | grep -o -- '--user-data-dir=[^ ]*'
--user-data-dir=/home/mmack/.config/gv-bridge-chrome     <- GV bridge (ours)
$ tr '\0' ' ' < /proc/32425/cmdline | grep -o -- '--user-data-dir=[^ ]*'
--user-data-dir=/home/mmack/.config/radio-kiosk-chrome   <- your kiosk
```

(Both listeners are `chrome`, so the port alone does not attribute them — the profile directory is
what distinguishes the two, which is also why `gv-bridge-ensure.sh` uses that marker for liveness.)

`gv-bridge-ensure.sh` now launches Chrome with `--remote-debugging-port=9224 --remote-allow-origins=*`,
and those flags are load-bearing rather than debug aids — our cookie refresh reaches the browser over
CDP through them. Your own item 8 already notes the flags were added; what had not reached you is that
the port is consequently up continuously.

⚠️ **We are not claiming your log spam is gone.** We checked the *port*, not your journal. Please
re-check whether the ~20-minute stack-trace spam is still present, because if it is, the cause is
something other than an absent listener and the item needs re-diagnosing rather than closing.

## 6. `XR-6` — fixed here, and it was wider than three lines

You were right about the defect and right about the mechanism. Two corrections to the framing.

**It is not a three-line change.** `FindNodeAsync` has **four** call sites, and propagating `Succeeded`
forces a decision at every one — you cannot propagate it and ignore it. Three now answer 502; one
deliberately does not:

| Call site | Behaviour on a failed list | Why |
|---|---|---|
| `GetItem` | **502** | Same defect you found, same remedy |
| `GetAudio` | **502** | The bug you reported |
| `MarkRead` step 2 (pre-write lookup) | **502** | Same defect, and it 404s *before* any write is attempted |
| `MarkRead` step 5 (post-write re-read) | **stays 200** — flag deliberately discarded | The write to Google **already succeeded**. Returning 502 here would tell you a real state change did not happen, and you would reconcile away a change that is real — a worse lie than a marginally stale DTO |

That last row is a deliberate, tested exception with a comment in the source explaining why, so nobody
"fixes" it later. Each route is covered by a **pair** of tests — one proving a failed list becomes 502,
its twin proving a successful list that simply lacks the id is **still 404**. The pair is the point: a
fix that turned every miss into a 502 would pass the first half and break your `404` semantics.

**So the contract is now:** `502` = *"we could not look."* `404` = *"we looked, and it is not there."*

⚠️ **One ceiling you should know about before you harden anything on that.** The 404 half is bounded
at the **100 most recent voicemails**. We request `count: 100`, and our client deliberately *ignores* a
page token because the paging field position in Google's wire format is unverified (we log a warning
rather than guess and silently re-read page 1). So a voicemail older than the 100th comes back as a
successful list with the id absent, and we report it as a genuine miss — a **404 for a voicemail that
exists**. That is pre-existing, unchanged by this PR, and out of its scope, but it is the same
guest-facing lie `XR-6` fixed, reached by a different trigger. **`404` from us means "not in the 100
most recent", not "does not exist"** — so `IsPermanent` on `NotFound` is now correct within that
window, and still overclaims outside it. Tell us if you want paging made real; it needs a paged
capture from the box first.

**Please drop the "~45% of the time / ~9 minutes in every 20" severity figure.** It was measured before
PR #72, which added recover-and-retry on 401/403 at the shared read path. The blackout window is now
materially narrower — the live status above shows an auth failure at 14:20:29 recovered by 14:34:36,
with `psidtsAgeSeconds` at 342 rather than the 128041 you measured during the keyring outage. `XR-6`
was a **real** correctness bug and worth fixing, but it is a *rare* lie, not a frequent one. Reprioritise
accordingly.

**One thing we did not change, and want your call on.** The surviving 404 on `GetAudio` still conflates
*"no such voicemail"* with *"the voicemail exists but has no media"*. Splitting them would change the
response body you match on, so we left it alone. **Do you want them split?** If yes, tell us the shape
you want and we will do it as its own change.

## 7. `KIOSK-2`: `gv-bridge-ensure.sh` — the path is fine, the exit code is not

Recorded in the boundary doc Change Log (2026-09-08). Ownership does not move: the script, the
2-minute watchdog timer and the nightly restart timer stay ours; you only invoke it.

**Path — good.** We install it to **`~/bin/gv-bridge-ensure.sh`** (mode 755), the only one of your
three candidates that exists today. Your candidate-list resolution
(`~/bin/` → `/usr/local/bin/` → `/opt/rotary-phone/bin/`) is exactly the right shape — a relocation
degrades to a reported failure instead of a silent wrong answer. We will announce a move in the
boundary doc Change Log before making one.

⚠️ **Exit code — do not use it.** `gv-bridge-ensure.sh` **exits 0 on every path it can reach**: bridge
already up, lock held by the other launcher, **and launch failed**. It runs under `set -u` with no
`set -e`, and its final statement is an unconditional `echo` to its log, so a failing `systemd-run` is
swallowed. Verified 2026-09-08 by running it with `google-chrome` absent:

```
Failed to find executable google-chrome: No such file or directory
2026-09-08 10:32:14 ensure: bridge was down -> launched      <- overclaim
$ echo $?
0
```

A nonzero status therefore means *the interpreter* failed — file missing, not executable, `set -u`
violation — and **never** "the bridge is down". **If your `VOICE` row goes amber on a nonzero exit, it
will never go amber.**

**Use instead:** treat the script strictly as a repair *action*, then re-check liveness directly with
the same marker the script itself uses —

```bash
pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"
```

— or read `/api/gvbridge/status`. Whether to give the script a meaningful exit code is an open decision
on our side (it would put `gv-bridge-watchdog.service`, a `Type=oneshot` unit, into a failed state on
every failed launch); we will update the Change Log row if that changes.

---

## What actually went wrong — and it is not what either of us assumed

The tempting conclusion is *"the protocol's reply half was never used."* **That is not what happened.**
We wrote both replies, on time:

- `docs/handoffs/radioconsole-gv-threadid-decode-b1-reply.md` — committed `d4c3b5e`, **2026-07-31**,
  the same day as the fix.
- `docs/handoffs/radioconsole-gv-auth-blackout-reply.md` — committed `dc01037`, **2026-08-01**.

Neither is referenced anywhere in your repo. **The replies were written and never delivered.** The
protocol in the boundary doc has a well-defined *inbound* lane — you create a file under our
`docs/prompts/`, and that works; `XR-6` is the only item that skipped it, which is why it is the only
one we did not know about. But the *outbound* direction terminates in a file in **our** repo, which
you have no reason to read. There is no delivery step, so a reply can be complete, correct, committed
— and invisible.

That is also why the board could show `XR-2` open for six weeks while it was fixed and deployed, and
why `GV-5` is blocked on a premise that was already false when it was written.

**Two cheap fixes, and we would like your view on both:**

1. **Every reply gets acknowledged on the board.** Add a line to the cross-repo item: *"reply received
   <date>, ref <file>"*. An unacknowledged reply is then visibly undelivered rather than silently so.
2. **Deliver outbound replies into your repo**, mirroring your inbound convention — a file under
   `D:\prj\RTest\RTest\docs\` — instead of only committing them into ours. We are happy to do this
   from our side by default; say the word and where you want them.

We are not proposing a queue file. Both of us already had one, both were accurate about our own work,
and neither could see the other's. The missing piece is an ack, not a tracker.

**Finally, on `XR-5` and `XR-6` being found by reading our source rather than our docs:** that was the
correct thing to do and it worked — both findings were sound. But `XR-6` then sat unfiled, so we did
not learn about the one item that was genuinely broken. If you find something that way again, please
still open the inbound file; a source-derived finding is worth just as much as a documented one, and
it is the only kind we cannot discover on our own.
