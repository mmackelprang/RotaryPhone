# Inbound call: the caller's hangup never reaches the box — the rotary rings until the handset is lifted

**From:** Radio Console session (RTest) · **Date:** 2026-09-11
**Status:** Handoff for your next session. ⛔ **The owner explicitly said this does NOT need fixing now.**
**Nothing has been changed on the box.** This is evidence-gathering only, done because the journal was
in front of me and the evidence ages out.

## What the owner reported

> *"I called the rotary phone from my cell phone and it began ringing. I hung up the cell phone, but
> the rotary phone continued ringing (for a long time) until I lifted and replaced the handset. It
> looks like the hangup sequence from the cell phone didn't make it back to the Grandstream. This was
> around 6:30pm eastern on 10/Sep."*

## ⭐ The log supports the owner's diagnosis. The only `BYE` came from the HT801, not from Google.

Reconstructed from `journalctl -u rotary-phone`, **all numbers redacted** (both repos are public):

```
17:26:50  SIP received … INVITE sip:<NUM>@…;transport=wss
17:26:50  Incoming INVITE from <NUM>, Call-ID=4TP6I5…
17:26:51  Answered incoming call from <NUM>            <- THE GV LEG IS AUTO-ANSWERED HERE
17:26:51  State changed to: Ringing
17:26:51  CallManager sending INVITE to 1000@192.168.86.240 (SDP RTP port 49000)
17:26:51  SIP Response received: 180 Ringing from udp:192.168.86.240:5060
          ... 33 seconds of ringing, NOTHING inbound from Google ...
17:27:24  HT801 answered INVITE (200 OK) — handset lifted, triggering off-hook
17:27:24  Hook change: OFF-HOOK · State changed to: InCall
17:27:26  SIP Request received: BYE from udp:192.168.86.240:5060   <- FROM THE HT801 (handset replaced)
17:27:26  Processing BYE message - Call terminated by remote party
17:27:26  Hook change: ON-HOOK · BYE sent to HT801 · BYE sent to Google Voice · State: Idle
```

⛔ **There is no inbound `BYE` or `CANCEL` from Google Voice anywhere in the call window.** The
teardown was driven entirely by the owner's handset. The service's own log line
*"Call terminated by remote party"* is describing the **HT801** as the remote party, which is true of
the SIP dialog and misleading about what actually happened.

## ⭐ The detail I think matters most: the GV leg is ANSWERED before the human picks up

`17:26:51 Answered incoming call from <NUM>` fires **33 seconds before** the handset is lifted.

**So from Google's side the call was already connected while the rotary was still ringing.** A caller
who abandons at that point is hanging up an **answered** dialog, which should produce a **`BYE`**, not
a `CANCEL`. ⚠ **If anything downstream is watching for `CANCEL` to mean "caller gave up", it would
never fire on this path.** I have not read your code and am not asserting that is the bug — **it is
the first thing I would check.**

## ⚠ A second, separate standing defect: the GV SIP WebSocket throws every hour

`GvSipWebSocketChannel.ReceiveLoopAsync` threw at:

```
16:21:14 · 17:21:15 · 18:21:21 · 19:21:22 · 20:21:26 · 20:25:27 · 21:25:31
```

**Seven times in six hours, clustered on the :21 minute.** ⚠ **I do NOT think this caused the missed
hangup** — the drop at 17:21:15 was 5½ minutes before the call, and four inbound SIP messages were
received during the call, so the socket was working. ⛔ **Recorded because it is clearly a defect in
its own right, not because I am proposing it as the cause.** Resist the temptation to connect them
without evidence.

## ⛔ TWO DISCREPANCIES — do not let these be smoothed over

| The owner said | The log says |
|---|---|
| *"around 6:30pm"* | **17:26:50** — an hour earlier |
| *"continued ringing for a long time"* | **33 seconds** (17:26:51 → 17:27:24) |

⚠ **This is the ONLY call in the 17:00–22:00 window**, so either the owner's recall was imprecise on
both counts, **or the call they mean is not in the journal at all** — which would be a more serious
finding than the one reported. ⛔ **Establish which before building anything.** The box clock is
`America/New_York`, NTP-synchronised, verified at read time.

⭐ **The owner is a reliable reporter — every UAT result they have given us this week has held up.**
Two independent discrepancies in one report is therefore worth a moment's thought rather than a
shrug.

## What I did NOT do

- ⛔ **No changes of any kind** — read-only journal queries.
- ⛔ **No code read.** I have not opened a single RotaryPhone source file, so every statement above is
  from the log alone. **The mechanism is yours to establish.**
- ⚠ I did not verify whether Google *sent* a BYE that was dropped in transit versus never sent one.
  **From this side those are indistinguishable** — you would need the GV bridge's own view.

## Reproducing it

Call the rotary from a mobile and **hang up while it is still ringing**, then read
`journalctl -u rotary-phone` around that minute. If no inbound `BYE`/`CANCEL` appears from Google, it
reproduces. ⚠ **Note the wall-clock time of the hangup press** — on the Radio side this week, an
un-timestamped pause/resume cost us two wrong causal conclusions before the owner noted the clock, and
the second run overturned the first.
