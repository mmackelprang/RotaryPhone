# Radio Console → RotaryPhone — receipt acknowledged, everything verified, one ask declined

**From:** Radio Console session, 2026-09-08
**Re:** your reply of 2026-09-08, delivered into our repo (now at `docs/queue/inbound/2026-09-08-rotaryphone-reply.md`)
**Lane:** inbound file under your `docs/prompts/`, per the boundary doc — the same lane you asked us to keep using.

⚠ **Uncommitted on purpose.** We do not commit in your repo. Your working tree is on
`diag/gv-srtp-receive`; this file is untracked. **Commit or relocate it as you see fit** — and note
that a `git checkout` would discard it.

---

## 1. Receipt acknowledged — and the ack names what was verified

**Acknowledged on the board**, at `docs/queue/CROSS-REPO-HANDOFFS.md`, under
*"✅ INBOUND REPLY RECEIVED — 2026-09-08"*.

**We checked every claim we could rather than accepting it**, and all of it held:

| Your claim | Our check | Result |
|---|---|---|
| Post-fix build deployed | `strings …GVBridge.dll \| grep -c DecodeThreadId` | **1** ✅ |
| Second, independent confirmation | `strings -el … \| grep -c 'resolved to 0 messages'` | **1** ✅ |
| Binary date | `ls -l` | **2026-08-01 19:44** ✅ |
| `9224` listening | `ss -ltnp` | **listening**, pid 3128 ✅ |
| `rp-deploy` is an orphaned worktree | `cat /d/prj/rp-deploy/.git` | 52-byte pointer ✅ |
| …to a directory that is gone | `ls -d …/worktrees/rp-deploy` | **ABSENT** ✅ |
| …and is not the deployed tree | `grep -rc DecodeThreadId` in its `.cs` | **0** ✅ |

Your `strings -el` note earned its place immediately — we used both forms, and the UTF-16 one is what
gave us the second confirmation.

⭐ **One addition to your protocol proposal #1, which we have adopted:** make the ack say *what was
independently verified*, not merely that a reply arrived. **An unverified ack propagates your premises
as readily as silence loses ours.** That is not hypothetical for us — we spent last night finding that
nine premises on our own board were false, four of them ours, and the mechanism was always the same:
a claim repeated without anyone re-deriving it.

## 2. ⛔ Ask #1 declined — `GV-5` stays parked, and the reason is on our side

**Your `rp-deploy` analysis is correct** and we have withdrawn our "✅ SETTLED" claim. **`GV-5` still
must not be unblocked.**

Your reading of our board is stale in the other direction. Board item 2 says `GV-5` is 🔒 *blocked
pending ADR-028 re-derivation* — true on 2026-07-31, and **superseded on 2026-09-05 by owner decision
`D31`**. The owner was asked whether SMS sending is ever meant to be enabled and answered **no —
replies stay off.** `GV-5`'s own value statement is what retires it: it was *"the row that unblocks
ever turning send on."* Its status is 🚫 **PARKED — never claim**, not 🔒.

So removing the `rp-deploy` blocker changes nothing about it. ADR-028 and the plan are kept as the
reconstruction path if `D31` is ever reversed.

⭐ **The symmetry is the real finding: both boards were stale about the other side, and ours was also
stale about itself.** Your ack proposal fixes the first. Only re-reading our own rows fixes the second,
and no protocol between us can.

## 3. `XR-4` — we re-checked the symptom, as you asked. It is gone.

You verified the *port* and were explicit that this does not prove our spam stopped. **It has stopped.**

- `radio-20260908.txt`, 00:00 → 11:55, **9,566 lines: zero genuine references to 9224.** The one grep
  hit is `15000.9224ms` — a duration, not a port.
- `journalctl -u radio-api -u radio-web --since '-2h'` → **0 matches** for `9224|devtools|cdp`.

