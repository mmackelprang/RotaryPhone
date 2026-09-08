# ADR: Bell-health contract (`XR-5`) — ratification, and the transport-split defect it exposed

- **Status:** Accepted. The contract is **SHIPPED and live on the box**; this ADR ratifies it, corrects
  the record, and decides one **new** defect found while ratifying.
- **Date:** 2026-09-08
- **Author:** Architect
- **Request being answered:** RadioConsole `docs/design-handoffs/HANDOFF-bell-failure-surfacing.md` §6,
  tracked by them as punch-list **`XR-5`** and as `design/FUTURE-WORK.md` §13.
- **Reply already produced:** `docs/handoffs/radioconsole-bell-failure-reply.md` (commit `654a1a8`).
- **Related ADR:** `docs/architecture/decisions/2026-07-29-ht801-learned-registrar-binding.md` — the
  addressing half. This ADR depends on it heavily (§4).
- **Baseline:** `main` @ `3c2c892`. All code citations are as-built on that commit.

> **Record correction, stated first because it changes what this document is for.**
> RadioConsole's `XR-5` says the request *"has never been filed"* and that the first action is
> *"filing the request, which has never happened."* **That is stale.** The handoff was received, the
> contract was designed, a full reply was written (`654a1a8`, 2026-07-29), and **the whole thing was
> built and merged** across `127c032`, `68f1f17`, `a979738`, `af7be67`, `3299b6f`, `92cd52e`,
> `775f19f`, `494e85a`, `915fcf9` — every one of them an ancestor of `main`. It is **running in
> production right now** (§2). `XR-5` is a **record correction on their side, not a build on ours.**

---

## 1. Context

RadioConsole modelled a tri-state bell health indicator (`Ok` / `Suspect` / `Failed` / `Unknown`) and
wired it through their UI, but `BellHealth.Failed` had no producer, so three of their UI states could
never render. The bell is the most physical thing on the machine after the knobs; a failure state with
no producer means it fails silently — which is the exact 2026-07 incident that motivated the work
(96px amber `RINGING` on screen, rotary phone silent for the full 60-second timeout).

They asked for five things. **All five are delivered.** The purpose of this ADR is therefore *not* to
design the contract. It is to:

1. **Ratify** the shipped contract so a future refactor on either side cannot quietly drift from it;
2. **Correct the record** so `XR-5` stops being tracked as unfiled work;
3. **Decide a defect found while ratifying** — the one in §4, which is genuinely new, is not in the
   reply, and materially weakens the feature on the exact path RadioConsole actually consumes.

---

## 2. Delivery status — verified live, not from documents

Measured on the box (`ssh mmack@radio`), 2026-09-08 15:54–15:57 UTC, against the running
`/opt/rotary-phone/RotaryPhoneController.Server`:

```
GET /api/phone/status
{"callState":"Idle","dialedNumber":"","incomingNumber":null,"callId":null,"lastBellFailure":null}

GET /api/phone/system-status
{... "ht801IpAddress":"192.168.86.240","ht801Reachable":true,
     "ht801LastCheckedUtc":"2026-09-08T15:54:44.8980762Z"}
```

| Their ask | Status | Evidence |
|---|---|---|
| §6.1 `BellInviteFailed` hub event, single-DTO | ✅ Shipped | `SignalRNotifierService.cs:159-170` — the sole emission point |
| §6.2 Recovery signal | ✅ Both forms | `BellRecovered` at `SignalRNotifierService.cs:173`; `SystemStatusChanged` at `:404` |
| §6.3 `Ht801LastCheckedUtc` + null-means-unknown | ⚠️ Shipped, **but see §4** | `SystemStatus.cs:68`; tri-state doc at `:53-61` |
| §6.4 `lastBellFailure` on `/api/phone/status` | ✅ Shipped | `PhoneController.cs:96-106`; live above |
| §6.5 `CallId` on the status DTO | ✅ Shipped | `CallManager.cs:102`, minted `BeginCall()` `:332-337`; live above |
| §6.5 `CallId` on `CallStateChanged` payload | ❌ Declined — stands (§5) | reply §7 |
| §6.6 `POST /api/phone/bell-failure/ack` | ✅ Shipped | `PhoneController.cs:111` |
| §6.7 `POST /api/phone/bell/probe` | ❌ Not delivered — stands | reply §6 |
| §6.8 Ordering contract in writing | ✅ Stated | reply §8, ratified in §6 below |

