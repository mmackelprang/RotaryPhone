# Reply — bell-failure surfacing contract ("the phone won't ring")

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.
> This is the **RotaryPhone-side confirmation** of the backend contract requested in §6 of your
> `HANDOFF-bell-failure-surfacing.md`. It is delivered the same way we sent you the SMS `send` and
> mark-read specs. Decision record for the addressing half:
> `docs/architecture/decisions/2026-07-29-ht801-learned-registrar-binding.md`.

---

## TL;DR — what you get

- **`BellInviteFailed`** hub event, **single-DTO shape**, exactly the fields you specified. ✅
- **`BellRecovered`** hub event — your preferred option (a), delivered. **And** the
  `SystemStatusChanged` guarantee (option b) is real, backed by a 30-second reachability probe. ✅
- **`lastBellFailure`** on `GET /api/phone/status` — the reload-survivability requirement. ✅
- **`callId`** on `GET /api/phone/status`. ✅
- **`ht801LastCheckedUtc`** on `GET /api/phone/system-status`, with `Ht801Reachable == null` meaning
  genuinely *unknown*. ✅
- **`POST /api/phone/bell-failure/ack`** — durable dismissal, idempotent. ✅
- **`POST /api/phone/bell/probe`** — **not delivered.** Use the graceful-degradation path you already
  specified: re-fetch, relabel the button `Refresh`. ❌
- **`CallId` on the `CallStateChanged` hub payload** — **declined**, with a concrete reason (§7). Use
  the `callId` on `GET /api/phone/status` instead; you already re-fetch on hub events.
- **Ordering contract stated explicitly** (§8), as you asked: the failure event **may** arrive after the
  call has left `Ringing`.
- **Your Q5 answered** (§9): no, the physical handset cannot answer a call whose INVITE never landed.
  Your on-screen Answer button is the only path. Your §5.1 copy is correct as written.

Your §6.1, §6.3, and §6.4 blockers are all cleared. §6.5 is partially cleared (the DTO field, not the
hub payload). §6.7 is declined and degrades exactly as you planned for.

---

## 1. `BellInviteFailed` — the event (§6.1)

On the existing `RotaryHub` (`/hub`). **Single-DTO shape** — one argument object, matching
`SmsReceived` / `VoicemailReceived`, **not** the two-argument tuple shape of `CallStateChanged`.

```jsonc
// event name: "BellInviteFailed"   — one argument
{
  "phoneId":       "default",
  "callId":        "9f2c1a...",
  "direction":     "Inbound",
  "callerNumber":  "+18015550134",
  "occurredAtUtc": "2026-07-29T20:14:07.812Z",
  "reason":        "Timeout",
  "target":        "192.168.86.240:5060",
  "detail":        "no response to INVITE after 5000ms"
}
```

```csharp
hub.On<BellInviteFailedDto>("BellInviteFailed", OnBellInviteFailed);
```

| Field | Type | Notes |
|---|---|---|
| `phoneId` | `string` | The configured phone Id. `default` on the current single-phone install |
| `callId` | `string` | Correlates with `callId` on `GET /api/phone/status` (§4) |
| `direction` | `string` | `"Inbound"`. Bell health is an inbound-ring concern only — matches your §7n |
| `callerNumber` | `string?` | May be null for a call with no caller ID |
| `occurredAtUtc` | `string` (ISO-8601 UTC) | When the failure was detected, not when the INVITE was sent |
| `reason` | `string` | Closed enum — see below |
| `target` | `string` | Diagnostics only. The **resolved** address the INVITE was actually sent to |
| `detail` | `string?` | Diagnostics only, free text, nullable |

### `reason` — closed enum

```
Timeout · Unreachable · Rejected · NotRegistered · NotConfigured · Unknown
```

Confirmed as you specified. **An unrecognised value must be treated as `Unknown` and must still show
the alert** — your rule, and we agree with it: a reason code we add later must never be able to
suppress a user-facing failure notice. The user-facing copy does not vary by reason, so nothing else
depends on the value.

