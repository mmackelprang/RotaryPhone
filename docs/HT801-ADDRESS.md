# The HT801 address — where it lives, how to change it, how to verify it

The HT801 ATA is the device that rings the rotary phone's bell. Sending a SIP INVITE to the wrong
address is silent: the call still rings on the network leg, the UI still says "Ringing", and nothing
in the system complains. This document is the single place that records where that address comes
from and how to check it.

---

## 1. TL;DR

| | |
|---|---|
| **Current address** | `192.168.86.240` |
| **MAC** | `ec:74:d7:88:45:05` |
| **DHCP** | Pinned to this address on the router — it should not move on its own |

Since PR2 the service also **learns** this address from the HT801's own SIP REGISTER, so a DHCP move
self-heals within one registration interval (~50 minutes) without any config edit.

---

## 2. The one place to change it

```
RotaryPhone:Phones[].HT801IpAddress
```

Two files carry that key:

- **On the box** — `/opt/rotary-phone/appsettings.Production.json`. This is the authoritative one.
  `Deploy-ToLinux.ps1` **preserves** it across deploys.
- **In the repo** — `src/RotaryPhoneController.Server/appsettings.Production.json`. The template used
  for a fresh install, and what anyone reading the repo will believe.

There is no second key. `GVBridge:HT801Ip` was deleted in PR2 (§3).

---

## 3. Every location the address can appear

| Location | Role |
|---|---|
| `appsettings.Production.json` → `RotaryPhone:Phones[].HT801IpAddress` | **Authoritative.** Preserved across deploys |
| `appsettings.json` → same key | Dev/template default. **Overwritten by every deploy** — never hand-edit this on the box |
| Learned registrar binding (runtime, in-memory) | **Wins at runtime** when fresh; self-heals DHCP moves. Never persisted |
| `HT801Config.IpAddress` (runtime, `HT801ConfigService`) | Projection for the UI/diagnostics. Last-wins over `Phones`. **Reporting only — not the INVITE target** |
| ~~`GVBridge:HT801Ip`~~ | **Removed in PR2** — it was a duplicate source of truth, and it lived only in the deploy-overwritten file |

Since PR2 there is also no site-specific HT801 address compiled into source. `AppConfiguration.HT801IpAddress`
and `HT801Config.IpAddress` default to `""`, and startup validation rejects an empty or unparseable
value (§6).

---

## 4. Procedure to change the address

With PR2 this is usually unnecessary — the learned binding tracks the device on its own, and the
configured value is only the cold-start fallback for the window between service start and the first
REGISTER. Do it anyway when the device is moved deliberately, so a cold start rings the right place.

1. Edit the authoritative file on the box:

   ```bash
   sudo nano /opt/rotary-phone/appsettings.Production.json
   # RotaryPhone → Phones → [0] → HT801IpAddress
   ```

2. Restart the service:

   ```bash
   sudo systemctl restart rotary-phone.service
   ```

3. Verify per §5. Do **not** verify with `/api/phone/system-status`.

If the service refuses to start with `Fatal: Invalid RotaryPhone configuration`, read the message —
it names the key and the file. That is startup validation catching a real problem (§6), not a
regression.

---

## 5. How to verify correctly

### Valid signals

| Signal | Where | What it proves |
|---|---|---|
| `CallManager sending INVITE to 1000@<ip>` | journal | A ring was attempted (see the caveat below on which address this prints) |
| `INVITE target endpoint: udp:<ip>:5060` | journal | **The address the datagram actually went to.** Authoritative |
| `Learned registrar binding: <aor> -> <ip>:5060` | journal | Where the device says it is, from the source address of its REGISTER |
| `GET /api/diagnostics/sip-registrations` | REST | The current learned bindings — i.e. where INVITEs will actually go |

```bash
sudo journalctl -u rotary-phone.service -f
curl -s http://radio:5004/api/diagnostics/sip-registrations | jq
```

### Valid signal (negative)

The **absence** of `Phone default is already registered` at startup. That line meant a duplicate
phone entry was being silently discarded — the original bug (§7). Since PR1 a duplicate Id is fatal
rather than warned about, so the line should never appear again.

### NOT VALID — `/api/phone/system-status` → `ht801IpAddress`

