# PBAP Contact Sync — Design Spec

**Date:** 2026-03-12
**Status:** Approved
**Scope:** Radio.API (primary), RotaryPhone (minor SignalR callback)

## Problem

When an incoming call arrives on the rotary phone, the system can ring and announce "Incoming call" via TTS, but cannot identify the caller by name. The phone's contact list (phonebook) is not accessible to the system.

## Solution

Add a PBAP (Phone Book Access Profile) client to Radio.API that pulls contacts from connected Bluetooth phones via the BlueZ OBEX D-Bus API. Contacts are stored per-device in SQLite. On incoming calls, Radio.API resolves the caller's phone number to a name and announces it via TTS, then sends the resolved name back to RotaryPhone for UI/logging.

## Architecture Decision

**PBAP lives in Radio.API**, not RotaryPhone, because Radio.API owns the BlueZ adapter and manages Bluetooth pairing/connections. RotaryPhone remains BT-passive.

## Components

### 1. PbapSyncService

**Location:** Radio.API (background service + on-demand)

**Responsibilities:**
- Listens for BT device connection events from the existing Bluetooth service
- On connect: checks if contacts for that device MAC are stale (>24h) or missing
- If sync needed: connects to BlueZ OBEX D-Bus (`org.bluez.obex.Client1`), creates a PBAP session, pulls contacts via async transfer, parses returned vCards
- Extracts display name + phone numbers from each vCard entry
- Upserts into SQLite, tagged by device MAC address
- Exposes `SyncContactsAsync(deviceAddress)` for manual trigger via API

**Sync trigger rules:**
- Auto-sync on BT connect if contacts are missing or older than 24 hours
- Manual sync via REST endpoint (always re-syncs regardless of age)
- Manual sync endpoint accepts optional `?deviceAddress=` parameter; defaults to currently connected device

**BlueZ OBEX D-Bus flow:**
1. Call `CreateSession` on `org.bluez.obex.Client1` with target device address and `{ "Target": "PBAP" }`
2. Get `org.bluez.obex.PhonebookAccess1` interface on the returned session path
3. Call `Select("int", "pb")` to select the internal phonebook
4. Call `PullAll(targetFile, filters)` — this returns a Transfer object path + properties dict, NOT inline vCard data
5. Monitor the `org.bluez.obex.Transfer1` object's `Status` property for `"complete"` or `"error"`
6. On `"complete"`: read vCard data from the local `targetFile` that `obexd` wrote
7. Parse vCard data for `FN` (display name) and `TEL` (phone numbers) fields
8. Delete the temp file
9. Call `RemoveSession` to clean up

**Transfer lifecycle & temp file management:**
- `targetFile` should be a unique temp path: `/tmp/pbap-sync-{deviceAddress}-{timestamp}.vcf`
- Monitor transfer via D-Bus property changes on `org.bluez.obex.Transfer1` (`Status` property)
- On `"complete"`: read file, parse, delete
- On `"error"` or timeout (30s): delete temp file if it exists, log warning, abort
- On phone disconnect mid-transfer: `obexd` will error the transfer — handle via the `"error"` status path
- Ensure temp file cleanup in a `finally` block to prevent accumulation

**obexd availability:**
- Check for `obexd` on the session D-Bus at startup (`org.bluez.obex` well-known name)
- If unavailable: log warning, auto-sync disabled but not permanently — each connect event re-checks availability
- No persistent "disabled" flag; each sync attempt independently verifies `obexd` is reachable
- If `CreateSession` fails at runtime (obexd crashed): log warning, skip that sync cycle, next connect/manual trigger will retry

### 2. SQLite Schema

```sql
CREATE TABLE PbapContacts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceAddress TEXT NOT NULL,        -- BT MAC of the source phone
    DisplayName TEXT NOT NULL,
    PhoneNumber TEXT NOT NULL,          -- digits-only normalized, one row per number
    LastSynced DATETIME NOT NULL,
    UNIQUE(DeviceAddress, PhoneNumber)
);

CREATE INDEX IX_PbapContacts_DeviceAddress ON PbapContacts(DeviceAddress);
CREATE INDEX IX_PbapContacts_PhoneNumber ON PbapContacts(PhoneNumber);
```

A contact with multiple phone numbers produces multiple rows (one per number). This enables fast indexed lookup by phone number without parsing JSON arrays.

### 3. Phone Number Normalization

Phone numbers are normalized to **digits-only** at both write time (vCard import) and query time (caller lookup):

1. Strip all non-digit characters (spaces, dashes, parens, dots, `+`)
2. If result starts with country code `1` and is 11 digits, strip the leading `1` to get 10 digits
3. Store the normalized result

**Matching strategy** (at query time, after normalizing the input):
1. Exact match on normalized `PhoneNumber` column
2. If no match, last-7-digit suffix match (handles remaining country code / area code discrepancies)

This matches RotaryPhone's existing `ContactService` approach.

### 4. Contact Lookup in Call Flow

**Current flow:**
```
Phone rings -> BT HFP -> RotaryPhone -> SignalR "Ringing, 555-1234" -> Radio.API -> TTS "Incoming call"
```