The failure-detection machinery behind the event is real and correlated, not best-effort:
`BellFailureReason` is a closed enum (`Bell/BellFailure.cs:7-20`); `BellFailureTracker` is the single
convergence point both detection paths reach before anything is broadcast
(`SignalRNotifierService.cs:157-158`); and late outcomes are joined back to the right call through
`_inviteOrigins`, keyed on the SIP `Call-ID` (`SignalRNotifierService.cs:49`, rationale at `:41-48`).

---

## 3. Decision summary

| # | Question | Decision |
|---|---|---|
| 1 | Is `XR-5` a build? | **No.** Ratify as shipped; it is a record correction on RadioConsole's side. |
| 2 | **`SystemStatus` means different things over REST and SignalR** | **Fix it — converge REST onto the SignalR probe cache.** The one real change this ADR authorizes (§4). |
| 3 | Is the ~5s predictive-degrade window ours or theirs? | **Theirs, in the UI — but only sound once #2 lands.** Their mechanism is right; our input to it is currently wrong (§4.3). |
| 4 | `CallId` on `CallStateChanged` | **Decline stands** — and the payload is worse than the reply knew (§5). |
| 5 | `GET /api/phone/status` has no named type | **Give it one.** Recommended, not urgent (§7). |
| 6 | `BellFailureReason.NotConfigured` | **Unreachable member** — fix or delete (§7). |

---

## 4. The defect — `SystemStatus` means two different things depending on transport

This is new. It is not in the reply, and it is the reason this ADR exists rather than a one-line
"already done" note.

### 4.1 There are two independent HT801 reachability paths, and they disagree

**Path A — REST, `GET /api/phone/system-status`** (`PhoneController.cs:172-203`):

```csharp
var ht801Config = _ht801Service.GetConfig(defaultPhoneId);   // :187  CONFIGURED address
status.Ht801IpAddress = ht801Config.IpAddress;               // :189
var result = await _ht801Service.TestConnectionAsync(ht801Config.IpAddress);  // :193-198
status.Ht801Reachable    = result.Success;
status.Ht801LastCheckedUtc = DateTime.UtcNow;                //  ← always "now"
```

An **ICMP ping with a 3-second timeout** (`HT801ConfigService.cs:82-124`), run **synchronously inside
the request**, against the **configured** address.

**Path B — SignalR, `SystemStatusChanged`** (`SignalRNotifierService.cs:331-405`):

```csharp
var address = _sipAdapter.ResolveHt801Address(phone.HT801Extension, phone.HT801IpAddress, ...); // :344
```

A **genuine 30-second background probe** (`Ht801ProbeInterval`, `:31`) against the **resolved /
learned registrar binding** — the address the INVITE will actually go to — with cached
`_ht801LastCheckedUtc` (`:33`) and a broadcast only when the value *changes* (`:368`, `:374-379`). Its
own comment (`:333-342`) says `GetConfig` is "unusable here" because it is a last-wins projection.

So, for the same `SystemStatus` type:

| Field | Over REST | Over SignalR |
|---|---|---|
| `Ht801IpAddress` | **Configured** address | **Resolved / learned** address |
| `Ht801Reachable` | Ping of the **configured** address | Probe of the **resolved** address |
| `Ht801LastCheckedUtc` | **Always `DateTime.UtcNow`** | A real cache age |

### 4.2 Verified live — the REST timestamp is a response timestamp, not a probe timestamp

Two calls 10 ms apart returned **different** values; then 40 seconds of silence produced a value equal
to the moment of asking, proving no background probe had run behind that endpoint:

```
15:55:45.6352455Z   }  back-to-back, 10 ms apart → different
15:55:45.6452160Z   }
<40s with no requests>
now=15:56:25Z   →   15:56:25.6569741Z      (equal to the request instant)
```

**Consequence:** `Ht801LastCheckedUtc` over REST can never be stale, so RadioConsole's §6.3
`last checked 14:32` sub-line renders the current time forever, and their "mark a stale probe"
affordance is **dead on arrival — and confidently so**, because it looks like it is working.

