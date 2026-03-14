# PRD: Google Voice Trunk Integration
**Project:** RotaryPhone  
**Component:** `RotaryPhoneController.GVTrunk`  
**Status:** Ready for Implementation  
**Target:** Claude Code session  

---

## 1. Background & Motivation

The existing RotaryPhone project routes calls through a Bluetooth HFP bridge:

```
Rotary Phone → HT801 ATA → Raspberry Pi (SIPSorcery) → Bluetooth HFP → Mobile Phone
```

This PRD adds a parallel call path using a SIP trunk registered to a Google Voice number, enabling the rotary phone to send and receive calls via Google Voice without requiring a paired mobile phone. It also adds a lightweight software dashboard for call history and SMS notifications.

The new component must be **self-contained** — it plugs into the existing `ISipAdapter` / `CallManager` abstraction without modifying existing code. The Bluetooth HFP path remains fully intact.

---

## 2. Goals

| Priority | Goal |
|---|---|
| P0 | Rotary phone can receive calls forwarded from Google Voice |
| P0 | Rotary phone can dial out via SIP trunk (with GV caller ID) |
| P1 | Blazor dashboard shows live call status and call history log |
| P1 | Dashboard displays SMS notifications received via Gmail API |
| P2 (future) | IVR / call routing automation hooks |

---

## 3. Architecture

### 3.1 High-Level Signal Flow

```
┌────────────────────────────────────────────────────────────────┐
│  PSTN / Caller                                                 │
└──────────────────────┬─────────────────────────────────────────┘
                       │
               Google Voice (forwards to DID)
                       │
               VoIP.ms SIP Trunk (DID number)
                       │
         ┌─────────────▼───────────────┐
         │  GVTrunkAdapter             │  ← NEW (this PRD)
         │  (ISipAdapter impl)         │
         └─────────────┬───────────────┘
                       │ ISipAdapter events
         ┌─────────────▼───────────────┐
         │  CallManager (existing)     │
         └─────────────┬───────────────┘
                       │ SIP INVITE
         ┌─────────────▼───────────────┐
         │  Grandstream HT801 ATA      │
         └─────────────┬───────────────┘
                       │ Analog FXS
         ┌─────────────▼───────────────┐
         │  Rotary Phone               │
         └─────────────────────────────┘
```

### 3.2 New Solution Project

Add a new **Razor Class Library (RCL)** to the solution. The RCL project type is chosen because it bundles both backend logic and UI surfaces in a single portable package:

- **Backend logic** (adapters, services, models) — shared by all hosts
- **REST API controllers + SignalR hub** — consumed by the React frontend in `RotaryPhone` over HTTP/WebSocket
- **Razor components** — consumed directly by the Blazor frontend in `RTest` as component references

This means neither host project contains any GVTrunk UI code of its own — they only reference this project and wire up routing.

```
RotaryPhoneController.GVTrunk/
├── Adapters/
│   └── GVTrunkAdapter.cs               # ISipAdapter implementation
├── Services/
│   ├── TrunkRegistrationService.cs      # SIP REGISTER keepalive
│   ├── GmailSmsService.cs               # Gmail API polling for GV SMS/missed-call emails
│   └── CallLogService.cs                # SQLite call history store
├── Models/
│   ├── TrunkConfig.cs                   # Config POCO (bound from appsettings)
│   ├── CallLogEntry.cs                  # Call record model
│   └── SmsNotification.cs               # SMS notification model
├── Interfaces/
│   ├── ITrunkAdapter.cs                 # Extends ISipAdapter with trunk-specific members
│   ├── ISmsProvider.cs                  # Abstraction over Gmail/other SMS sources
│   └── ICallLogService.cs               # Abstraction for call history persistence
├── Api/
│   ├── GVTrunkController.cs             # REST endpoints — consumed by React (RotaryPhone)
│   └── GVTrunkHub.cs                    # SignalR hub — real-time push to React
├── Components/
│   ├── GVTrunkDashboard.razor           # Top-level Blazor component — consumed by RTest
│   ├── TrunkStatusPanel.razor           # Registration badge, call state, re-register button
│   ├── CallHistoryTable.razor           # Last 50 call log entries
│   ├── SmsNotificationsPanel.razor      # Inbound SMS / missed call feed
│   └── OutboundDialPanel.razor          # E.164 dial input + button
├── Extensions/
│   └── GVTrunkServiceExtensions.cs      # AddGVTrunk() / MapGVTrunk() extension methods
└── RotaryPhoneController.GVTrunk.csproj # <Project Sdk="Microsoft.NET.Sdk.Razor">
```