**New flow:**
```
Phone rings -> BT HFP -> RotaryPhone -> SignalR "Ringing, 555-1234" -> Radio.API
  -> Resolve device: IBluetoothService.ConnectedDevice?.Address
  -> Query PbapContacts WHERE DeviceAddress = connected AND PhoneNumber matches
  -> TTS "Incoming call from John Smith"
  -> Invoke RotaryPhone hub method: ReportCallerResolved(phoneNumber, displayName)
```

**Device scoping:** The lookup service obtains the currently connected device address from `IBluetoothService.ConnectedDevice` at query time. If no device is connected or no contacts exist for that device, falls back to announcing the raw phone number.

### 5. SignalR Communication — CallerResolved

**Direction:** Radio.API (client) → RotaryPhone (hub server)

Radio.API is a SignalR **client** connected to RotaryPhone's call hub. To send the resolved caller name back:

1. **RotaryPhone** adds a new hub method: `ReportCallerResolved(string phoneNumber, string displayName)` on its existing call hub
2. **Radio.API's** `PhoneCallClient` invokes this method via `_hubConnection.SendAsync("ReportCallerResolved", phoneNumber, displayName)`
3. **RotaryPhone hub** receives the call, updates `CallManager` state with the display name, and broadcasts to its own UI clients

This follows the existing pattern where Radio.API calls hub methods on RotaryPhone's server.

### 6. API Endpoints (Radio.API)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/bluetooth/pbap/sync?deviceAddress=XX:XX:XX:XX:XX:XX` | Manual sync trigger (defaults to connected device if omitted) |
| `GET` | `/api/bluetooth/pbap/contacts?deviceAddress=XX:XX:XX:XX:XX:XX` | List contacts for a device |
| `GET` | `/api/bluetooth/pbap/lookup?phoneNumber=555-1234` | Resolve phone number to display name (scoped to connected device) |
| `GET` | `/api/bluetooth/pbap/status` | Last sync time, contact count per device |

### 7. RotaryPhone Changes

Minimal changes:
- Add `ReportCallerResolved(string phoneNumber, string displayName)` hub method to the existing call hub
- Hub method updates `CallManager` / call log with the resolved display name
- Hub method broadcasts resolved name to connected UI clients
- No PBAP protocol code in RotaryPhone

### 8. vCard Parsing

Lightweight parser for PBAP vCard output. Only extracts:
- `FN` — Formatted display name
- `TEL` — Phone number(s), one or more per contact

Handles:
- vCard 2.1 and 3.0 formats (both common in PBAP)
- `ENCODING=QUOTED-PRINTABLE` on `FN` fields (common on Android for non-ASCII names)
- `CHARSET=UTF-8` parameter
- Multiple `TEL` entries per contact (mobile, home, work, etc.)

All other vCard fields are ignored (YAGNI).

### 9. Dependencies

| Dependency | Purpose | Notes |
|-----------|---------|-------|
| `obexd` | BlueZ OBEX daemon | Must be running on Linux host (session bus) |
| `Tmds.DBus` or equivalent | .NET D-Bus client | Check if already in Radio.API dependencies |
| vCard parser | Parse `FN` + `TEL` fields | Hand-roll (minimal scope — ~50 lines for the fields we need) |

### 10. Configuration

```json
{
  "Bluetooth": {
    "Pbap": {
      "AutoSyncOnConnect": true,
      "SyncStaleThresholdHours": 24,
      "TransferTimeoutSeconds": 30
    }
  }
}
```

## Integration with Existing PhoneContactLookupService

Radio.API has an existing `PhoneContactLookupService` used by `PhoneCallIntegrationService` for caller name resolution. The PBAP lookup **augments** this service — add PBAP SQLite lookup as an additional source within the existing `FindCallerNameAsync` method. Priority:
1. PBAP contacts (per-device, authoritative from phone)
2. Existing lookup sources (if any)
3. Fallback: return raw phone number

## Out of Scope

- Contact photos or extended vCard fields (address, email, etc.)
- Contact editing / write-back to phone
- Call history sync via PBAP (PBAP supports `telecom/cch.vcf` but not needed now)
- Changes to RotaryPhone's existing `ContactService` (separate manual contact management)
- PBAP for Windows (Linux/BlueZ only, matching existing BT architecture)

## Error Handling

- If `obexd` is unavailable: log warning, skip sync, retry on next connect/manual trigger (no persistent disabled state)
- If PBAP session fails (phone denies access, `CreateSession` error): log warning, skip sync, system continues without caller ID
- If transfer times out (>30s) or errors: delete temp file, log warning, abort sync
- If phone disconnects mid-sync: `obexd` errors the transfer, temp file cleaned up, partial data discarded (no upsert)
- If no contact found for incoming number: announce "Incoming call from [phone number]" (fallback to number display)

## Testing Strategy

- Unit tests for vCard parser (various vCard 2.1/3.0 samples, including QUOTED-PRINTABLE encoding)
- Unit tests for phone number normalization + matching logic
- Unit tests for PbapSyncService (mock D-Bus, verify upsert logic)
- Integration test for D-Bus PBAP session (requires real BT device or mock obexd)
- Manual UAT: pair phone, verify contacts sync, make incoming call, verify announcement includes caller name