### 4.3 The serious half: they are consuming the signal that was green during the outage

RadioConsole's `BellHealthService` polls **`GET /api/phone/system-status`** every 15 s — Path A. Their
`Suspect` state, and therefore their entire predictive-degrade rule, is derived from a ping of the
**configured** address.

Our own code says, in the XML doc on that very endpoint (`PhoneController.cs:152-168`):

> *"This endpoint reports the CONFIGURED HT801 address, not the INVITE target… **It reported the
> CORRECT address throughout the entire 2026-07 outage while every INVITE went to a stale one**, so it
> is NOT a valid verification signal for addressing."*

**That is the outage this feature exists to prevent.** As shipped, on the path RadioConsole actually
polls, predictive-degrade would **not** have fired during the incident that motivated it. The reply
promised a "30-second reachability probe" behind the recovery guarantee; that probe is real, but it
sits behind the SignalR path, not the REST one they poll.

### 4.4 Decision — converge Path A onto Path B's cache

**Options considered:**

- **(a) Status quo.** Zero work. Rejected: it leaves a feature that is confidently wrong in exactly the
  scenario it was built for, and leaves one DTO with two meanings — a divergence class that will be
  re-discovered by whoever debugs the next bell outage.
- **(b) Converge REST onto the SignalR probe cache — CHOSEN.** `GetSystemStatus` stops pinging in-request
  and instead reads the cached `_ht801Reachable` / `_ht801LastCheckedUtc` / `_ht801ProbedAddress` that
  `SignalRNotifierService` already maintains. One meaning per field, on both transports.
- **(c) Converge SignalR onto per-request probing.** Rejected outright: it would move the *wrong*
  (configured-address) semantics onto the path that is currently correct, and destroy change-detection —
  `BellRecovered` and the `SystemStatusChanged` guarantee both depend on a cached previous value.

**Why (b):**

1. **It makes `Suspect` genuinely predictive.** Once reachability refers to the resolved binding, an
   unreachable ATA is unreachable *at the address the INVITE will use*, so `Suspect` becomes a real
   predictor of `Failed`. This is what makes RadioConsole's predictive-degrade rule sound (§6).
2. **It makes `Ht801LastCheckedUtc` mean what its name says** — a probe age, so their staleness
   affordance starts working, unchanged on their side.
3. **It removes a 3-second blocking ping from the request path.** Today, `TestConnectionAsync` awaits
   `SendPingAsync(ip, 3000)` inline. Measured healthy latency is ~3 ms, but when the ATA is
   **unreachable** the endpoint blocks for up to 3 s — for every polling client, every 15 s, *precisely
   when the bell is broken*. (Derived from code, not measured under fault; see Open Questions.)
4. **The cache already exists and is already correct.** This is a deletion plus a read, not new
   machinery.

**Cost accepted:** REST reachability becomes up to 30 s stale rather than instantaneous. That is
strictly better than instantaneously wrong, it is what `Ht801LastCheckedUtc` is *for*, and 30 s is well
inside the tolerance their own `last checked` sub-line communicates. Second cost: before the first
probe completes, REST returns `Ht801Reachable = null` — which is **correct** ("not yet probed") and is
already the contracted tri-state (`SystemStatus.cs:53-61`), but it does mean a cold-start window now
reports `Unknown` where it previously reported a fast, possibly-wrong `true`. Their §7m rule already
requires `null` to render as gray `Unknown` and never alarm, so this needs no UI change — but they
should be told, because it is a visible behaviour change at boot.

**This is the only build item this ADR authorizes.** It is a RotaryPhone-internal change: **no wire
shape changes, no field is added or removed, and no RadioConsole code has to change.**

---

## 5. `CallId` on `CallStateChanged` — the decline stands, and the payload is worse than we thought

The reply (§7) declined adding `CallId` to the `CallStateChanged` hub payload because it is sent as two
arguments and the .NET SignalR client throws `InvalidDataException` when the server sends more arguments
than the handler declares. **That reasoning holds and the decline stands** — `callId` is on
`GET /api/phone/status`, which RadioConsole already re-fetches on hub events, and correlation works
through that.

**Newly found, and not in the reply:** `CallStateChanged` is already emitted with **two mutually
incompatible encodings on the same hub**:

- `RotaryHub.cs:18` sends the raw `CallState` **enum** (serialized as a *number*),
- `SignalRNotifierService.cs:255` sends `manager.CurrentState.ToString()` — a **string**.

RadioConsole's client is written against the string form. The enum form is emitted from a
client-invokable hub method, so it is reachable. **This is a latent cross-service defect independent of
the bell work**, and it should be fixed before anyone converts `CallStateChanged` to the single-DTO
shape the reply contemplates — converting it would be the natural moment to unify, and doing both at
once is one coordinated break instead of two. Flagged here; **not decided in this ADR** (it needs the
owner and a coordinated deploy — see Open Questions).

---

## 6. The ~5-second window — theirs to solve, and now genuinely solvable

RadioConsole asked, and the dispatch asks again, whether the predictive-degrade case is ours to fix in
the contract or theirs to fix in the UI.

**Decision: theirs, in the UI. The reply's §10 position is ratified — with the §4 correction attached.**

The ordering is contractual and deliberate. `CallManager` broadcasts `Ringing` at `:433`/`:484` **before**
the INVITE is attempted (`:440`, `:521`), and the timeout-derived failure lands ~5 s later
(`SipDiagnosticService.InviteTimeout = 5s`, `:24`, checked on a 3-second timer, `:384`). So
`BellInviteFailed` **can never precede `Ringing`**, and may arrive after the call has already left it.

**Why we do not close the window on our side:**

- **Reordering — sending the INVITE before broadcasting `Ringing` — is actively harmful.** It would delay
  the on-screen `Ringing` by up to 5 s, and when the bell is dead the screen is *the only answer path*
  (reply §9: the ATA has no call, so lifting the handset does nothing). We would be delaying the one
  working affordance in order to be more accurate about the broken one.
- **The call state is not lying.** Plan decision **D8** (`CallManager.cs:372-378`): the call genuinely *is*
  ringing on the network leg; the caller is connected and answering on screen works. Only the *implied*
  claim — that the bell is sounding — was false, and `BellInviteFailed` is what qualifies it. Making the
  call state itself `Failed` would be a different and worse lie.
- **A new "attempt started" event would not help.** It cannot report a failure sooner; it only adds a
  third event and more ordering surface.

**But the mechanism they chose only works if `Suspect` is a real predictor**, and today it is not — it is
a ping of the configured address, green throughout the outage (§4.3). **So the split is: the rule is
theirs, the signal is ours, and our half is currently broken.** §4.4 fixes our half. After it lands,
predictive-degrade covers the common case (ATA genuinely unreachable) with no blind window, and the
residual uncovered case narrows to "ATA reachable at probe time but rejects or ignores the INVITE" —
where a ~5 s window remains and their §7f handling (record the sticky note, no live strip) is correct.

**We must tell them this**, because the reply implied the input was sound and it is not.

---

## 7. Smaller findings — recorded, not all decided

1. **`GET /api/phone/status` has no named type.** It is `Ok(new { ... })` (`PhoneController.cs:58-88`)
   with **PascalCase** top-level keys and a **hand-written camelCase** nested `lastBellFailure`
   (`:96-106`). Nothing is compile-checked; a rename silently breaks RadioConsole.
   **Recommend** a named record with `[JsonPropertyName]` on every field, as
   `GvBridgeStatusDto` already does for exactly this reason. Note the live response shows *camelCase*
   top-level keys, so a serializer policy is normalizing them — which means the casing that reaches the
   wire is incidental rather than pinned. Not urgent; do it before the next field is added.
2. **`BellFailureReason.NotConfigured` is unreachable.** Nothing produces it: the no-SIP-transport path
   (`SIPSorceryAdapter.cs:591-595`) returns `false`, which `RecordBellInviteFailure` records as
   `Unreachable` (`CallManager.cs:390`). Either produce it or delete it — a closed enum with a dead
   member invites a consumer to write handling that can never run.
3. **`/api/phone/*` is unauthenticated by design.** `GvBridgeAuthMiddleware` gates only
   `/api/gvbridge*` (`:34`), asserted by a regression test that pins `/api/phone/status` as ungated
   (`GvBridgeAuthMiddlewareTests.cs:35-36`). Correct for LAN-only today; **it means the bell contract
   inherits no auth**, unlike every gvbridge route. Worth stating so nobody assumes parity.