### 3.3 Dependency on Existing Projects

- References `RotaryPhoneController.Core` (for `ISipAdapter`, `CallState` enum, `ILogger`)
- **`RotaryPhone`** (React host): references `GVTrunk` for backend services + API/hub; React frontend calls the REST/SignalR endpoints — no Razor components used
- **`RTest`** (Blazor host): references `GVTrunk` for backend services + Razor components; mounts `<GVTrunkDashboard />` directly in a page — no REST/SignalR consumption needed from the Blazor side
- No modifications to `RotaryPhoneController.Core` are required

---

## 4. Detailed Requirements

### 4.1 `TrunkConfig` (Configuration POCO)

Bound from `appsettings.json` under the key `"GVTrunk"`.

```json
"GVTrunk": {
  "SipServer": "sip.voip.ms",
  "SipPort": 5060,
  "SipUsername": "<voipms_account>/<did_number>",
  "SipPassword": "<voipms_password>",
  "LocalSipPort": 5061,
  "LocalIp": "192.168.1.20",
  "GoogleVoiceForwardingNumber": "<DID_number>",
  "OutboundCallerId": "<google_voice_number>",
  "RegisterIntervalSeconds": 60,
  "GmailPollIntervalSeconds": 30,
  "GmailCredentialsPath": "/home/pi/.config/rotaryphone/gmail_credentials.json",
  "CallLogDbPath": "/home/pi/.local/share/rotaryphone/calllog.db"
}
```

### 4.2 `ITrunkAdapter` Interface

Extends the existing `ISipAdapter`:

```csharp
public interface ITrunkAdapter : ISipAdapter
{
    bool IsRegistered { get; }
    event Action<bool> OnRegistrationChanged;  // true = registered, false = lost registration
    Task RegisterAsync(CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);
}
```

### 4.3 `GVTrunkAdapter` Class

Implements `ITrunkAdapter` using SIPSorcery.

**Responsibilities:**

1. **SIP Registration**
   - On `RegisterAsync()`, send a SIP REGISTER to `TrunkConfig.SipServer` using configured credentials
   - Maintain registration via `TrunkRegistrationService` (periodic re-REGISTER before expiry)
   - Set `IsRegistered = true` on 200 OK; fire `OnRegistrationChanged(true)`
   - On registration failure or timeout, fire `OnRegistrationChanged(false)` and log via Serilog at Warning level

2. **Inbound Call Handling**
   - Listen for incoming SIP INVITEs from the trunk on `TrunkConfig.LocalSipPort`
   - On INVITE received: fire `OnIncomingCall()` event (existing `ISipAdapter` contract)
   - Send 180 Ringing, then wait for CallManager to instruct answer/reject
   - On answer: send 200 OK + SDP, establish RTP session
   - Bridge RTP audio: trunk RTP ↔ HT801 RTP (pass-through)

3. **Outbound Call Handling**
   - `SendInviteToHT801(string extensionToRing, string targetIp)` — existing behavior unchanged (rings the HT801 for incoming calls)
   - Add new method `PlaceOutboundCall(string e164Number)` — sends SIP INVITE to trunk with `From:` header set to `TrunkConfig.OutboundCallerId`

4. **Hook & Digit Events**
   - These continue to come from the HT801 via existing `SIPSorceryAdapter` — `GVTrunkAdapter` does NOT replace the HT801 adapter
   - `GVTrunkAdapter` is the **outbound/inbound trunk path**; the HT801 adapter remains the **local phone path**

5. **RTP Bridge**
   - When a trunk call is active and the HT801 RTP session is also active, bridge the two RTP streams in-process using SIPSorcery's `RTPSession`
   - Codec: G.711 PCMU (µ-law) — supported by both VoIP.ms and HT801
   - Log codec negotiation at Debug level

**Error handling:**
- All SIP operations wrapped in try/catch; exceptions logged via Serilog at Error level
- Registration loss triggers automatic re-registration after 5s backoff (configurable)
- RTP bridge failure transitions CallManager to `Idle` and logs at Error level

### 4.4 `TrunkRegistrationService`

Hosted service (`IHostedService`) responsible for maintaining SIP registration.