**Note on `target`:** this is the address the INVITE was *resolved* to, which since PR2 may differ from
the configured address (the service now learns the HT801's real address from its SIP REGISTER). It is
the truthful answer to "where did the ring go," which is what your Diagnostics card wants. Keep your
§3.8 treatment — `reason` and `target` are safe to render, `detail` is untrusted free text and should
stay truncated, non-hero, non-toast.

---

## 2. `BellRecovered` — the recovery signal (§6.2)

**Both forms delivered.** You asked for (a) *or* (b); you get (a), and (b) is now a real guarantee.

**(a) The event:**

```jsonc
// event name: "BellRecovered"   — one argument
{
  "phoneId":       "default",
  "occurredAtUtc": "2026-07-29T20:41:22.104Z"
}
```

**(b) The guarantee:** `SystemStatusChanged` now genuinely fires whenever `Ht801Reachable` **changes
value in either direction**. It is backed by a **30-second reachability probe** rather than being
incidental to some other poll, so a recovery is observed within ~30 s without a call having to happen.
Your existing re-fetch-on-`SystemStatusChanged` plumbing therefore works as a second path.

Use (a) as the primary clear and (b) as the backstop. Your §7h rule stands unchanged — a later call
that rings successfully is still the strongest recovery evidence, and should clear bell health
regardless of either signal.

---

## 3. `GET /api/phone/system-status` (§6.3)

Adds `ht801LastCheckedUtc`:

```jsonc
{
  // ... existing fields unchanged ...
  "ht801Reachable":      true,        // bool? — true | false | null
  "ht801LastCheckedUtc": "2026-07-29T20:32:11Z"   // NEW, nullable
}
```

**`ht801Reachable == null` means genuinely *unknown / not yet probed*, never *false*.** Confirmed and
honoured server-side: the field is null until the first probe completes, and it is not coerced. Render
it as your §3.6 gray `Unknown` pill, never as red `Offline`, and never raise an alert on it (your §7m).

`ht801LastCheckedUtc` is null until the first probe returns, and thereafter carries the timestamp of the
most recent probe regardless of outcome. It powers your `last checked 14:32` sub-line and lets you mark
a stale probe if the value ages past a few probe intervals.

---

## 4. `GET /api/phone/status` (§6.4, §6.5) — reload survivability

Two additions. Together these are what make the signal survive a page reload, a kiosk restart, or a
Blazor circuit drop:

```jsonc
{
  // ... existing fields unchanged ...
  "callId": "9f2c1a...",              // NEW, nullable — null when idle
  "lastBellFailure": {                // NEW, nullable — null when no unresolved failure
    "occurredAtUtc": "2026-07-29T20:14:07.812Z",
    "reason":        "Timeout",
    "callerNumber":  "+18015550134",
    "callId":        "9f2c1a...",
    "failureCount":  2,
    "acknowledged":  false
  }
}
```

| Field | Notes |
|---|---|
| `occurredAtUtc` | Most recent failure |
| `reason` | Same closed enum as §1 |
| `callerNumber` | Nullable |
| `callId` | The failed call — correlates with the hub event and with the top-level `callId` |
| `failureCount` | Consecutive failed rings since the last success. Drives your "2 calls didn't ring…" phrasing |
| `acknowledged` | Reflects `POST .../ack` (§5). Survives restart |

Rehydrate State D from this on first render, per your §7l. `lastBellFailure` is the durable record;
the hub event is the live one. They carry the same `callId`, so a client that receives both does not
need to deduplicate by heuristic.

---

## 5. `POST /api/phone/bell-failure/ack` (§6.6) — delivered

```
POST /api/phone/bell-failure/ack?phoneId=default
→ 200 { "acknowledged": true }
```

- **Idempotent.** Acking an already-acked (or absent) failure returns `200 { "acknowledged": true }`,
  never a 409 and never an error. Retry freely on a flaky network.
- **Durable.** The flag survives a service restart, so your `[ Dismiss ]` is durable across reloads —
  your Q4 concern about a kiosk resurrecting a week-old dismissed note is addressed.
- **Acking clears the note, not the fault.** Exactly your §7i: an ack sets `acknowledged: true` on
  `lastBellFailure` and does **not** touch `Ht801Reachable`. If the ATA is still unreachable your State
  C persists. Dismissing history must not silence a live fault.
- A **new** failure after an ack produces a fresh unacknowledged `lastBellFailure` with an incremented
  `failureCount`.

---

## 6. `POST /api/phone/bell/probe` (§6.7) — NOT delivered

Declined for this PR. There is a 30-second reachability probe running server-side already (§2b), so a
manual probe endpoint buys a bounded amount of freshness for a new mutating route on the call path.

**Take the graceful-degradation path from your own §6.7:** wire `[ Check again ]` to re-fetch
`GET /api/phone/status` and `GET /api/phone/system-status`, and **relabel the button `Refresh`**. With
the 30-second probe behind it, a refresh returns data that is at most 30 s old, which is within the
tolerance your `last checked 14:32` sub-line already communicates.

Your §5.6 accessible name (`Check whether the phone can ring`) should follow the visible label — suggest
`Refresh the phone status`. Your call; the copy is yours.

---

## 7. `CallId` on the `CallStateChanged` hub payload (§6.5) — DECLINED, with reason

**This is the one request we are turning down outright, and the reason is a deploy-time break, not a
preference.**

`CallStateChanged` is sent as **two arguments**, not a DTO:

```csharp
SendAsync("CallStateChanged", phoneId, state)
```

The .NET SignalR client's `JsonHubProtocol` throws `InvalidDataException` when the server sends **more
arguments than the registered handler declares**. Adding a third argument would therefore break **every
existing Radio.Web consumer at the moment of deploy** — not degrade, not warn: throw. The break would
land on whichever side deploys first, and it would look like an unrelated hub failure.

Converting `CallStateChanged` to a single-DTO shape (as `BellInviteFailed` correctly is) would be the
clean fix, but it is the same breaking change with the same coordination cost, and it is not something
to bundle into a bell-failure PR.

**What to do instead:** `callId` is on `GET /api/phone/status` (§4), which **Radio.Web already
re-fetches on hub events**. Correlate through that fetch:

1. `CallStateChanged` arrives → you re-fetch `/api/phone/status` (existing behaviour) → you have the
   current `callId`.
2. `BellInviteFailed` arrives carrying its own `callId` → compare against the `callId` you hold.
3. Match → apply live (State B). No match → the failure belongs to a call that has already ended;
   record the sticky note only, per your §7f.

This resolves the fast-hangup-and-redial ambiguity you raised in §6.5 without any change to
`CallStateChanged`. Your stated fallback ("apply to the current ringing call, ignore if not ringing")
is no longer necessary — you can correlate properly.

If a future PR moves `CallStateChanged` to a single-DTO shape, it will be coordinated across both repos
as its own change, and `callId` goes in at that point.

---

## 8. Ordering contract — stated explicitly, as requested (§6.8)

**`BellInviteFailed` MAY arrive after the call has already left `Ringing`** — answered, rejected, or
timed out. This is stated here in writing so a future refactor on either side cannot quietly start
assuming ordering.

Specifics:

- The `Ringing` state is broadcast **before** the INVITE is attempted (`CallManager.cs:363` sets state;
  the INVITE goes out at `:389`). The failure signal therefore **cannot** precede `Ringing`.
- The **timeout-derived** failure is detected **~5 s after the INVITE is sent**. Socket-level failures
  (`Unreachable`, `NotConfigured`, `NotRegistered`) are detected immediately, but you must not depend on
  that — design for the 5 s case.
- Within that 5 s window the call can be answered on screen, rejected, or the caller can hang up. All
  three produce a `BellInviteFailed` for a call that is no longer ringing.
- **Consumers must handle a failure event for a call that has already ended.** Your §7f already does:
  no live strip, still record the sticky note, never retro-apply an alert to a hero showing a different
  call. That is the correct handling and we are ratifying it as the contract.

`callId` (§4, §7) is what lets you tell "this failure is about the call on screen" from "this failure is
about the call that just ended."

---

## 9. Your Q5 — can the physical handset still answer? **No.**

Confirmed: **if the INVITE never reached the ATA, the ATA has no call, so lifting the handset will not
answer it.** There is nothing for the handset to pick up — the ATA was never told a call exists. Lifting
it produces dial tone, not the caller.

**The on-screen Answer button is the only path.** This makes your §5.5 tooltip changes *required*
rather than polish:

| Button | When bell health is `Failed` / `Suspect` |
|---|---|
| Silence | `The phone isn't ringing` |
| Reject | `Answer or reject on this screen` |

And it confirms your §5.1 copy is right as written — *"answer here on the screen"*, not *"you can also
answer on the screen."* The word *also* would be false.

---

## 10. What we are deliberately NOT changing

**The `Ringing`-before-INVITE race is unchanged** (plan decision D8). `CallManager` will continue to
broadcast `Ringing` before attempting the INVITE, and the call state will remain `Ringing` even when the
bell INVITE fails.

This is deliberate, and it agrees with your §2 reading: **the call genuinely IS ringing** on the inbound
network leg. The caller is connected and waiting; answering on screen works. The only false claim was
the implied one — that the bell is sounding — and that is what `BellInviteFailed` now qualifies. Making
the call state itself `Failed` would be a different and worse lie.

So your degraded-but-live model is the correct one, and it is the one the backend is built to support.
Your §12.2 ("fixing the underlying race" is out of scope) is correct, and it stays out of scope
deliberately rather than by omission.

---

## 11. Cross-reference — verifying the bell address

If you are debugging a real bell failure rather than building the UI for it: **do not use
`/api/phone/system-status` → `ht801IpAddress` to check which address the bell is rung at.** That field
reports a configured projection, not the INVITE target, and it reported the correct address for the
entire duration of the outage that prompted this work. The `target` field on `BellInviteFailed` (§1) is
the resolved address and is trustworthy. On the RotaryPhone side, `GET /api/diagnostics/sip-registrations`
is the authoritative view. Full detail: RotaryPhone `docs/HT801-ADDRESS.md`.

---

## 12. Summary table

| Your ask | Status | Where |
|---|---|---|
| §6.1 `BellInviteFailed`, single-DTO | ✅ Delivered as specified | §1 |
| §6.2 Recovery signal | ✅ Both (a) `BellRecovered` and (b) the `SystemStatusChanged` guarantee | §2 |
| §6.3 `Ht801LastCheckedUtc` + null-means-unknown | ✅ Delivered | §3 |
| §6.4 `lastBellFailure` on `/api/phone/status` | ✅ Delivered | §4 |
| §6.5 `CallId` — on the status DTO | ✅ Delivered | §4 |
| §6.5 `CallId` — on the `CallStateChanged` payload | ❌ **Declined** — would break every existing consumer at deploy | §7 |
| §6.6 `POST .../bell-failure/ack` | ✅ Delivered, idempotent, durable | §5 |
| §6.7 `POST .../bell/probe` | ❌ Not delivered — relabel to `Refresh` and re-fetch | §6 |
| §6.8 Ordering contract in writing | ✅ Stated | §8 |
| §11 Q5 — can the handset answer? | ✅ Answered: **no** | §9 |
