# Radio Console ↔ RotaryPhone BT Audio Boundary

> **Purpose:** This is a shared boundary contract between two Claude sessions working in
> parallel on the same Ubuntu box. Radio Console (D:\prj\RTest\RTest) owns music/A2DP.
> RotaryPhone (D:\prj\RotaryPhone) owns voice/HFP. Neither side should modify the other's
> adapter, profiles, or WirePlumber configs without updating this document.
>
> **Canonical location:** `D:\prj\RotaryPhone\docs\prompts\RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`
> **Last updated:** 2026-09-08 by RotaryPhone session (Builder — bell/phone-status contract: REST `system-status` reachability converged onto the resolved address, and a dismissed bell note now survives a restart; **no BT or audio behavior change, no wire shape change, nothing required from Radio Console**). ⚠️ Two items for Radio Console in the newest Change Log row: **`ht801IpAddress` is newly nullable over REST** at cold start, and the **`bell-failure/ack` response body contradicts the delivered reply** (`{"acknowledged": false}` on a repeat ack where reply §5 promised `true`) — pre-existing and deliberately unfixed, needs an owner call. Earlier: XR-6 voicemail-blackout 404 fix; **no BT or audio behavior change**. ⚠️ Also still open: **`gv-bridge-ensure.sh` always exits 0, even when the launch fails** — see Change Log 2026-09-08 before binding a `VOICE` amber indicator to its exit code. Still open from earlier entries: the `authBlackout` sub-second caveat under Integration Points, and the deploy hazard that can rewrite RotaryPhone's `BluetoothAdapter` setting under Operational Notes. Also backfilled here: rule 8 (HT801 point-to-point cable) and the orphaned 2026-09-06 / 2026-09-07 Change Log rows, which had been sitting uncommitted in a stale working tree.
>
> **If you need to change any boundary (adapter assignment, WP config, profile ownership),
> update this document first, then coordinate with the other session.**

---

## System Overview

Two services share the same Ubuntu box (`radio`, Intel N100):

| Service | Port | Purpose | BT Adapter |
|---------|------|---------|------------|
| **Radio.API** (Radio Console) | 5000 | A2DP music streaming, AVRCP, PBAP, audio engine | **TP-Link UB500** (`hci0`, `78:20:51:F5:FB:A7`) |
| **RotaryPhone.API** | 5004 | SIP calls, HT801 ATA, HFP voice, phone call UI | **Intel AX201** (`hci1`, `10:91:D1:FE:00:46`) |

**The adapters are dedicated. Never cross them.**

---

## Adapter Assignment

### TP-Link UB500 (`hci0`, MAC: `78:20:51:F5:FB:A7`) — MUSIC ONLY

**Owned by:** Radio Console (Radio.API)
**Alias:** "Grandpas Radio"
**Profiles:** A2DP Sink, A2DP Source, AVRCP, PBAP, HSP, HFP-AG
**NOT available:** HFP-HF (removed from WirePlumber to free it for RotaryPhone)

Radio Console manages this adapter via BlueZ D-Bus. WirePlumber audio policies only apply to this adapter (`bluez5.default.adapter` is set to its MAC).

**Do NOT:**
- Connect RotaryPhone services to this adapter
- Register any BlueZ agents or profiles on this adapter
- Change WirePlumber configs that affect `bluez_card.*` rules (they apply to this adapter)
- Call `bluetoothctl` without `select 78:20:51:F5:FB:A7` first (the Intel adapter may be the default)

### Intel AX201 (`hci1`, MAC: `10:91:D1:FE:00:46`) — VOICE ONLY

**Owned by:** RotaryPhone.API
**Alias:** "Grandpas Phone"
**Profiles:** HFP-HF, HFP-AG (whatever RotaryPhone needs for call handling)
**WirePlumber:** Does NOT manage this adapter (restricted via `87-bt-adapter-select.lua`)

RotaryPhone should manage this adapter directly via BlueZ D-Bus. Since WirePlumber ignores it, RotaryPhone has full control over profile registration, device connections, and audio routing.

**You CAN:**
- Register BlueZ Profile1 agents (HFP, etc.) on this adapter
- Set discoverable/pairable as needed
- Manage device connections independently
- Use SCO/eSCO audio channels for voice calls

**Do NOT:**
- Register A2DP profiles on this adapter (would confuse phones about which adapter to use for music)
- Modify files in `/etc/wireplumber/bluetooth.lua.d/` without coordinating with Radio Console
- Change `bluetoothctl` default adapter (use `select 10:91:D1:FE:00:46` explicitly)

---

## WirePlumber Configuration (owned by Radio Console)

These files are in `/etc/wireplumber/bluetooth.lua.d/` and affect WirePlumber's bluez_monitor:

| File | Purpose | Do NOT modify |
|------|---------|---------------|
| `85-disable-hfp-hf.lua` | Removes `hfp_hf` from WP's `bluez5.roles` so RotaryPhone's `hfp_monitor.py` can register the Profile1 agent | Only modify if HFP-HF role assignment changes |
| `87-bt-adapter-select.lua` | Sets `bluez5.default.adapter` to TP-Link MAC — WP only manages hci0 | Do not change |
| `89-bt-autoconnect.lua` | Enables `bluez5.auto-connect` for A2DP profiles on the Music adapter | Do not change |
| `90-disable-bt-input-autolink.lua` | Prevents WP from auto-linking `bluez_input` nodes to speakers | Do not change |

**Also patched:** `/usr/share/wireplumber/scripts/monitors/bluez.lua` line 382 — always activates BT devices
(workaround for PipeWire 1.0.7 quirk where `api.bluez5.connection` reports "disconnected" even when connected).
Backup at `.bak`. **Auto-protected:** APT hook at `/etc/apt/apt.conf.d/99-protect-bluez-lua` re-applies the patch after WirePlumber upgrades.

---

## BlueZ Pairing Data

Pairing databases are per-adapter at `/var/lib/bluetooth/<adapter-mac>/`:

- `/var/lib/bluetooth/78:20:51:F5:FB:A7/` — Music adapter pairings (Radio Console)
- `/var/lib/bluetooth/10:91:D1:FE:00:46/` — Voice adapter pairings (RotaryPhone)