- Calls `GVTrunkAdapter.RegisterAsync()` on startup
- Re-registers every `RegisterIntervalSeconds - 10` seconds (10s before expiry)
- Exposes `bool IsRegistered` property
- Logs all registration state changes at Information level

### 4.5 `ISmsProvider` / `GmailSmsService`

**Background:** Google Voice sends inbound SMS and missed call notifications to the account's Gmail inbox as emails.

`ISmsProvider` interface:

```csharp
public interface ISmsProvider
{
    event Action<SmsNotification> OnSmsReceived;
    event Action<SmsNotification> OnMissedCallReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

`GmailSmsService` implementation:

1. **Authentication:** OAuth2 via Google.Apis.Gmail.v1 NuGet package. Credentials stored at `TrunkConfig.GmailCredentialsPath`. Use offline access; persist token to disk.
2. **Polling:** Every `GmailPollIntervalSeconds`, call `users.messages.list` with query `from:voice-noreply@google.com is:unread`
3. **Parsing:**
   - Inbound SMS: subject matches `"New text message from ..."` → parse sender number and body from message snippet
   - Missed call: subject matches `"Missed call from ..."` → parse caller number
4. **Deduplication:** Track last-processed Gmail message ID in memory; skip already-seen messages
5. **Mark as read:** After processing, mark message as read via `users.messages.modify`
6. **Models:**

```csharp
public record SmsNotification(
    string FromNumber,
    string? Body,           // null for missed calls
    DateTime ReceivedAt,
    SmsType Type            // Sms | MissedCall
);