4. **`BellFailureTracker` is in-memory and deliberately not persisted** (plan D5,
   `BellFailureTracker.cs:35-42`). So `acknowledged` survives a *reload* but **not a service restart** —
   the reply §5 says "durable… survives a service restart," which is **stronger than the code**. Their
   Q4 concern (a nightly-restarting kiosk resurrecting a dismissed note) is therefore **not** fully
   addressed. **This needs the owner** — it is a real, if minor, promise/behaviour gap (see below).

---

## 8. Consequences

**Good:**
- The contract is ratified against as-built code and live production output, not against documents.
- `XR-5` stops being tracked as unfiled work on their side; the reply is already written and needs only
  delivery plus the §4/§6/§7 corrections.
- §4.4 makes one DTO mean one thing on both transports, makes `Suspect` genuinely predictive, makes
  their staleness affordance work, and removes a blocking ping from the request path — with **no wire
  change and no RadioConsole code change**.

**Bad / costs:**
- REST reachability becomes up to 30 s stale, and reports `null` (`Unknown`) during the cold-start window
  before the first probe. Both are correct-by-contract, but both are visible behaviour changes.
- The `CallStateChanged` dual-encoding defect (§5) is now known and unfixed. Leaving it is a deliberate
  deferral, not an oversight.
- Two statements in the delivered reply are stronger than the code: the "30-second probe" (true only of
  the SignalR path) and "survives a service restart" (§7.4). Both must be corrected in the follow-up, or
  the reply becomes another instance of the failure class this project already tracks — a document
  asserting more than the code does.

**Neutral / explicitly unchanged:**
- The `Ringing`-before-INVITE ordering (D8) stays, deliberately.
- `POST /api/phone/bell/probe` stays undelivered; the `Refresh` degradation stands.
- `CallId` on the `CallStateChanged` payload stays declined.

---

## 9. What needs the owner rather than the Architect

1. **The §4.4 convergence has a visible cold-start change on RadioConsole's screen** (`Unknown` pill at
   boot instead of a fast `true`). It needs no code from them, but they should get to weigh it.
2. **§7.4 — `acknowledged` does not survive a service restart**, contradicting the delivered reply and
   leaving their Q4 concern open. Persisting the tracker is a small change but a **behavioural promise**
   to another team; the owner should decide whether to persist it or to correct the promise.
3. **§5 — unifying `CallStateChanged`** is a coordinated cross-repo breaking deploy. Not schedulable
   from this side alone.

---

## 10. Related decisions

- `docs/architecture/decisions/2026-07-29-ht801-learned-registrar-binding.md` — the resolved-address
  machinery §4.4 converges onto.
- `docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md` — the cross-service contract
  convention this ADR follows.
- `docs/handoffs/radioconsole-bell-failure-reply.md` — the delivered reply, to be amended per §4/§6/§7.
- `docs/HT801-ADDRESS.md` — why the configured address is not a trustworthy verification signal.
- `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` — **no Change Log row needed for this ADR.** It is
  not a BT/audio ownership change and it changes no wire shape. If §4.4 ships, one row is warranted
  noting that REST reachability semantics converged onto the resolved address.

## 11. Open questions

1. **The 3-second blocking ping under fault is derived from code, not measured.** `SendPingAsync(ip, 3000)`
   awaited in-request implies up to 3 s of added latency per call when the ATA is unreachable. Worth
   confirming on the box during the next real bell outage rather than manufacturing one. Does not block
   §4.4 — convergence removes the ping either way.
2. **How frequently does the HT801 actually re-register?** `learnedAtUtc` moved between samples 6 s apart
   while `expiresSeconds` is 3600. Confirmed *not* a read-time stamp (two back-to-back reads returned an
   identical value), so the binding is genuinely refreshing far more often than the expiry implies.
   Harmless, possibly a symptom of the point-to-point link. Unrelated to this contract; recorded so the
   next reader does not mistake it for a bug in `RegistrarBinding`.
3. **Owner decisions in §9** — the cold-start change, tracker persistence, `CallStateChanged` unification.
