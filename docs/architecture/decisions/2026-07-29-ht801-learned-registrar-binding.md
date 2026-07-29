# ADR: HT801 address resolution — learned registrar bindings

- **Status:** Accepted (implemented in PR2, branch `feat/ht801-registrar-binding`)
- **Date:** 2026-07-29
- **Author:** Architect (PR2)
- **Plan:** `docs/plans/ht801-address-resolution-and-config-binder-fix.md` (decisions **D3**, **D4**, **D5**)
- **Precedes / relates to:** PR1 (`fix/ht801-invite-target`, #67) — the config-binder fix that restored
  the bell. This ADR covers the durable half.
- **Operator-facing companion:** `docs/HT801-ADDRESS.md` — where the address lives, how to change it,
  how to verify it.

---

## 1. Context

The HT801 ATA is the device that rings the rotary phone's bell. Its address is the target of every SIP
INVITE on the inbound path. Getting it wrong is **silent**: the call still rings on the network leg, the
UI still says "Ringing", and no error is raised — the bell simply does not sound.

That failure mode shipped. In July 2026 every inbound INVITE went to `192.168.86.22` while the device
lived at `192.168.86.240`, because a hardcoded default in `AppConfiguration` was appended ahead of the
real configuration by the .NET `ConfigurationBinder` and won a first-wins registration race. PR1 fixed
that specific defect. It did not address the underlying property that made it possible: **the address
was a static value that something had to keep in sync with reality, and nothing did.**

The device has held **three** addresses over the project's life — `192.168.86.250` (Raspberry Pi era),
`192.168.86.22`, and now `192.168.86.240` — and every one of them was at some point compiled into
source. A fourth was still in test fixtures. Any design where the correct address is a value a human
must remember to update will eventually be wrong again, and will be wrong silently.

The HT801 already tells us where it is, roughly every 50 minutes, in the form of a SIP REGISTER. We were
answering those REGISTERs and throwing the information away.

---

## 2. Decision (summary)

| # | Question | Decision |
|---|----------|----------|
| **D3** | What do we learn the address *from*? | The **source address of the REGISTER**, not the Contact header. Contact is recorded for diagnostics; a warning is logged when the two disagree |
| **D4** | Which wins, learned or configured? | A **fresh learned binding always wins.** Configuration is the cold-start fallback and the stale fallback. A mismatch logs a warning naming the config key |
| **D5** | Do we persist bindings? | **No.** In-memory only, never written to disk |

Freshness is `expires` (as requested by the device, default 3600s) **+ 5 minutes grace**.

---

## 3. D3 — learn from the REGISTER's source address, not the Contact header

**Decision: bind to `remoteEndPoint.Address`. Record `ContactURI.Host` for diagnostics only. Log a
warning when they disagree.**

RFC 3261 nominates the Contact URI as the address at which a registered endpoint can be reached, and on
a correctly configured device that is what it contains. But Contact is a value the device *asserts*
about itself: a Grandstream ATA can be configured with a stale host, or be NAT-confused, and will then
advertise an address it cannot be reached at. The source address of the REGISTER is not an assertion —
it is the address a datagram **provably just arrived from**. It cannot be stale, because a stale value
could not have delivered the packet.

On the deployment this system actually runs on — a flat LAN, no NAT, one ATA — the two values are
identical, so preferring the source address costs nothing on the happy path and is strictly more robust
on the failure path. That is the whole argument: same result when things are fine, better result when
they are not.

Contact is not discarded. It is stored on the binding and surfaced by
`GET /api/diagnostics/sip-registrations`, and a disagreement produces:

```
REGISTER Contact host <contact> differs from source address <source> — using the source address for INVITEs
```

which is exactly the diagnostic someone would want if an ATA were ever misconfigured.

### Rejected alternative — prefer Contact, fall back to the source address

Identical behaviour on the happy path (the values match), worse behaviour on the only path that
matters: a device advertising a bad Contact would be believed, and the INVITE would go nowhere. The
fallback would never engage, because a wrong-but-present Contact is not a missing Contact. Rejected.

---

## 4. D4 — a fresh learned binding beats configuration, always

**Decision: when a fresh learned binding exists, it is the INVITE target. The configured address is the
cold-start fallback and the stale fallback.**

Resolution happens at a single chokepoint, `ISipAdapter.ResolveHt801Address`. Its callers are
`SendInviteToHT801` (as a backstop for anything that did not pre-resolve), `CallManager`,
`DiagnosticsController.TestRing`, the GV audio bridge and the HT801 reachability probe — so every leg
of a call, and every diagnostic that claims to report the target, gets the same answer. A divergence
between the "ring test" and a real call was itself part of the original bug's camouflage, and a
divergence between the bell and the audio bridge is the same failure class one leg over.

| Situation | Target used | Logged |
|---|---|---|
| Fresh binding, matches configuration | Learned | — |
| Fresh binding, **disagrees** with configuration | **Learned** | `Warning` naming `RotaryPhone:Phones[].HT801IpAddress` |
| No binding yet (before the first REGISTER) | Configured | `Warning` — "falling back to configured HT801 address …" |
| Binding stale | Configured | `Warning` with the learn time and expiry |

**Freshness = the device's requested `expires` (default 3600s) + 5 minutes grace.** The grace is
load-bearing rather than decorative: the HT801 re-registers at roughly 50% of the expiry interval, so a
binding is normally refreshed at ~30 minutes into a 60-minute window. A single dropped or delayed
refresh must not invalidate a binding that is almost certainly still correct — expiring at exactly
`expires` would make one lost packet fall back to a configured value that may well be the stale one.

Configuration is deliberately kept, and deliberately still validated at startup (missing or unparseable
addresses are fatal). It is what rings the bell in the window between service start and the first
REGISTER, which after a restart can be up to ~50 minutes.

The mismatch warning is the mechanism by which configuration drift becomes *visible* rather than
*harmful*. Before this change, a wrong configured value produced silence. After it, the bell rings and
the log tells an operator exactly which key to correct.

---

## 5. D5 — bindings are in-memory only, never persisted

**Decision: `RegistrarBindingStore` is an in-memory `ConcurrentDictionary`. Nothing is written to disk.**

Persisting looks attractive — it would close the cold-start window — but it is the wrong trade:

- **A binding learned before a restart may be stale.** The service is most likely to be restarted
  precisely when something changed, including the network.
- **The window it would close is bounded and self-closing.** The device re-registers within ~50 minutes
  of any restart, and the configured fallback covers the interval.
- **It would recreate the defect being fixed.** The bug in §1 was a second, unsynchronised store of the
  HT801's address that outlived the reality it described. A persisted binding file is exactly that —
  and worse, an *invisible* one, since nobody would think to look in it. Persisting would trade a
  self-correcting cache for a second stale-address store.

A cache that forgets on restart is a cache that cannot lie for longer than one registration interval.
That property is the point.

---

## 6. Consequences

### Positive

- A DHCP move self-heals within one registration interval with no human action and no deploy.
- There is exactly one HT801 address key in the system (`RotaryPhone:Phones[].HT801IpAddress`);
  `GVBridge:HT801Ip` — which lived only in the deploy-overwritten `appsettings.json` — is deleted.
- No site-specific HT801 address remains compiled into source, so the class of bug in §1 cannot recur
  through a hardcoded default.
- Where an INVITE will go is now **observable** via `GET /api/diagnostics/sip-registrations`, which is
  what `/api/phone/system-status` was mistakenly assumed to be.
- Configuration drift produces a warning that names the key, instead of silence.

### Residual risks (carried forward from plan §7)

| Risk | Assessment / mitigation |
|---|---|
| **A spoofed REGISTER on the LAN** could teach us a wrong address | Accepted. The deployment is a single-ATA, no-NAT home LAN; an attacker able to send SIP to the service from that LAN has better options than misdirecting a doorbell-grade INVITE. Mitigated in depth by the freshness bound (a spoof must be sustained), the configured/learned mismatch warning (a spoof is loudly logged), the observable `sip-registrations` endpoint (a spoof is inspectable), and the configured fallback (a spoof that stops is corrected within one expiry window). No SIP authentication on REGISTER is added here; if the threat model ever changes, digest auth on REGISTER is the correct fix, not abandoning learned bindings |
| **`GetSingle()` ambiguity** if a second SIP device ever registers | By design, `GetSingle()` returns `null` when more than one binding exists and no AOR matches — the resolver then falls back to configuration rather than guessing. Covered by `Resolve_DoesNotGuess_WhenMultipleBindingsAndNoAorMatch`. This is a deliberate degradation to today's behaviour, not a failure. It is also **uniform**: every caller (bell, RTP bridge, GV audio bridge, ring test, reachability probe) reaches it through `ResolveHt801Address`, which tries `Get(extension)` before `GetSingle()`. Reimplementing the tail elsewhere would break that uniformity — a `GetSingle()`-only copy falls back to configuration in the two-binding case where the resolver would still match on the AOR |

`GetSingle()` exists because the HT801 registers under the AOR `rotaryphone` while we ring extension
`1000`. An exact-AOR-match-only lookup would have compiled cleanly, passed unit tests, and never
engaged in production — the self-healing would have been decorative.

### Costs accepted

- Cold start still depends on configuration being correct, for up to ~50 minutes.

---

## 7. Related decisions

- **D1 / D2** (two PRs; fail-fast on duplicate phone Ids) — plan §4, implemented in PR1 #67.
- **D6** (single address key; `GVBridge:HT801Ip` deleted) — plan §4, implemented in PR2 alongside this ADR.
- **D7** (test fixtures moved to RFC 5737 `192.0.2.x`) — so a grep for a production address returns only
  real configuration.
- **D8** (UI honesty about a failed bell INVITE; the `Ringing`-before-INVITE race is deliberately
  unchanged) — see `docs/handoffs/radioconsole-bell-failure-reply.md`.

## 8. Open questions

1. **SIP authentication on REGISTER.** Not implemented; the LAN threat model does not currently justify
   it. Revisit if the service is ever exposed beyond the home LAN, or if a second SIP endpoint is added.
2. **Multi-device deployments.** `GetSingle()` degrades safely to configuration, but a genuine
   two-phone install would want AOR-to-phone mapping in configuration rather than a single-binding
   heuristic. Not needed today; the shape of the fix is clear if it ever is.
3. **The remaining local-address fallbacks** (`SIPSorceryAdapter.cs:701` and `GvSipTransport.cs:944-947`,
   both `192.168.86.50`) are *local/server* addresses on exception paths, not HT801 addresses. Out of
   scope for PR2; a candidate follow-up. Listed in `docs/HT801-ADDRESS.md` §8 so they are not forgotten.