public enum SmsType { Sms, MissedCall }
```

### 4.6 `CallLogService` / `CallLogEntry`

Stores a running log of all calls (inbound, outbound, missed) using SQLite via `Microsoft.Data.Sqlite`.

```csharp
public record CallLogEntry(
    int Id,
    DateTime StartedAt,
    DateTime? EndedAt,
    string Direction,       // "Inbound" | "Outbound"
    string RemoteNumber,
    string Status,          // "Answered" | "Missed" | "Rejected"
    int? DurationSeconds
);
```

`ICallLogService`:

```csharp
public interface ICallLogService
{
    Task AddEntryAsync(CallLogEntry entry);
    Task UpdateEntryAsync(int id, DateTime endedAt, string status, int durationSeconds);
    Task<IReadOnlyList<CallLogEntry>> GetRecentAsync(int count = 50);
}
```

- Schema created on first run via `CREATE TABLE IF NOT EXISTS`
- `GetRecentAsync` returns entries ordered by `StartedAt DESC`

### 4.7 UI Surface A — REST API + SignalR Hub (consumed by React / RotaryPhone)

These live in `Api/` inside the GVTrunk project. The React frontend in `RotaryPhone` communicates exclusively through these endpoints — no direct C# service references.

#### `GVTrunkController` — REST endpoints

Base route: `/api/gvtrunk`

| Method | Route | Description |
|---|---|---|
| `GET` | `/status` | Returns `{ isRegistered, callState, activeCallDurationSeconds }` |
| `GET` | `/calls` | Returns last 50 `CallLogEntry` records |
| `GET` | `/sms` | Returns last 20 `SmsNotification` records (in-memory) |
| `POST` | `/dial` | Body: `{ "number": "+13365551234" }` — calls `PlaceOutboundCall` |
| `POST` | `/reregister` | Forces `GVTrunkAdapter.RegisterAsync()` |

All endpoints return JSON. Error responses use standard ProblemDetails format. Return `409 Conflict` on `/dial` if call already active or trunk not registered.

#### `GVTrunkHub` — SignalR hub

Mount at `/hubs/gvtrunk`.

Server → client messages pushed to all connected clients:

| Event name | Payload | Trigger |
|---|---|---|
| `RegistrationChanged` | `{ isRegistered: bool }` | `ITrunkAdapter.OnRegistrationChanged` |
| `CallStateChanged` | `{ state: string, durationSeconds: int }` | `CallManager.StateChanged` |
| `SmsReceived` | `SmsNotification` (JSON) | `ISmsProvider.OnSmsReceived` |
| `MissedCallReceived` | `SmsNotification` (JSON) | `ISmsProvider.OnMissedCallReceived` |

No client → server methods required in Phase 1 (all mutations go through REST).

#### React integration notes (for `RotaryPhone` implementation)

- Install `@microsoft/signalr` npm package
- Create a `useGVTrunk` hook that:
  - Fetches `/api/gvtrunk/status`, `/calls`, and `/sms` on mount
  - Opens a SignalR connection to `/hubs/gvtrunk` and applies incoming events to local state
  - Exposes `dial(number)`, `forceReregister()` methods that POST to the REST endpoints
- Mount the dashboard UI at route `/gvtrunk` in the React app
- Dashboard panels mirror the Blazor component structure below (same four sections)

---

### 4.8 UI Surface B — Razor Components (consumed by Blazor / RTest)

These live in `Components/` inside the GVTrunk project. The Blazor host mounts them directly — no REST or SignalR consumption needed from the Blazor side (components inject `ITrunkAdapter`, `ICallLogService`, and `ISmsProvider` directly from DI).

#### Component tree

```
<GVTrunkDashboard />               ← single mount point for host pages
├── <TrunkStatusPanel />
├── <CallHistoryTable />
├── <SmsNotificationsPanel />
└── <OutboundDialPanel />
```

#### Component specifications

**`TrunkStatusPanel`**
- Registration status badge: `Registered` (green) / `Unregistered` (red) — bound to `ITrunkAdapter.IsRegistered`
- Current call state label — bound to `CallManager.CurrentState`
- Active call duration timer — increments via `System.Threading.Timer` when `InCall`
- Button: "Force Re-Register" → calls `ITrunkAdapter.RegisterAsync()`

**`CallHistoryTable`**
- Columns: Time, Direction, Number, Status, Duration
- Populated from `ICallLogService.GetRecentAsync(50)`
- Auto-refreshes every 10 seconds

**`SmsNotificationsPanel`**
- Last 20 notifications (in-memory, not persisted)
- Each entry: timestamp, from-number, type badge (SMS / Missed Call), body preview
- Refreshes immediately on `ISmsProvider.OnSmsReceived` / `OnMissedCallReceived`

**`OutboundDialPanel`**
- Text input: E.164 number
- Button: "Dial via GV Trunk" → calls `GVTrunkAdapter.PlaceOutboundCall(number)`
- Disabled when trunk not registered or call already active

**State update pattern (all components):** Subscribe to relevant service events in `OnInitializedAsync`; always call `await InvokeAsync(StateHasChanged)` in callbacks to marshal to the Blazor render thread. Unsubscribe in `IAsyncDisposable.DisposeAsync`.

---

## 5. NuGet Dependencies (New Project Only)

| Package | Purpose |
|---|---|
| `SIPSorcery` | Already in Core; add reference to GVTrunk project |
| `Google.Apis.Gmail.v1` | Gmail OAuth + message read/modify |
| `Google.Apis.Auth` | OAuth2 credential management |
| `Microsoft.Data.Sqlite` | Call log persistence |
| `Microsoft.AspNetCore.SignalR` | SignalR hub for real-time React push |
| `Microsoft.Extensions.Hosting.Abstractions` | `IHostedService` |
| `Microsoft.Extensions.Options` | `IOptions<TrunkConfig>` binding |
| `Serilog` | Logging (consistent with existing stack) |

---

## 6. DI Registration — Extension Methods

`GVTrunkServiceExtensions.cs` provides two extension methods so each host wires up with a single call and there is no copy-paste between projects.

### `AddGVTrunk()` — services (both hosts)

```csharp
public static IServiceCollection AddGVTrunk(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.Configure<TrunkConfig>(configuration.GetSection("GVTrunk"));
    services.AddSingleton<GVTrunkAdapter>();
    services.AddSingleton<ITrunkAdapter>(sp => sp.GetRequiredService<GVTrunkAdapter>());
    services.AddSingleton<ICallLogService, CallLogService>();
    services.AddSingleton<ISmsProvider, GmailSmsService>();
    services.AddHostedService<TrunkRegistrationService>();
    services.AddSignalR();           // no-op if already registered by host
    services.AddControllers();       // no-op if already registered by host
    return services;
}
```

### `MapGVTrunk()` — endpoints (both hosts)

```csharp
public static IEndpointRouteBuilder MapGVTrunk(this IEndpointRouteBuilder endpoints)
{
    endpoints.MapControllers();                         // picks up GVTrunkController
    endpoints.MapHub<GVTrunkHub>("/hubs/gvtrunk");
    return endpoints;
}
```

### Usage in `RotaryPhone` (`Program.cs`)

```csharp
builder.Services.AddGVTrunk(builder.Configuration);
// ...
app.MapGVTrunk();
```

### Usage in `RTest` (`Program.cs`)

```csharp
builder.Services.AddGVTrunk(builder.Configuration);
// ...
app.MapGVTrunk();   // still register hub/API even if Blazor doesn't use them directly
```

---

## 7. CallManager Integration (Additive Only)

The existing `CallManager` does not need to be modified. To wire the trunk path:

- `TrunkRegistrationService.StartAsync` subscribes to `GVTrunkAdapter.OnIncomingCall` and delegates to `CallManager.SimulateIncomingCall()`
- `CallManager.StateChanged` is observed by the Blazor dashboard and by `CallLogService` to record call start/end times
- If CallManager exposes a `CallStarted` / `CallEnded` event pair in a future refactor, `CallLogService` can subscribe directly — but this is **not** required for Phase 1

---

## 8. Google Voice Setup (Out-of-Band Configuration)

These are human steps, not automated by this component:

1. Log into Google Voice → Settings → Calls → **Forward to linked numbers** → Add the VoIP.ms DID number
2. In VoIP.ms portal: create a SIP sub-account, enable the DID, set "No Answer" forwarding to voicemail as desired
3. For outbound caller ID: verify the Google Voice number as a verified number in VoIP.ms caller ID settings
4. For Gmail API: create a Google Cloud project, enable Gmail API, generate OAuth2 credentials (Desktop app type), download `credentials.json` to `TrunkConfig.GmailCredentialsPath`

---

## 9. Future Hooks (Phase 2 — IVR / Automation)

The following extension points are designed into the component now but not implemented:

- `ITrunkAdapter` includes a `OnDtmfReceived(string digit)` event stub — reserved for IVR digit collection from trunk callers
- `CallLogService` schema includes a nullable `Notes` column — reserved for IVR interaction transcripts
- `GVTrunkAdapter.PlaceOutboundCall` returns a `string sessionId` — reserved for mid-call control (transfer, hold)
- `ISmsProvider` includes a stub `Task SendSmsAsync(string toNumber, string body)` — reserved for outbound SMS via a future provider (e.g., Twilio, once Google Voice SMS API is unavailable)

---

## 10. Testing Guidance for Claude Code

When implementing, generate:

1. **Unit tests** (`RotaryPhoneController.GVTrunk.Tests/`)
   - `GVTrunkAdapterTests`: mock `SIPTransport`; verify `OnIncomingCall` fires on INVITE, `IsRegistered` flips on 200 OK/timeout
   - `GmailSmsServiceTests`: mock `IGmailService`; verify correct parsing of GV notification email subjects and bodies
   - `CallLogServiceTests`: use in-memory SQLite (`:memory:`); verify add/update/query round-trips

2. **Integration smoke test**
   - `TrunkRegistrationIntegrationTest`: connect to VoIP.ms sandbox (if credentials available via env var `VOIPMS_TEST_CREDS`); verify registration succeeds within 5s

---

## 11. File / Folder Placement in Repository

### GVTrunk component (self-contained RCL)

```
RotaryPhoneController.GVTrunk/
├── Adapters/
│   └── GVTrunkAdapter.cs
├── Services/
│   ├── TrunkRegistrationService.cs
│   ├── GmailSmsService.cs
│   └── CallLogService.cs
├── Models/
│   ├── TrunkConfig.cs
│   ├── CallLogEntry.cs
│   └── SmsNotification.cs
├── Interfaces/
│   ├── ITrunkAdapter.cs
│   ├── ISmsProvider.cs
│   └── ICallLogService.cs
├── Api/
│   ├── GVTrunkController.cs            # REST API — consumed by React
│   └── GVTrunkHub.cs                   # SignalR hub — consumed by React
├── Components/
│   ├── GVTrunkDashboard.razor          # Top-level mount point — consumed by RTest
│   ├── TrunkStatusPanel.razor
│   ├── CallHistoryTable.razor
│   ├── SmsNotificationsPanel.razor
│   └── OutboundDialPanel.razor
├── Extensions/
│   └── GVTrunkServiceExtensions.cs     # AddGVTrunk() / MapGVTrunk()
└── RotaryPhoneController.GVTrunk.csproj
```

### `RotaryPhone` — React host (additive changes only)

```
RotaryPhone/
├── Program.cs                          # add: builder.Services.AddGVTrunk(...); app.MapGVTrunk();
└── ClientApp/src/
    ├── hooks/
    │   └── useGVTrunk.ts               # NEW: SignalR + REST hook
    ├── components/gvtrunk/
    │   ├── GVTrunkDashboard.tsx         # NEW: top-level dashboard page
    │   ├── TrunkStatusPanel.tsx
    │   ├── CallHistoryTable.tsx
    │   ├── SmsNotificationsPanel.tsx
    │   └── OutboundDialPanel.tsx
    └── App.tsx                         # add: <Route path="/gvtrunk" element={<GVTrunkDashboard />} />
