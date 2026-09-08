# RotaryPhone HFP is being accepted on `hci0`, and the phone drops while playing music

**From:** Radio Console session, 2026-09-07
**To:** RotaryPhone session
**Boundary rule involved:** #6 — *"Do not register A2DP on the voice adapter or HFP-HF on the music adapter."*
**Severity:** user-visible. The owner's phone drops mid-playback and shows an error on the handset.

---

## The symptom the owner reported

> "Connecting my phone via bluetooth works for a few seconds but sometimes drops for a little while
> before reconnecting. When it drops, there is an error message on the phone."

Phone: Pixel 10 Pro XL, `B0:D5:FB:D2:0D:68`, connected to **`hci0`** (TP-Link UB500, the *music*
adapter) for A2DP.

## What the logs show — this part is proven

```
11:18:53  RotaryPhoneController.Server: bt_manager stderr: [bt_manager] NewConnection:
          device=/org/bluez/hci0/dev_B0_D5_FB_D2_0D_68, fd=9
11:18:55  bluetoothd: /org/bluez/hci0/dev_B0_D5_FB_D2_0D_68/sep16/fd5: fd(65) ready   ← A2DP up
11:19:40  bluetoothd: src/profile.c:ext_io_disconnected() Unable to get io data for
          RotaryPhone HFP: getpeername: Transport endpoint is not connected (107)
11:19:58  bluetoothd: /org/bluez/hci0/dev_B0_D5_FB_D2_0D_68/fd6: fd(64) ready          ← 18s gap ends
```

**`bt_manager` accepted an HFP connection on `hci0`** — the music adapter — and 47 seconds later that
session was dead. An 18-second gap in the A2DP transport brackets the failure, which matches the
owner's "drops for a little while before reconnecting."

`getpeername: Transport endpoint is not connected` means the socket was **already dead** when BlueZ
went to read it. The HFP session on `hci0` died on its own.

## What is NOT proven

- **That the HFP failure caused the A2DP drop.** The correlation is tight and the mechanism is
  plausible, but this is **one occurrence**, not a reproduction. Please do not treat causation as
  established.
- **That the dedup path is involved.** A first hypothesis was that
  `HfpProfile.NewConnection`'s address-based dedup was closing the fd. **There is no
  `Duplicate HFP … closing fd` line in the journal for this event**, so that path did not fire.
  Ruled out.

## Where this comes from in your code

`scripts/bt_manager.py:511`:

```python
# Accept HFP on any adapter — the phone may connect via hci0 or hci1.
# Closing the fd for the "wrong" adapter causes the phone to drop ALL
# HFP connections, including the one on the correct adapter.
```

This is a **deliberate reversal of your own architecture's mitigation.**
`docs/superpowers/specs/2026-03-13-rotaryphone-standalone-architecture-design.md:58` says:

> **Adapter isolation note:** `ProfileManager1.RegisterProfile` is BlueZ daemon-wide (not
> per-adapter). Adapter isolation is achieved by: (a) only running discovery on hci1, (b) only
> initiating `ConnectProfile` on devices under `/org/bluez/hci1/dev_*`, and **(c) verifying the
> device path prefix in `NewConnection` callbacks to reject connections arriving on the wrong
> adapter.**

Mitigation (c) is the one that is now disabled. The comment explains why — rejecting produced a
worse failure — so **this is a known trade, not an oversight.** We are not asking you to simply
re-enable it; that was already tried and it broke HFP on `hci1` too.

## What we think the actual question is

The trade above is between two bad options because the phone is being *allowed* to offer HFP to
`hci0` at all. The design's real invariant is that the music adapter should never be an HFP
endpoint. Options we can see, none of which we are qualified to choose:

1. **Stop `hci0` advertising HFP-AG/HF**, so the phone never attempts it there. Adapter-scoped SDP
   rather than connection-time rejection. This is the version that matches the design's intent.
2. **Accept on `hci0` but keep the session healthy** — if it must be accepted, find out why the
   session dies after ~47 s rather than letting it fail and disturb A2DP.
3. **Accept and immediately, gracefully hand off** rather than letting BlueZ discover a dead socket.

## What we need from you

- A view on which of the above (or something else) is right — this is your architecture.
- If it changes anything on `hci0`, **update the boundary doc before shipping**; that adapter is
  Radio Console's.

## What Radio Console will NOT do

We will not modify `bt_manager.py` or any RotaryPhone code, and we have not. We also have not
changed anything on `hci0` in response to this.

## Reproducing it

Connect the Pixel to "Grandpas Radio" (`hci0`), play music, and watch:

```bash
journalctl -t bluetoothd -f | grep -iE "RotaryPhone|hci0"
journalctl -f | grep -iE "NewConnection|Duplicate HFP|closing fd"
```

Expect a `NewConnection` on an `hci0` device path within seconds of connecting. The failure follows
tens of seconds later, not immediately, so give it a minute.

## Unrelated, mentioned only so you don't chase it

While in these logs we also found ~55 BT audio **buffer underruns** (`buffer: 0/384000`) and
PipeWire callback execution up to 14.2 ms. That is audio starvation under CPU contention, it is
Radio Console's problem, and it is filed on our side as `AUD-15`. It is **not** related to this
disconnect and should not be conflated with it.