> **This field reports the configured projection, not the INVITE target.** It reads
> `HT801ConfigService` (last-wins over `Phones`), while the INVITE resolves its target separately.
> The two are different values and can disagree.
>
> **During the 2026-07 outage this field reported the correct `.240` for months while every single
> INVITE went to `.22`.** It is the reason the bug survived so long. Do not use it to verify
> addressing — use the four signals above.

### What a configured/learned mismatch looks like

The warning names the problem; every line after it shows the address traffic actually went to:

```
Configured HT801 address 192.168.86.99 does not match the learned address 192.168.86.240 —
  using the learned address. Update RotaryPhone:Phones[].HT801IpAddress
CallManager sending INVITE to 1000@192.168.86.240 (SDP RTP port 49000)
INVITE target endpoint: udp:192.168.86.240:5060
SCO audio connected for ... — starting RTP bridge to 192.168.86.240:49000
```

The warning is the **only** place the stale configured value appears, and it names the key to fix.

**The bell line and the audio line must agree.** Both are resolved once per call, from the same
resolver, and the result is cached for the duration of the call — so they cannot diverge. If you
ever see `INVITE target endpoint` and `starting RTP bridge to` pointing at different hosts, that is
a bug, not a quirk.

> An earlier draft of this document described a "deliberate quirk" in which `CallManager sending
> INVITE to ...` printed the *configured* address while the INVITE went somewhere else. That was
> real, and it was removed before this shipped: a journal line printing an address the datagram
> never went to is precisely the kind of half-truth that let the original bug hide for months.

---

## 6. What now fails fast

PR2 extends startup validation (`AppConfigurationValidator`). The service **refuses to start** —
`Fatal` log, non-zero exit — on any of:

- no phones configured under `RotaryPhone:Phones`
- duplicate phone `Id`s
- a missing or unparseable `HT801IpAddress`
- a missing `HT801Extension`

This is deliberate. A service that refuses to start is a strictly better failure than one that
reports "Ringing" while ringing an address nobody lives at.

---

## 7. The historical bug

In July 2026 inbound calls showed **Ringing** in the Radio.Web UI while the physical bell stayed
silent. `AppConfiguration.Phones` was pre-seeded with one element carrying a hardcoded
`192.168.86.22`, and .NET's `ConfigurationBinder` **appends** to a non-null `List<T>` rather than
replacing it — so a single-phone config produced *two* phones, and first-wins registration kept the
hardcoded one. Every INVITE went to `.22`. **No edit to any configuration file could fix it**, and
`/api/phone/system-status` reported the correct address throughout, which is why it went undiagnosed
for months. Fixed in PR1 (#67, empty seed list + fail-fast on duplicates) and hardened in PR2
(no site IPs in source, fail-fast validation, learned registrar bindings). Full analysis:
[`docs/plans/ht801-address-resolution-and-config-binder-fix.md`](plans/ht801-address-resolution-and-config-binder-fix.md).

The device has held **three** addresses over the project's life — `192.168.86.250` (Raspberry Pi
era), `192.168.86.22`, and now `192.168.86.240` — and **every one of them was at some point compiled
into source**. That, more than any individual outage, is the argument for learning the address from
the device rather than configuring it. Test fixtures were moved to the RFC 5737 documentation range
(`192.0.2.x`) in PR2 so that a future grep for a production address returns only real configuration.

---

## 8. Still hardcoded (known, out of scope for PR2)

Two site-specific literals remain in source. **Neither is an HT801 address** — both are *local/server*
address fallbacks, and both sit on an exception path that only runs when the normal lookup throws:

| Location | Literal | What it is |
|---|---|---|
| `src/RotaryPhoneController.Core/SIPSorceryAdapter.cs:810` | `192.168.86.50` | `GetLocalIPForTarget` returns this when the UDP-connect probe throws |
| `src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs:944-947` | `192.168.86.50` | GV media IPv4 bind fallback, same literal, same shape |

Neither affects HT801 addressing, so neither could produce the failure in §7. Both are candidates for
a follow-up that resolves the local address the same way the HT801 address is now resolved.

(For completeness, `src/RotaryPhoneController.Server/Program.cs:100` also carries a site-specific
literal — `http://192.168.86.55:5173`, a dev CORS origin. Not an address fallback, not phone-related.)