```

### `RTest` — Blazor host (additive changes only)

```
RTest/
├── Program.cs                          # add: builder.Services.AddGVTrunk(...); app.MapGVTrunk();
└── Pages/
    └── GVTrunk.razor                   # NEW: one-liner page that mounts <GVTrunkDashboard />
```

### Tests

```
RotaryPhoneController.GVTrunk.Tests/
└── RotaryPhoneController.GVTrunk.Tests.csproj
```

---

---

## 12. RTest Integration Instructions

These are the complete steps to wire GVTrunk into the `RTest` Blazor project. All changes are additive.

### Step 1 — Add project reference

In `RTest.csproj`:

```xml
<ProjectReference Include="..\RotaryPhoneController.GVTrunk\RotaryPhoneController.GVTrunk.csproj" />
```

### Step 2 — Register services and endpoints

In `RTest/Program.cs`, add two lines:

```csharp
// After other builder.Services registrations:
builder.Services.AddGVTrunk(builder.Configuration);

// After app.Build(), before app.Run():
app.MapGVTrunk();
```

### Step 3 — Add `appsettings.json` config block

Copy the `"GVTrunk"` config block from section 4.1 into `RTest/appsettings.json` (or `appsettings.Development.json`). Update `CallLogDbPath` and `GmailCredentialsPath` for the RTest environment as appropriate.

### Step 4 — Add the Razor page

Create `RTest/Pages/GVTrunk.razor`:

```razor
@page "/gvtrunk"
@using RotaryPhoneController.GVTrunk.Components

