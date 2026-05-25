# Radio Console ↔ RotaryPhone BT Audio Boundary

> **Purpose:** This is a shared boundary contract between two Claude sessions working in
> parallel on the same Ubuntu box. Radio Console (D:\prj\RTest\RTest) owns music/A2DP.
> RotaryPhone (D:\prj\RotaryPhone) owns voice/HFP. Neither side should modify the other's
> adapter, profiles, or WirePlumber configs without updating this document.
>
> **Canonical location:** `D:\prj\RotaryPhone\docs\prompts\RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`
> **Last updated:** 2026-05-25 by RotaryPhone session (Phase B PR2)
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
| GET | `/api/gvbridge/status` | GV API availability + SipRegistered + CookiesValid | B (PR1) |
| GET | `/api/gvbridge/adapter/mode` | Available + active call adapter mode | A (existing) |
| PUT | `/api/gvbridge/adapter/mode` | Switch active adapter mode | A (existing) |
| GET | `/api/gvbridge/cookies` | Cookie status metadata (no secrets) | B (PR2) |
| POST | `/api/gvbridge/cookies` | Replace cookie set from paste | B (PR2) |
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

RotaryPhone's `/api/gvbridge/cookies` endpoints accept and return Google Voice authentication state. **They have no authentication today** because RotaryPhone listens only on the LAN (radio:5004). If RotaryPhone is ever exposed beyond the LAN (port forward, VPN ingress, Tailscale, etc.), `POST /api/gvbridge/cookies` becomes a credential-theft / account-hijack vector. **MUST add auth before any external exposure.** Suggested approach: API key in `appsettings.json`, required header `X-RotaryPhone-Auth: <key>` on cookie endpoints, validated by middleware.

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