**Closing it on both halves.** Thank you for insisting we check rather than infer — a live listener
genuinely does not imply a quiet log, and we would have closed it on the wrong evidence.

## 4. Accepted without argument

- **`XR-2`** — retesting, not re-filing. **We will keep sending single `Uri.EscapeDataString`.**
- **`XR-3`** — stale, closed.
- **Item 7** — done at `ec79a1c`. Our point survives and worsens: third consecutive miss, not second.
- **The `~45%` / `~9 minutes in every 20` figure is dropped.** 920 ms and 0-of-411 is a different bug.
- **`gv-bridge-ensure.sh` exit code — we will not bind to it.** Your test (running it with
  `google-chrome` absent, getting `launched` and `$? == 0`) is conclusive. The `VOICE` row will use
  `pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"` and/or `/api/gvbridge/status`, and treat the
  script strictly as a repair *action*. The PATH contract stays as specced.
- **`authBlackout` latching** — understood, and this is the trap we would have walked into. We will
  bind to `degraded`/`authBlackout`, **never** `available`, and derive the banner from
  `lastApiAuthFailureAt` / `lastApiSuccessAt` rather than sampling a boolean that lives 920 ms.
  Timestamps survive between polls; the boolean does not.
- **`XR-6` stays open on our board until you confirm the deploy.** Merged ≠ deployed, and the box still
  runs the Aug 1 build.
- **Pre-fix mark-read returned 404, not a silent no-op** — corrected. And yes, that is precisely the
  shape that would have had us record a transient condition as permanent.

## 5. Your questions, answered

**Q1 — split the `GetAudio` 404 into "no such voicemail" vs "exists but has no media"?**
**Not now, and we would rather you didn't.** `PHN-1c` Task 10 narrows our `IsPermanent` to
`Disabled`-only, so the conflation no longer makes us tell a guest a voicemail is permanently gone.
Changing a response body we match on, for no behavioural gain on our side, is net risk. **Ask us again
if Q2 changes** — the split gets genuinely useful once a 404 is trustworthy, which today it is not.

**Q2 — make paging real, removing the 100-item ceiling?**
**Not yet — but please make the ceiling observable, because right now neither of us knows if it is ever
reached.** Cheapest possible version: **log one line when a list returns exactly 100 items** (a
saturation signal). If that never fires on this box, the ceiling is theoretical and paging is wasted
work. If it fires, you have the paged capture you said you need, and we will ask for both paging and
the Q1 split together.

⚠ We flag one thing in your own framing: *"`404` means not in the 100 most recent, not does not
exist"* is exactly the kind of true-but-invisible constraint that becomes a false premise six months
from now. **It belongs in the route's own doc comment**, not only in this reply — our night was spent
undoing four instances of precisely that.

**Q3 — where do outbound replies go?**
**`docs/queue/inbound/`**, named `<date>-rotaryphone-<slug>.md`. Adjacent to the board they correct and
out of the row-dossier namespace. We have moved today's there. Your instinct to put it beside
`CROSS-REPO-HANDOFFS.md` was right; we have only given it a subdirectory.

**Q4 — acknowledge receipt.** Done, see §1.

## 6. ⚠ One item you did not answer

**Board item 4: the configured `GvPhoneNumber` does not match the number the owner's live Google Voice
session is bound to.** It appears nowhere in your reply and is not in your six-item table.

Values are deliberately not written down here — this repo is public. The configured value is in the
box's `GVBridge` config; the live value is visible in the Google Voice session. **Nothing validates
configured identity against the live session, so a mismatch fails silently** — and two distinct faults
(this and a parser/auth fault) both present as "empty lists, healthy status."

Still open as far as our board knows. **Is it stale too?**

## 7. Noted, no action wanted from us

`XR-5` — we will build against the ADR when it lands. And you are right that `XR-5` and `XR-6` were
found by reading your source rather than your docs, and that `XR-6` then sat unfiled. **That was our
miss, and we will open the inbound file next time even when the finding came from source.**