<PageTitle>Google Voice Trunk</PageTitle>

<GVTrunkDashboard />
```

That is the entire page file. All UI logic lives in the component.

### Step 5 — Add nav link (optional)

In `RTest/Shared/NavMenu.razor`, add:

```razor
<NavLink href="gvtrunk">
    <span class="oi oi-phone" aria-hidden="true"></span> GV Trunk
</NavLink>
```

### Step 6 — Register the RCL's static assets

In `RTest/Pages/_Host.cshtml` (or `App.razor` depending on Blazor hosting model), ensure the RCL static asset path is included. For Blazor Server this is automatic when the project reference is in place — no additional step needed. Verify by checking that `_content/RotaryPhoneController.GVTrunk/` resolves at runtime if the components use any bundled CSS.

### What RTest does NOT need

- No `@microsoft/signalr` npm package — Blazor components inject services directly
- No REST polling — state updates come through DI-injected service events
- No custom hook or client-side state management
- No controller or hub code — `MapGVTrunk()` registers them but the Blazor UI doesn't call them

---

## 13. Acceptance Criteria

| # | Criterion |
|---|---|
| AC-1 | `GVTrunkAdapter` registers with VoIP.ms SIP server and `IsRegistered` becomes `true` within 5s |
| AC-2 | Inbound call to GV number rings the rotary phone via HT801 within 3s of INVITE arrival |
| AC-3 | Lifting handset answers the trunk call and two-way audio is established (G.711 PCMU) |
| AC-4 | Dialing from the rotary phone via trunk shows GV caller ID on the called party |
| AC-5 | React dashboard at `/gvtrunk` in `RotaryPhone` shows correct registration badge and updates call state in real time via SignalR |
| AC-6 | Blazor dashboard at `/gvtrunk` in `RTest` shows correct registration badge and updates call state in real time via DI events |
| AC-7 | Inbound GV SMS appears in dashboard SMS panel (both hosts) within 2× poll interval |
| AC-8 | Call log records start time, end time, direction, and duration for all calls |
| AC-9 | Existing Bluetooth HFP path in `RotaryPhone` is unaffected by this component |
| AC-10 | All unit tests pass on `dotnet test` |
| AC-11 | `RotaryPhone` and `RTest` each integrate by adding ≤ 5 lines of code to `Program.cs` and one new page file — no other modifications |
| AC-12 | Application starts cleanly on Raspberry Pi (ARM64, .NET 8) |