These are independent. A phone paired on one adapter is NOT paired on the other.

---

## Audio Pipeline (Radio Console)

```
Phone (A2DP) → BlueZ → PipeWire bluez_input node
                          ↓
              Radio.API PipeWire native stream (radio-bt-stream)
                          ↓
              BufferedSoundGenerator → SoundFlow Mixer
                          ↓
              Modifiers (Balance → Limiter → FingerprintTap → VizTap)
                          ↓
              SoundFlow PlaybackDevice → PipeWire → Built-in Audio speakers
```

Radio Console captures BT audio via a PipeWire native stream (`radio-bt-stream`), NOT via direct speaker link.
WirePlumber rule `90-disable-bt-input-autolink.lua` prevents auto-linking `bluez_input` to speakers.

**For RotaryPhone voice audio:** Since WirePlumber doesn't manage hci1, you'll need to handle
SCO/eSCO audio routing yourself (e.g., via PipeWire directly, or BlueZ's transport API).

---

## Integration Points Between the Two Services

### Radio Console → RotaryPhone (SignalR + REST)

Radio.Web connects to RotaryPhone.API at `http://radio:5004`:
- **SignalR Hub** at `/hub` — receives: `CallStateChanged`, `IncomingCall`, `CallHistoryUpdated`, `SystemStatusChanged`
- **REST API** — `GET /api/phone/system-status`, `GET /api/phone/status`, contacts CRUD, call history, simulate endpoints

### REST endpoints consumed by Radio Console (RTest UI)

| Method | Route | Purpose | Phase added |
|--------|-------|---------|:-----------:|
| GET | `/api/phone/system-status` | Platform / BT / SIP / HT801 reachability | A (existing) |
| GET | `/api/phone/status` | Current call state | A (existing) |
| GET | `/api/gvbridge/status` | GV API availability + SipRegistered + CookiesValid (+ `authBlackout` / `lastApiSuccessAt` / `lastApiAuthFailureAt` — see the auth-blackout note below) | B (PR1), extended by B2 |
| GET | `/api/gvbridge/adapter/mode` | Available + active call adapter mode | A (existing) |
| PUT | `/api/gvbridge/adapter/mode` | Switch active adapter mode | A (existing) |
| GET | `/api/gvbridge/cookies` | Cookie status metadata (no secrets) | B (PR2) |
| POST | `/api/gvbridge/cookies` | Replace cookie set from paste | B (PR2) |
| POST | `/api/gvbridge/cookies/refresh-from-browser` | Extract cookies from Chrome CDP and activate | B (PR3) |
| GET | `/api/gvbridge/voicemail` | Voicemail list + cached-audio proxy (no Google media calls from RTest) | 4 (PR2) |
| GET | `/api/gvbridge/sms/threads` | SMS thread list | 4 (PR3) |
| GET | `/api/gvbridge/sms/...` | SMS thread/message read endpoints | 4 (PR3) |
| POST | `/api/gvbridge/sms/send` | Send an SMS (account write — ships dark behind `EnableSmsSend`) | 4 (PR4) |
| POST | `/api/gvbridge/voicemail/{id}/read` | Mark a voicemail read (`{ "isRead": bool }` → returns `VoicemailItemDto`; GV write-through; **shipped DARK (path a)** behind `EnableMarkRead`, default off) | 4 (mark-read FF) |
| POST | `/api/gvbridge/sms/threads/{threadId}/read` | Mark an SMS thread read (`{ "isRead": bool }` → returns `SmsThreadDto`, `hasUnread=false`; GV write-through; **shipped DARK (path a)** behind `EnableMarkRead`, default off) | 4 (mark-read FF) |
| GET | `/api/gvtrunk/status` | VoIP.ms SIP trunk registration state | A (existing) |
| GET | `/api/gvtrunk/calls` | Call history (last 50) | A (existing) |
| GET | `/api/gvtrunk/sms` | SMS history (last 20, in-memory) | A (existing) |
| POST | `/api/gvtrunk/dial` | Place outbound call via trunk | A (existing) |
| POST | `/api/gvtrunk/reregister` | Force re-registration of trunk | A (existing) |
| GET | `/api/diagnostics/status` | Full diagnostics snapshot | B (existing, newly consumed) |
| GET | `/api/diagnostics/audio-bridge` | Audio-bridge stats only (cheap polling) | B (PR1) |
| GET | `/api/diagnostics/ht801` | Consolidated HT801 status (network + SIP + freshness) | B (PR1) |
| GET | `/api/diagnostics/sip-log` | Recent SIP messages | B (existing, newly consumed) |
| GET | `/api/diagnostics/timeline` | Call timeline events | B (existing, newly consumed) |
| GET | `/api/contacts/*` | Contact CRUD (already in use) | A (existing) |
| GET | `/api/callhistory` | Call history | A (existing) |

**JSON conventions:** All response payloads are camelCase or PascalCase depending on whether the controller returns an anonymous object (camelCase) or a typed DTO/record (PascalCase). RTest's `Radio.Web` configures `JsonSerializerOptions.PropertyNameCaseInsensitive = true` so both work transparently. **New endpoints should prefer typed records** for OpenAPI/Swagger schema clarity.

**GV auth-blackout status fields (B2, 2026-08-01) — `degraded`, NOT `available`:** `/api/gvbridge/status`
gains three append-only fields so GV auth health is derived from **the last real data-plane call** rather
than from a periodic probe of a different endpoint: **`authBlackout`** (bool — the most recent real GV
call was rejected for auth and nothing has succeeded since), **`lastApiSuccessAt`** and
**`lastApiAuthFailureAt`** (nullable UTC). `cookiesValid` is now `probe passed AND NOT authBlackout`, and
`degraded` derives from it, so both go false the moment a real call is rejected instead of reporting
healthy for up to 30 minutes. ⚠️ **`available` deliberately stays `true` during a blackout** — it gates
`GetAuthenticatedClient()` *inside* RotaryPhone, so flipping it would make the adapter refuse its own
recovery retry and turn a ~9-minute blackout into a hard stop. **Radio Console's "GV is reconnecting"
banner must bind to `degraded` or `authBlackout`, not to `available`.** The four original field names
(`available`, `activeMode`, `sipRegistered`, `cookiesValid`) are unchanged. See
`docs/handoffs/radioconsole-gv-auth-blackout-reply.md`.

> ⚠️ **Post-fix, `authBlackout` may be true for well under a second — do not bind a banner to it naively.**
> Measured on the box 2026-08-01: the one live blackout during a 90-minute UAT lasted **920 ms**
> (`lastApiAuthFailureAt` → `lastApiSuccessAt`), and **zero** `authBlackout:true` samples were captured
> across **411** status polls. That is the fix working — recovery is now faster than any practical polling
> rate — but it means a banner bound directly to `authBlackout` at a 10-second cadence will **effectively
> never appear**, and if it ever does it will flicker for one frame. **Recommended:** treat a `true`
> reading as an *event*, latch it, and hold the banner for a minimum display window (a few seconds), or
> drive the UI from sustained failure (e.g. N consecutive failed reads) rather than from the instantaneous
> flag. `lastApiAuthFailureAt` / `lastApiSuccessAt` are the better inputs for "how long has this been bad" —
> they are timestamps, so they survive between polls where the boolean does not.

**Inter-service auth (PR5, default-off):** ALL `/api/gvbridge/*` REST endpoints above **and** the `/hub` SignalR connection accept an **optional** `X-RotaryPhone-Auth: <key>` header. The header is **required only when** RotaryPhone's `GVBridge:InterServiceAuthKey` is set (default empty = LAN-only, no auth, no behavior change). When the key is set, requests/connections without a matching header get **401** (REST) or an **aborted connection** (hub); the compare is constant-time. **EXCEPTION:** `/api/gvbridge/event` (the browser-extension content-script callback) stays open — never gated. The secret is supplied at runtime out-of-source (env `GVBridge__InterServiceAuthKey` / user-secrets), never committed. See the Cookie Management Security note and the handoff (`docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md`) for how RTest sends it.

**SignalR `/hub` events for GV voicemail/SMS (push, on the existing `RotaryHub`):** RadioConsole
subscribes to these alongside the call events — RotaryPhone polls GV and pushes; RadioConsole never polls
GV directly.

| Event | Payload | Fired when | Phase added |
|-------|---------|------------|:-----------:|
| `VoicemailReceived` | `VoicemailItemDto` | Poller detects a new voicemail | 4 (PR3) |
| `SmsReceived` | `SmsMessageDto` (inbound) | Poller detects a new inbound SMS | 4 (PR3) |
| `SmsSent` | `SmsMessageDto` (outbound echo) | Successful send / poller surfaces an outbound | 4 (PR4) |
| `ReadStateChanged` | `{ kind: "Voicemail"\|"Sms", id, threadId, isRead, changedAtUtc }` (camelCase) | Read-state changes from any source — **fired on a mark-read route call (path a — shipped, behind `EnableMarkRead`)**; poller-detected externally-originated read flip (phone/GV web) is a fast-follow (path b — not yet built). | 4 (mark-read FF) |

**Polling cadence guidance for RTest:**

- `/api/phone/status` -- 5 seconds (already)
- `/api/gvbridge/status` -- 10 seconds (cheap, fast-changing)
- `/api/gvtrunk/status` -- 10 seconds
- `/api/diagnostics/audio-bridge` -- 2 seconds **only while audio bridge is active**, otherwise paused
- `/api/diagnostics/ht801` -- 30 seconds (involves a network probe of HT801)
- `/api/gvbridge/cookies` -- 60 seconds (cookie expiry is measured in days)
- `/api/diagnostics/sip-log`, `/api/diagnostics/timeline` -- only on-demand (user opens diagnostics panel), do NOT poll

### RotaryPhone → Radio Console (SignalR)

Radio.API's `PhoneCallIntegrationService` connects to RotaryPhone's SignalR hub and:
- Listens for `CallStateChanged` events
- On incoming call: looks up caller via PBAP contacts (local SQLite) + RotaryPhone contacts API fallback
- Plays ring sound + TTS announcement with audio ducking
- Reports resolved caller name back via `ReportCallerResolved` hub method

### Caller ID Resolution Flow

1. RotaryPhone sends `IncomingCall` or `CallStateChanged(Ringing)` event
2. Radio.API checks PBAP contact DB (synced from phone's phonebook via BT, Music adapter)
3. Falls back to `GET {RotaryPhone}/api/contacts/lookup?phone={number}`
4. Announces via TTS, ducks music audio
5. Reports resolved name back to RotaryPhone via SignalR

---

## Operational Notes

### ⚠️ Deploying RotaryPhone can silently rewrite its BT adapter config

**This is a live hazard on every RotaryPhone deploy — read before deploying, and after.**

`/opt/rotary-phone/appsettings.Production.json` holds RotaryPhone's **`BluetoothAdapter: hci1`** and
`UseActualBluetoothHfp` settings. That file **ships inside the publish artifact** (the SDK's default
`appsettings*.json` content glob), and `deploy/Deploy-ToLinux.ps1`'s **tar-pipe fallback** path can
overwrite the box's authoritative copy with the repo template: its backup/restore straddles a
`tar --unlink-first` that errors on directories and exits 2, so `set -e` aborts the chain before the
restore runs. The **rsync** path is safe (`--exclude 'appsettings.Production.json'`); only the fallback
bites. Observed for real during PR #72 UAT on 2026-08-01 and restored by hand.

**Why Radio Console cares:** a RotaryPhone deploy could silently change which BT adapter RotaryPhone
claims. If `BluetoothAdapter` ever came back as `hci0`, RotaryPhone would reach for the **TP-Link UB500 —
Radio Console's music adapter** — violating rule 1/2 below and breaking A2DP audio, with no log line on
either side saying a config changed.

**Mandatory until the tooling is fixed** (tracked in RotaryPhone's `docs/KNOWN-ISSUES.md`, finding L3):

```bash
# BEFORE any RotaryPhone deploy
sudo cp /opt/rotary-phone/appsettings.Production.json /opt/rotary-phone/appsettings.Production.json.bak
# AFTER — must print hci1
grep -n 'BluetoothAdapter' /opt/rotary-phone/appsettings.Production.json
```

If it is not `hci1`, restore the backup and restart `rotary-phone` before doing anything else.
**No BT/audio behavior is changed by this note** — it documents a deploy hazard that could change it.

### Service Restart Order

After a reboot or PipeWire restart:
1. `radio-bt-setup.service` runs first (oneshot, configures adapters, PipeWire sink, patches)
2. `radio-api.service` starts (depends on radio-bt-setup)
3. `rotary-phone` starts (independent)

**`radio-bt-setup.service`** (new, 2026-03-14) is a systemd oneshot that runs at boot before radio-api:
- Sets hci0 alias "Grandpas Radio", discoverable on
- Sets hci1 alias "Grandpas Phone", discoverable off
- Removes stale hci1 pairings for music-only devices (from `/opt/radio-console/config/bt-music-devices.conf`)
- Sets PipeWire default sink
- Verifies bluez.lua patch and WP configs are intact

This means RotaryPhone no longer needs to set hci1's alias or worry about adapter state on boot — it's handled by the Radio Console boot script.

### If TP-Link Adapter Disconnects/Reconnects

Radio Console handles this via its BT reconnection loop. No action needed from RotaryPhone.

### If Intel Adapter Disconnects/Reconnects

RotaryPhone needs its own reconnection handling. Radio Console will not be affected.

### WiFi Coexistence Warning

The Intel AX201 is a combo WiFi+BT chip. HFP voice traffic is low-bandwidth and should not
cause WiFi interference. However, if WiFi issues appear, the Intel BT adapter is the first
suspect. Monitor WiFi stability after enabling voice calls.

### Cookie Management Security

RotaryPhone's `/api/gvbridge/cookies` endpoints accept and return Google Voice authentication state. **They have no authentication by default** because RotaryPhone listens only on the LAN (radio:5004). If RotaryPhone is ever exposed beyond the LAN (port forward, VPN ingress, Tailscale, etc.), `POST /api/gvbridge/cookies` becomes a credential-theft / account-hijack vector. **MUST enable auth before any external exposure.**

**This gap is now closeable (PR5).** Setting `GVBridge:InterServiceAuthKey` enforces a required `X-RotaryPhone-Auth: <key>` header on **all** `/api/gvbridge/*` endpoints (the cookie endpoints included) AND the `/hub` SignalR connection, via constant-time middleware/hub-filter — "one gate, applied consistently" (ADR §6.5). The gate is **default-off** (empty key = today's LAN-only behavior, byte-identical) and is the **prerequisite for any non-LAN exposure**. Supply the secret at runtime out-of-source (env `GVBridge__InterServiceAuthKey` on the `radio` box / `dotnet user-secrets` for local dev), matching the `CookieEncryptionKey` precedent — **never commit a real key**; the in-repo value stays `""`. Enabling it is a **coordinated, two-sided** change: set the same key on RotaryPhone and on RadioConsole (RTest) together, or RadioConsole gets an instant 401/abort storm (see handoff).

### `bluetoothctl` Default Adapter

With two adapters, `bluetoothctl` may default to either one. **Always use `select <MAC>` first:**

```bash
# For Radio Console work:
bluetoothctl -- select 78:20:51:F5:FB:A7

# For RotaryPhone work:
bluetoothctl -- select 10:91:D1:FE:00:46
```

---

## Passing Work Between Sessions

### If Radio Console needs RotaryPhone to change something:

1. Update this boundary doc with what's needed and why (in the Change Log)
2. If code changes are needed in RotaryPhone, create a file at `D:\prj\RotaryPhone\docs\prompts\` describing the request
3. Tell the user to switch to the RotaryPhone session and reference the prompt file
4. After RotaryPhone completes the work, it updates this boundary doc's Change Log

### If RotaryPhone needs Radio Console to change something:

1. Update this boundary doc with what's needed and why (in the Change Log)
2. If code changes are needed in Radio Console, create a file at `D:\prj\RTest\RTest\docs\` describing the request
3. Tell the user to switch to the Radio Console session and reference the prompt file
4. After Radio Console completes the work, it updates this boundary doc's Change Log

### Shared system-level changes (BlueZ, systemd, udev):

Some changes affect both services (e.g., BlueZ restart, udev rules, systemd service ordering).

- **BlueZ restart** — affects both adapters. Both services will need reconnection. Warn the user.
- **`/etc/wireplumber/bluetooth.lua.d/`** — owned by Radio Console. RotaryPhone must request changes via this doc.
- **udev rules for BT** — coordinate via this doc. The Intel AX201 udev disable rule was removed on 2026-03-13.
- **systemd service ordering** — `radio-bt-setup` → `radio-api` (ordered). `rotary-phone` is independent of both.
- **`/opt/radio-console/config/bt-music-devices.conf`** — lists device MACs that must only be paired on hci0. The boot script removes stale hci1 pairings for these devices. If RotaryPhone needs a device excluded from this cleanup, coordinate via this doc.

### Repo locations:

| Repo | Local path | What it owns |
|------|-----------|--------------|
| Radio Console | `D:\prj\RTest\RTest` | Music adapter (hci0), WirePlumber configs, audio engine, A2DP/AVRCP/PBAP |
| RotaryPhone | `D:\prj\RotaryPhone` | Voice adapter (hci1), HFP profiles, SIP/HT801, this boundary doc |

### Quick checklist for the user switching between sessions:

1. Commit/push in the current session before switching
2. In the new session, read this boundary doc to catch any changes
3. Check `git log` in the other repo if recent changes were made
4. On the Ubuntu target, check service status: `sudo systemctl status radio-api rotary-phone`

---

## Summary of Rules

1. **Music adapter (TP-Link, hci0)** = Radio Console only. Do not touch.
2. **Voice adapter (Intel, hci1)** = RotaryPhone only. Full control.
3. **WirePlumber configs** = Radio Console manages. Coordinate changes.
4. **BlueZ pairings** are per-adapter and independent.
5. **Always select the correct adapter** before `bluetoothctl` commands.
6. **Do not register A2DP on the voice adapter** or HFP-HF on the music adapter.
7. **Update this document** before changing any boundary. The other session will read it.
8. **CRITICAL: Do NOT pair the same device on both adapters.** If a phone is already paired on hci0 (music), RotaryPhone must NOT pair it on hci1 (voice). Duplicate PipeWire devices with the same MAC-based name break WirePlumber's profile resolution (audio-gateway shows 0 sinks/sources, no bluez_input node). See Change Log 2026-03-13 entry #2.
9. **The HT801 lives on a point-to-point cable, not the LAN** (since 2026-09-06). It is reachable at `192.168.86.240` **only from the radio box** via `enp1s0` — not from a laptop, phone, or anything else on WiFi. To reach its web UI, use a browser on the radio console's own desktop. Do not "fix" the addressing: `192.168.86.50` is on two interfaces on purpose, and the `/32` host route is what makes it work. **Never plug anything else into `enp1s0`** — that would put a duplicate `192.168.86.50` onto the house LAN. Its NET LED flashing is expected and not a fault. See Change Log 2026-09-06.

---

## Change Log

| Date | Changed by | What changed |
|------|-----------|--------------|
| 2026-03-13 | Radio Console session | Initial boundary doc. Dual-adapter setup established. Intel AX201 re-enabled for RotaryPhone voice. WP adapter isolation config created. |
| 2026-03-13 | Radio Console session | CRITICAL: Added rule #8 — same device must NOT be paired on both adapters. Root cause of A2DP audio loss: Pixel 8 Pro paired on hci0+hci1 created duplicate PipeWire `bluez_card` devices, breaking WP profile resolution. `bluetoothctl remove` is global (affects all adapters); to remove from one adapter only, delete `/var/lib/bluetooth/<adapter-MAC>/<device-MAC>/` directly. RotaryPhone's bt_manager.py must check if device is already on hci0 before pairing on hci1. |
| 2026-03-14 | Radio Console session | Added BT reliability infrastructure (PR #347). New `radio-bt-setup.service` runs at boot: configures both adapters, removes stale cross-adapter pairings, sets PipeWire defaults, verifies WP patches. APT hook auto-protects bluez.lua patch. Radio.API now has pipeline self-healing monitor (30s) and BT health check. **ACTION NEEDED for RotaryPhone:** bt_manager.py must check if a device is already paired on hci0 before pairing on hci1. See prompt file `docs/prompts/2026-03-14-bt-cross-adapter-pairing-guard.md`. |
| 2026-03-14 | Radio Console session (on behalf of RotaryPhone) | COMPLETED: RotaryPhone commit `3f27809` adds cross-adapter pairing guard to bt_manager.py. Devices already paired on hci0 are now rejected from pairing on hci1. Action item from previous entry is resolved. |
| 2026-03-21 | RotaryPhone session | GV Bridge feature complete (PRs #12-#15). New `gv-bridge-chrome.service` runs a second Chrome instance (separate profile, off-screen) for `voice.google.com`. WebSocket server on `ws://127.0.0.1:8765`. No BT/audio boundary impact. **ACTION for Radio Console:** Integrate GV Bridge Blazor components into kiosk UI. See prompt at `D:\prj\RTest\RTest\docs\2026-03-21-gvbridge-kiosk-integration.md`. Key component: `<ConnectionModeSelector />` for switching between BT/SIP/GV call paths. |
| 2026-05-24 | RotaryPhone session | Phase B PR1 merged: `/api/gvbridge/status` now returns `sipRegistered` + `cookiesValid` fields; new `/api/diagnostics/audio-bridge` and `/api/diagnostics/ht801` endpoints. REST endpoints table added to Integration Points section. RTest Phase C can now consume these for two-badge GV status + audio-bridge dashboard + HT801 dashboard card. |
| 2026-05-25 | RotaryPhone session | Phase B PR2: cookie management endpoints `GET /api/gvbridge/cookies` (status metadata, no secrets) and `POST /api/gvbridge/cookies` (paste-in from browser DevTools). Accepts RawCookieHeader or individual fields. LAN-only, no auth -- see Cookie Management Security note. `GvCookieManager` service extracts cookie lifecycle. `GVApiAdapter` gains `LoadedAt`, `LastValidatedAt`, `ReloadCookiesAsync`. |
| 2026-05-25 | RotaryPhone session | Phase B PR3: `POST /api/gvbridge/cookies/refresh-from-browser` -- server-side CDP cookie extraction. Connects to Chrome's remote debugging port (default 9224), finds voice.google.com tab, extracts cookies via WebSocket Network.getCookies, feeds into existing SetCookiesAsync pipeline. No Playwright dependency, uses BCL HttpClient + ClientWebSocket only. `GVBridgeConfig.ChromeCdpPort` added. |
| 2026-06-20 | RotaryPhone session | PR5: inter-service auth gate. New optional `GVBridge:InterServiceAuthKey` (default empty = LAN-only, no behavior change). When set, `X-RotaryPhone-Auth: <key>` is REQUIRED on all `/api/gvbridge/*` REST endpoints AND the `/hub` SignalR connection (header, or access_token query for browser WS); 401/abort otherwise; constant-time compare. `/api/gvbridge/event` (extension content-script) stays open. Secret supplied at runtime via env (`GVBridge__InterServiceAuthKey`) / user-secrets — never committed. REQUIRED before any non-LAN exposure of RotaryPhone. RadioConsole must send the header on REST + an access-token provider on its HubConnection once the key is set (cross-repo; handoff updated). |
| 2026-06-20 | RotaryPhone session | **API only — no BT/audio change.** GV mark-read / durable read-state contract **RATIFIED** (build HELD by owner). Added two Integration-Points routes — `POST /api/gvbridge/voicemail/{id}/read` and `POST /api/gvbridge/sms/threads/{threadId}/read` (`{ "isRead": bool }` → returns the updated `VoicemailItemDto`/`SmsThreadDto`; **GV write-through** = Google is the single source of truth, no local store; 200 idempotent / 404 / 502) — and a new `/hub` event `ReadStateChanged` (`{ kind, id, threadId, isRead, changedAtUtc }`, fired on a mark route call now / on poller-detected external read flips as a fast-follow). Auth: auto-covered by the PR5 prefix gate (no special posture). Mark-unread is best-effort. Delete deferred. Decision record: `docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md`; reply to RadioConsole: `docs/handoffs/radioconsole-gv-markread-reply.md`. Build ships dark behind `EnableMarkRead` (default off) when funded; first real `updateread` pending the ADR §11 live capture. |
| 2026-06-21 | RotaryPhone session (Builder) | **API only — no BT/audio change.** GV mark-read **Path A SHIPPED DARK** (owner-hold lifted). Both mark routes above + the `ReadStateChanged` on-mark (path a) broadcast are now implemented behind `EnableMarkRead` (default **FALSE** → both routes return `409 markread_disabled` with **NO** GV call). Status taxonomy live: 200 applied-or-idempotent / 404 unknown / 502 upstream / 409 disabled / 400 `unread_unsupported` (isRead:false while `AllowMarkUnread`=false). The GV `api2thread/updateread` wire format (positions/grain/unread support) stays **UNVERIFIED**, isolated behind `IUpdateReadPayloadBuilder` — first real `updateread` pending ADR §11 step 8 on-box live capture. **Fixture-verified only; nothing mutates GV until the owner flips the flag.** Path B (poller-detected external read-flip → live "hear-on-phone clears the kiosk badge") is still a fast-follow, NOT in this PR. |
| 2026-07-16 | Radio Console session (Builder) | **IAC "save" — no functional/runtime change to the box, no adapter/profile/WP-behavior change.** Relocated the canonical copies of the boundary-owned WirePlumber rules **`85-disable-hfp-hf.lua`, `87-bt-adapter-select.lua`, `89-bt-autoconnect.lua`** from box-only into the Radio Console repo at **`deploy/common/`** (next to the already-tracked `90`/`41`). They are now installed by `deploy/debian-x64/setup.sh`, synced by `deploy/Deploy-ToLinux.ps1` (added to `$wpBluetoothRules`), and re-applied by the new idempotent `deploy/provision/provision.sh`. The `bluez.lua` PipeWire-1.0.7 patch (line 384 `if true or …`) is unchanged and still applied/verified via `radio-bt-setup.sh --patch-only` (also the `99-protect-bluez-lua` APT hook); no patch logic changed. **Shared-system OS tuning** (`vm.swappiness=10`, zram, masked evolution/tracker user services, cups disabled) is now captured under `deploy/provision/os-tuning/` — a rebuild via `provision.sh` affects **both** services, so coordinate reboots. **GV bridge:** documented (not scripted) as a **RotaryPhone-owned cross-service dependency** in `deploy/provision/README.md` — canonical launcher is the **google-chrome watchdog** (`gv-bridge-{watchdog,restart}` user units + `~/bin/gv-bridge-*.sh` + autostart loading `/opt/rotary-phone/ChromeExtension`); the disabled snap-Chromium `gv-bridge-chrome.service` is superseded. Ownership of the GV extension/profile stays with RotaryPhone. **Cross-repo note:** this Change Log edit is in the RotaryPhone repo and must be committed there separately — it is NOT part of the Radio Console PR (`chore/iac-provision-save`). |
| 2026-08-01 | RotaryPhone session (Builder) | **API only — no BT/audio change.** GV **auth-blackout (B2) fixed**. `/api/gvbridge/status` gains three append-only fields — `authBlackout`, `lastApiSuccessAt`, `lastApiAuthFailureAt` — and `cookiesValid`/`degraded` are now derived from **the last real data-plane call** instead of a 30-minute-stale probe of a *different* endpoint (`threadinginfo/get` vs the `api2thread/list` that was actually 401ing). ⚠️ **`available` deliberately stays `true` during a blackout** (it gates `GetAuthenticatedClient()` internally — flipping it would make the adapter refuse its own recovery retry); **Radio Console must bind its "GV is reconnecting" banner to `degraded` / `authBlackout`, not `available`** — this needs their agreement, see `docs/handoffs/radioconsole-gv-auth-blackout-reply.md`. Also: `CookieRefreshIntervalMinutes` now actually drives a proactive PSIDTS refresh (default **8 min**; `0` = kill switch) where it previously had **zero readers**; a 401 on the `api2thread` read path now runs the shared cookie-recovery ladder and replays **once** (write paths signal but never replay — ADR §4.2 #4); and `GVApiAdapter.ActivateAsync` is now **re-entrant**, so the box-side cron's 20-minute `refresh-from-browser` POST stops leaking a health timer, an `HttpClient` and a whole `GvSipTransport` per pass (~72/day) — which is also why the 30-minute watchdog, the only timed path into cookie recovery, had never fired. **No BT adapter, profile, or WirePlumber change; hci0/hci1 ownership untouched.** Box-side note: the cron at `/opt/rotary-phone/refresh-gv-cookies.sh` is **deliberately left running** — do not remove it as part of this change. |
| 2026-08-01 | RotaryPhone session (Builder) | **API/deploy hazard only — NO BT or audio behavior change; hci0/hci1 ownership, profiles and WirePlumber configs all untouched.** Closing out B2 (PR #72 merged after live on-box UAT: 932 requests, **zero 502s**, `api2thread/list returned Unauthorized` 33/hr → **0/hr**). Two items Radio Console needs: **(1)** ⚠️ **`authBlackout` may be true for well under a second.** Measured live: the one blackout in a 90-minute soak lasted **920 ms**, and **zero** `authBlackout:true` samples appeared across **411** status polls. A "GV is reconnecting" banner bound naively to that boolean at a 10 s cadence will effectively **never show**. Latch it as an event with a minimum display window, or derive the UI from `lastApiAuthFailureAt`/`lastApiSuccessAt` (timestamps survive between polls; the boolean does not). Integration Points updated with the full guidance. **(2)** ⚠️ **A RotaryPhone deploy can silently rewrite `/opt/rotary-phone/appsettings.Production.json`, including `BluetoothAdapter: hci1`** — the file ships inside the publish artifact and `Deploy-ToLinux.ps1`'s tar-pipe fallback loses it when `tar --unlink-first` exits 2 and `set -e` skips the restore (the rsync path is safe). If that value ever came back as `hci0`, RotaryPhone would reach for **Radio Console's UB500** and break A2DP, silently. New Operational Notes section documents the mandatory backup-and-verify around every deploy; fix proposed in RotaryPhone `docs/KNOWN-ISSUES.md` (finding L3, still OPEN). Also unchanged from the previous entry: the box-side cron `/opt/rotary-phone/refresh-gv-cookies.sh` is **still deliberately running** — retiring it is a separate box-side change, do not remove it as a side effect. |
| 2026-08-10 | Radio Console session (Builder) | **SHARED-SYSTEM CHANGE — system-wide core dump handling replaced. No BT/adapter/profile/WirePlumber change; hci0 and hci1 untouched.** `core_pattern` was piping to **apport**, which retains exactly ONE `.crash` per executable — two `radio-api` SIGABRTs on 2026-08-10 (16:16, 16:42) produced a single file, the second silently overwriting the first. Installed **`systemd-coredump`** (255.4-1ubuntu8.16) so successive crashes are retained separately; `core_pattern` is now `\|/usr/lib/systemd/systemd-coredump …`. APT removed **`apport-core-dump-handler`** to satisfy `apport`'s `apport-core-dump-handler \| systemd-coredump` alternative dependency — the `apport` package itself remains installed. **This affects every process on the box, RotaryPhone included:** RotaryPhone crashes now land in `coredumpctl` (`/var/lib/systemd/coredump/`, zstd-compressed) instead of `/var/crash/*.crash`. Retrieve with `coredumpctl list` / `coredumpctl info <exe>` / `coredumpctl debug <exe>` — strictly more forensic data than before, not less. Retention is **bounded** in `/etc/systemd/coredump.conf`: `Compress=yes`, `ProcessSizeMax=2G`, `ExternalSizeMax=2G`, `MaxUse=2G`, `KeepFree=10G` (measured 89G free on `/` at install). Verified after install: `rotary-phone.service` still **active (running)**, MainPID 2049 unchanged, `NRestarts=0`; `radio-api`/`radio-web` likewise unrestarted. Retention proven end-to-end by crashing a throwaway binary twice and confirming two separate stored cores. Pre-existing `/var/crash/_opt_radio-console_api_Radio.API.1000.crash` preserved to `/home/mmack/crash-archive/` (sha256 `df9d06f9…`), original left in place. **Action for RotaryPhone: none required** — but if any RotaryPhone tooling greps `/var/crash/`, switch it to `coredumpctl`. **Cross-repo note:** this Change Log edit lives in the RotaryPhone repo and must be committed there separately — it is NOT part of the Radio Console PR (`chore/crash-forensics-and-restart-limits`). |
| 2026-09-06 | Radio Console session | **NETWORK TOPOLOGY CHANGE — affects the HT801, which is RotaryPhone's device. No BT/adapter/profile/WirePlumber change; hci0 and hci1 untouched.** The HT801 ATA is now on a **dedicated point-to-point ethernet link** into the Radio Console box's `enp1s0`, not on the house LAN. Owner's intent: make the rotary phone self-contained in the console cabinet. **⚠ The HT801 is no longer reachable from the house LAN** — its web UI at `192.168.86.240` can now only be reached *from the radio box itself* (e.g. a browser on that desktop). A laptop on WiFi cannot reach it. Addressing is deliberately unusual and must not be "tidied": HT801 holds static `192.168.86.240`; `enp1s0` carries `192.168.86.50/32` plus a host route `192.168.86.240/32 dev enp1s0`, so the `/32` beats WiFi's `/24` by longest-prefix match. `192.168.86.50` is therefore on **two interfaces at once** — safe *only* because that cable is a dead end with one device on it, so ARP replies cannot reach the LAN. ⚠ **Plugging anything else into `enp1s0` will cause address conflicts on the house network.** Persisted as NetworkManager profile **`ht801-direct`** (`autoconnect yes`, `ipv4.never-default yes` so the cable can never steal the default route from WiFi, `ipv4.dad-timeout 0` because duplicate-address detection would otherwise refuse a deliberately duplicated address). **The HT801's NET LED flashes permanently and this is EXPECTED, not a fault** — its configured gateway `192.168.86.1` does not exist on a point-to-point cable, so it ARPs and gets nothing; it only ever needs to reach the box. Verified 2026-09-06: dial tone, inbound and outbound calls all working, and the GV dashboard reports the HT801 online. Pointing its gateway at `192.168.86.50` would make the LED solid; left alone rather than change a working configuration. **No action needed from RotaryPhone** — SIP registration and RTP were unaffected. |
| 2026-09-07 | Radio Console session | **BOUNDARY RULE #6 VIOLATION OBSERVED LIVE — needs a RotaryPhone decision. No Radio Console change made; hci0/hci1 config and WirePlumber untouched.** The owner's phone (Pixel 10 Pro XL `B0:D5:FB:D2:0D:68`, on **hci0** for A2DP) drops mid-playback and shows an error on the handset. **Proven from the journal:** `bt_manager` accepted an HFP `NewConnection` on `/org/bluez/hci0/dev_B0_D5_FB_D2_0D_68` at 11:18:53 — the **music** adapter — and at 11:19:40 bluetoothd logged `ext_io_disconnected() … RotaryPhone HFP: getpeername: Transport endpoint is not connected (107)`, bracketed by an 18-second gap in the A2DP transport (`fd5` ready 11:18:55 → `fd6` ready 11:19:58). **Not proven:** that the HFP failure *caused* the A2DP drop — one occurrence, tight correlation, no reproduction. **Ruled out:** the address-based dedup in `HfpProfile.NewConnection`; there is no `Duplicate HFP … closing fd` line for this event. **Source:** `scripts/bt_manager.py:511` deliberately accepts HFP on any adapter, disabling mitigation (c) from `docs/superpowers/specs/2026-03-13-rotaryphone-standalone-architecture-design.md:58` (reject wrong-adapter connections in `NewConnection`). Its comment records why — rejecting made the phone drop HFP on hci1 too — so **this is a known trade, not an oversight, and simply re-enabling the check is NOT the ask.** The likelier fix is to stop `hci0` advertising HFP at all so the phone never attempts it there, which is what the design's invariant implies; that is RotaryPhone's architecture call. **Full write-up and repro steps: `docs/prompts/2026-09-07-hfp-on-music-adapter-drops-the-phone.md`.** **ACTION NEEDED from RotaryPhone.** Radio Console has not modified `bt_manager.py` or any RotaryPhone code and will not. Separately and unrelated: ~55 BT audio buffer underruns with PipeWire callback execution to 14.2 ms were found in the same logs — that is Radio Console's, filed as `AUD-15`, and must not be conflated with this. |
| 2026-09-08 | RotaryPhone session (Builder) | **Cross-service tooling contract — no BT/audio change; hci0/hci1 ownership, profiles and WirePlumber configs untouched.** Radio Console's **`KIOSK-2`** adds a **second consumer of RotaryPhone's `gv-bridge-ensure.sh`**: it runs the script to repair a bridge it has found down, then decides whether its `VOICE` row shows amber. **Ownership does not move.** The script, `gv-bridge-watchdog.timer` (every 2 min) and the nightly `gv-bridge-restart.timer` remain RotaryPhone's; Radio Console does not own, reimplement, edit, install or `pkill` any part of bridge startup, and only ever *invokes* the script. **Path:** RotaryPhone installs it to **`~/bin/gv-bridge-ensure.sh`** (`deploy/setup-gvbridge.sh:131`, mode 755) — the only one of Radio Console's three candidates that exists today. Their side resolves it from a list (`~/bin/` → `/usr/local/bin/` → `/opt/rotary-phone/bin/`) and reports a failure when none matches, so a relocation degrades to a **reported failure rather than a silent wrong answer**; RotaryPhone will announce a move in this Change Log before making one. ⚠ **Exit code is NOT a health signal and must not be used as one.** `gv-bridge-ensure.sh` exits **0 on every path it can reach** — bridge already up, lock held by the other launcher, **and launch failed**. It runs under `set -u` with no `set -e`, and its last statement is an unconditional `echo` to the log, so a failing `systemd-run` is swallowed. Verified 2026-09-08 by running it with `google-chrome` absent: it logged `Failed to find executable google-chrome`, then logged `ensure: bridge was down -> launched` (an overclaim), then **exited 0**. A nonzero status therefore means the *interpreter* failed (file missing, not executable, `set -u` violation) — never "the bridge is down". **For an amber `VOICE` row, test liveness directly** using the same marker the script itself uses — `pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"` — or read `/api/gvbridge/status`; treat the script strictly as a repair *action* whose effect must be re-checked afterwards. Whether to give the script a meaningful exit code is an **open RotaryPhone decision** (it would put `gv-bridge-watchdog.service`, a `Type=oneshot` unit, into a failed state on every failed launch); this row will be updated if that changes. Full reply: `docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md`. |
| 2026-09-08 | RotaryPhone session (Builder) | **API semantics only — no BT/audio change; hci0/hci1 ownership, profiles and WirePlumber configs untouched. No wire shape change, no field added or removed, and no Radio Console code change required.** Two changes to the bell/phone-status contract, per ADR `docs/architecture/decisions/2026-09-08-bell-health-contract-ratification.md` §4.4 and §9.2. **(1) `GET /api/phone/system-status` reachability semantics converged onto the resolved address.** The endpoint used to run a synchronous ICMP ping of the **configured** HT801 address inside the request and stamp `ht801LastCheckedUtc` with `DateTime.UtcNow`; it now serves the same cached 30-second background probe of the **resolved registrar binding** that the `SystemStatusChanged` hub event already carried. One probe, one meaning, on both transports. ⚠ **This matters to Radio Console's `BellHealthService`, which polls this REST route every 15 s:** their predictive-degrade rule was sitting on a ping of the configured address — the signal that stayed green throughout the entire 2026-07 outage while every INVITE went to a stale address. As shipped before this change, predictive-degrade would **not** have fired during the incident it exists to prevent. It is now a real predictor. `ht801LastCheckedUtc` is now a genuine probe age rather than a response timestamp (verified live before: two calls 14.5 ms apart returned two different values, each equal to the request instant; after: three rapid calls return an identical value ~6.6 s old, holding steady and then advancing exactly on the 30 s cadence), so their "last checked" / stale-probe affordance starts working with **no change on their side**. Also removes a blocking ≤3 s ping from the request path, which previously fired for every polling client every 15 s *precisely when the ATA was unreachable*. ⚠ **Two visible behaviour changes at boot, needing no code from them but worth knowing:** before the first probe completes, `ht801Reachable`, `ht801LastCheckedUtc` **and `ht801IpAddress`** are all null — null means NOT YET PROBED, never offline, and must render as gray `Unknown`. **`ht801IpAddress` is newly nullable over REST**, where it previously always carried a string (the configured value defaults to `""`); their §7m rule already covers a null `ht801Reachable` but may not cover a null address. The window is a **single ping round-trip** — measured ~3.4 s on a dev box with no HT801 present, of which 3 s was the ICMP timeout, so milliseconds on the appliance — **not** the "up to 30 s" the ADR originally assumed. It is unbounded only in the degenerate case where no HT801 address can be resolved at all. **(2) A dismissed bell-failure note now survives a service restart.** `BellFailureTracker` was in-memory; the delivered reply `docs/handoffs/radioconsole-bell-failure-reply.md` §5 nevertheless told Radio Console the `acknowledged` flag "survives a service restart" and that their Q4 concern — a nightly-restarting kiosk resurrecting a note the operator already dismissed — "is addressed". **That was false.** The owner chose to make the claim true rather than retract it, reversing plan decision **D5**. State now persists to `data/bell-failure-state.json` (atomic temp-plus-rename write; `data/` is excluded from the deploy at `deploy/Deploy-ToLinux.ps1:90`), so a dismissal holds across the nightly restart, a crash and a deploy. `failureCount` survives too — a restart is not evidence the bell started working. Verified end-to-end against a running server: real failure driven through the detection path, acked, process killed, note restored with `acknowledged: true` and every field intact. ⚠ **STILL OPEN, and Radio Console should know:** the same reply §5 also promises that acking an **already-acked or absent** failure returns `200 {"acknowledged": true}` and invites clients to "retry freely on a flaky network". The code returns `{"acknowledged": false}` in both cases — confirmed live. Pre-existing and deliberately **not** changed here, because altering a response body Radio Console consumes is a coordinated decision; it needs an owner call. |
