# GVBridge — Setup Guide
**Component:** `RotaryPhoneController.GVBridge`  
**Repo:** RotaryPhone  
**Last updated:** March 2026  

This guide covers everything needed to get the GVBridge component running from a fresh clone — prerequisites, folder layout, first-run configuration, Chrome extension loading, and a step-by-step smoke test. Read this before opening a Claude Code session.

---

## Prerequisites

### On the Raspberry Pi (runtime host)

| Requirement | Version | Notes |
|---|---|---|
| .NET SDK | 8.0+ | `dotnet --version` to verify |
| .NET Runtime (ARM64) | 8.0+ | Included with SDK |
| NAudio.Core | via NuGet | Cross-platform; no ALSA/PulseAudio dependency |
| SQLite | system | `sudo apt install sqlite3` if not present |
| Chrome / Chromium | 116+ | Required for `tabCapture` + `offscreen` APIs |

```bash
# Verify .NET
dotnet --version

# Install SQLite if missing
sudo apt install sqlite3

# Install Chromium on Pi OS
sudo apt install chromium-browser
```

### On the development machine (build host)

| Requirement | Version |
|---|---|
| .NET SDK | 8.0+ |
| Node.js | 18+ (for React ClientApp) |
| Chrome | 116+ |
| Git | any recent |

---

## Step 1 — Repo Layout After Adding This Component

Run these commands from your repo root to create the new project structure before opening Claude Code:

```bash
# From repo root
mkdir -p RotaryPhoneController.GVBridge/{Adapters,Services,Models,Interfaces,Registry,Api,Components,Extensions}
mkdir -p RotaryPhoneController.GVBridge.Tests
mkdir -p ChromeExtension/{background,content,offscreen,icons}
```

The full expected layout after implementation:

```
RotaryPhone/                                        ← repo root
├── RotaryPhoneController.Core/                     # Existing — interfaces promoted here
│   └── Interfaces/
│       ├── ICallAdapter.cs                         # NEW (promoted from GVBridge)
│       ├── ICallAdapterRegistry.cs                 # NEW (promoted from GVBridge)
│       ├── ISmsProvider.cs                         # NEW (promoted from GVTrunk)
│       └── ICallLogService.cs                      # NEW (promoted from GVTrunk)
├── RotaryPhoneController.GVBridge/                 # NEW — Razor Class Library
│   ├── Adapters/
│   │   └── GVBrowserAdapter.cs
│   ├── Services/
│   │   ├── GVBridgeService.cs                      # WebSocket server
│   │   ├── AudioBridge.cs                          # PCM ↔ G.711/RTP
│   │   ├── GVSmsService.cs
│   │   └── CallLogService.cs
│   ├── Models/
│   │   ├── GVBridgeConfig.cs
│   │   ├── ExtensionMessage.cs
│   │   ├── CallLogEntry.cs
│   │   └── SmsNotification.cs
│   ├── Interfaces/
│   │   ├── ICallAdapter.cs                         # Also promoted to Core
│   │   ├── ICallAdapterRegistry.cs
│   │   ├── ISmsProvider.cs
│   │   └── ICallLogService.cs
│   ├── Registry/
│   │   ├── CallAdapterRegistry.cs
│   │   └── CallAdapterMode.cs
│   ├── Api/
│   │   ├── GVBridgeController.cs
│   │   └── GVBridgeHub.cs
│   ├── Components/
│   │   ├── GVBridgeDashboard.razor
│   │   ├── ConnectionModeSelector.razor            # Switches BT / SIP / GVBrowser
│   │   ├── BridgeStatusPanel.razor
│   │   ├── CallHistoryTable.razor
│   │   ├── SmsNotificationsPanel.razor
│   │   └── OutboundDialPanel.razor
│   ├── Extensions/
│   │   └── GVBridgeServiceExtensions.cs
│   └── RotaryPhoneController.GVBridge.csproj
├── RotaryPhoneController.GVBridge.Tests/           # NEW
│   └── RotaryPhoneController.GVBridge.Tests.csproj
├── RotaryPhone/                                    # Existing React host — additive only
│   ├── Program.cs                                  # + AddGVBridge / MapGVBridge
│   └── ClientApp/src/
│       ├── hooks/
│       │   └── useGVBridge.ts                      # NEW
│       └── components/
│           ├── ConnectionModeSelector.tsx           # NEW
│           └── gvbridge/
│               ├── GVBridgeDashboard.tsx
│               ├── BridgeStatusPanel.tsx
│               ├── CallHistoryTable.tsx
│               ├── SmsNotificationsPanel.tsx
│               └── OutboundDialPanel.tsx
└── ChromeExtension/                               # NEW — not a .NET project
    ├── manifest.json
    ├── background/
    │   └── service-worker.js
    ├── content/
    │   └── gv-bridge.js
    ├── offscreen/
    │   ├── offscreen.html
    │   └── audio-bridge.js
    └── icons/
        └── icon-*.png
```

---

## Step 2 — Create the .csproj Files

### `RotaryPhoneController.GVBridge.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AddRazorSupportForMvc>true</AddRazorSupportForMvc>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.SignalR" Version="1.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="8.*" />
    <PackageReference Include="NAudio.Core" Version="2.*" />
    <PackageReference Include="Serilog" Version="3.*" />
    <PackageReference Include="Serilog.Extensions.Logging" Version="8.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RotaryPhoneController.Core\RotaryPhoneController.Core.csproj" />
  </ItemGroup>

</Project>
```

### `RotaryPhoneController.GVBridge.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Moq" Version="4.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\RotaryPhoneController.GVBridge\RotaryPhoneController.GVBridge.csproj" />
  </ItemGroup>

</Project>
```

### Add both to the solution

```bash
dotnet sln add RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj
dotnet sln add RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj
```

---

## Step 3 — Configuration

Add the following block to `RotaryPhone/appsettings.json` (and `appsettings.Development.json` with local overrides):

```json
"GVBridge": {
  "WebSocketPort": 8765,
  "WebSocketHost": "127.0.0.1",
  "LocalRtpPort": 5070,
  "LocalIp": "192.168.1.20",
  "HT801Ip": "192.168.1.21",
  "HT801RtpPort": 5004,
  "AudioSampleRateHz": 16000,
  "AudioChannels": 1,
  "PcmFrameMs": 20,
  "ExtensionConnectTimeoutSeconds": 30,
  "CallLogDbPath": "/home/pi/.local/share/rotaryphone/calllog.db",
  "DefaultMode": "GVBrowser"
}
```

**Values to update for your environment:**

| Key | Where to find it |
|---|---|
| `LocalIp` | Pi's LAN IP — `hostname -I` on the Pi |
| `HT801Ip` | HT801's LAN IP — check your router's DHCP table or the HT801 web UI |
| `HT801RtpPort` | HT801 web UI → FXS Port Settings → Local RTP Port (default 5004) |
| `CallLogDbPath` | Any writable path on the Pi; directory must exist (`mkdir -p`) |

**Create the data directory on the Pi:**

```bash
mkdir -p /home/pi/.local/share/rotaryphone
```

---

## Step 4 — Wire Up `Program.cs`

In `RotaryPhone/Program.cs`, add two lines in the locations shown:

```csharp
using RotaryPhoneController.GVBridge.Extensions;

var builder = WebApplication.CreateBuilder(args);

// ... existing service registrations ...

builder.Services.AddGVBridge(builder.Configuration);   // ← ADD THIS

var app = builder.Build();

// ... existing middleware ...

app.MapGVBridge();                                      // ← ADD THIS

app.Run();
```

---

## Step 5 — Update `CallManager` for Multi-Mode Support

`CallManager` needs one change: swap its direct `ISipAdapter` dependency for `ICallAdapterRegistry`. This is the only modification to existing code required by this PRD.

**Before:**
```csharp
public class CallManager
{
    private readonly ISipAdapter _adapter;
    public CallManager(ISipAdapter adapter) { _adapter = adapter; }
}
```

**After:**
```csharp
public class CallManager
{
    private readonly ICallAdapterRegistry _registry;
    private ICallAdapter Adapter => _registry.ActiveAdapter;

    public CallManager(ICallAdapterRegistry registry)
    {
        _registry = registry;
        _registry.OnModeChanged += _ => RebindAdapterEvents();
        RebindAdapterEvents();
    }

    private void RebindAdapterEvents()
    {
        // Unsubscribe from previous adapter events, subscribe to new ones
        // Pattern: hold a reference to the previous adapter, unsubscribe, then subscribe to Adapter
    }
}
```

All other `CallManager` logic remains unchanged.

---

## Step 6 — Add React Route and Nav

In `RotaryPhone/ClientApp/src/App.tsx`, add the route:

```tsx
import { GVBridgeDashboard } from './components/gvbridge/GVBridgeDashboard';

// Inside your <Routes> block:
<Route path="/gvbridge" element={<GVBridgeDashboard />} />
```

In your navigation component, add a link:

```tsx
<NavLink to="/gvbridge">GV Bridge</NavLink>
```

---

## Step 7 — Load the Chrome Extension

The extension does not auto-install. Do this once on the machine running Chrome:

1. Open Chrome and navigate to `chrome://extensions`
2. Enable **Developer mode** (toggle in the top-right corner)
3. Click **Load unpacked**
4. Select the `ChromeExtension/` folder from the root of the RotaryPhone repo
5. The extension appears in the list as "RotaryPhone GV Bridge"
6. Pin it to the toolbar: click the puzzle-piece icon → pin "RotaryPhone GV Bridge"

**After any change to extension source files:**
1. Go to `chrome://extensions`
2. Click the ↺ refresh icon on the extension card
3. Reload `https://voice.google.com`

---

## Step 8 — First Run

### On the Pi (or dev machine):

```bash
cd RotaryPhone
dotnet run
```

Watch for this log line confirming the WebSocket server is listening:

```
[INF] GVBridgeService: WebSocket server listening on ws://127.0.0.1:8765
```

### In Chrome:

1. Navigate to `https://voice.google.com` and log in with your Google account
2. The extension's service worker activates automatically on page load
3. Watch the extension icon — it should show a green indicator within ~2 seconds
4. In the app dashboard at `/gvbridge`, the **Bridge Status** panel should show 🟢 **Connected**

If the status stays red, check:
- The .NET app is running and port 8765 is not blocked by firewall
- The extension loaded without errors (`chrome://extensions` → "Errors" button on the card)
- `voice.google.com` is fully loaded (not still at the login screen)

---

## Step 9 — Audio Device Setup on the Pi

The Pi needs a virtual audio loopback so the Chrome tab's audio output can be captured and routed to the RTP stack independently of any physical speakers.

### Install PulseAudio loopback module:

```bash
sudo apt install pulseaudio
pulseaudio --start

# Load the null sink (virtual output device)
pactl load-module module-null-sink sink_name=gvbridge_sink sink_properties=device.description=GVBridgeSink

# Load the loopback from null sink monitor to default input
pactl load-module module-loopback source=gvbridge_sink.monitor
```

### Configure Chromium to use the virtual device:

Launch Chromium with the virtual device as its audio output:

```bash
chromium-browser \
  --audio-output-device=gvbridge_sink \
  https://voice.google.com
```

Or set it as default in `chrome://settings/sound` once Chromium is open.

**Make it persistent across reboots** — add to `/etc/pulse/default.pa`:

```
load-module module-null-sink sink_name=gvbridge_sink sink_properties=device.description=GVBridgeSink
load-module module-loopback source=gvbridge_sink.monitor
```

---

## Step 10 — Smoke Test

Work through this checklist after initial setup to verify end-to-end function:

### Extension connectivity
- [ ] Dashboard shows 🟢 Extension Connected
- [ ] No errors in `chrome://extensions` → extension card → "Errors"
- [ ] No errors in Chrome DevTools → Background service worker console

### Inbound call
- [ ] Call the Google Voice number from a cell phone
- [ ] GV dialog appears in Chrome AND rotary phone rings via HT801 within 3 seconds
- [ ] Lift handset — GV answers the call
- [ ] Speak into cell phone — voice heard on rotary phone earpiece
- [ ] Speak into rotary phone — voice heard on cell phone
- [ ] Replace handset — call ends cleanly in both Chrome and HT801

### Outbound call
- [ ] Enter a number in the OutboundDialPanel and click Dial
- [ ] `voice.google.com` initiates the call
- [ ] Remote phone rings and answers
- [ ] Full-duplex audio works in both directions
- [ ] Hanging up from the rotary phone ends the call

### SMS
- [ ] Send an SMS to the Google Voice number from a cell phone
- [ ] SMS notification appears in the dashboard SMS panel within ~60 seconds

### Mode selector
- [ ] ConnectionModeSelector shows all three modes: Bluetooth, SIP Trunk, GV Browser
- [ ] GV Browser shows 🟢 Available (extension connected)
- [ ] Switch to Bluetooth — mode persists on page refresh
- [ ] Switch back to GV Browser — audio path restored

---

## Troubleshooting

### Extension connects but audio is silent inbound

The Chrome tab's audio output is not being captured. Verify:
1. PulseAudio null sink is loaded: `pactl list sinks short` — look for `gvbridge_sink`
2. Chromium is using the null sink as its audio output device
3. `AudioBridge` is logging "Inbound loop started" at Debug level (enable Serilog Debug output)

### Audio is choppy or delayed

- Verify `PcmFrameMs` is 20 in config
- Check Pi CPU usage during a call: `htop` — if above 85%, close other processes
- Increase `AudioBridge` thread priority: set `Thread.Priority = ThreadPriority.AboveNormal` on the audio loop threads

### Extension fails to connect to WebSocket

- Confirm the .NET app is running: `curl http://localhost:5000/health` (or whatever port)
- Confirm port 8765 is not in use: `ss -tlnp | grep 8765`
- Check firewall: `sudo ufw status` — port 8765 should be allowed on loopback (it binds to 127.0.0.1 only, so external firewall rules don't apply)
- Check extension background service worker log: `chrome://extensions` → GV Bridge → "Service Worker" → Inspect

### GV UI selectors stopped working after a Google update

Open `ChromeExtension/content/gv-bridge.js` and update the `SELECTORS` constant at the top of the file. Use Chrome DevTools → Inspector on `voice.google.com` to find the current ARIA labels for the affected buttons. Reload the extension after saving.

### Call log database errors on startup

```bash
# Ensure the directory exists and is writable by the app user
mkdir -p /home/pi/.local/share/rotaryphone
chmod 755 /home/pi/.local/share/rotaryphone

# Verify SQLite can create files there
sqlite3 /home/pi/.local/share/rotaryphone/test.db ".quit"
rm /home/pi/.local/share/rotaryphone/test.db
```

---

## Running as a systemd Service on the Pi

To have the .NET app start automatically on boot:

```bash
sudo nano /etc/systemd/system/rotaryphone.service
```

```ini
[Unit]
Description=RotaryPhone Controller
After=network.target pulseaudio.service

[Service]
Type=simple
User=pi
WorkingDirectory=/home/pi/RotaryPhone/RotaryPhone
ExecStart=/usr/bin/dotnet /home/pi/RotaryPhone/RotaryPhone/bin/Release/net8.0/RotaryPhone.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable rotaryphone
sudo systemctl start rotaryphone
sudo systemctl status rotaryphone
```

**Auto-start Chromium with GV after boot** — add to the Pi's autostart config (`/etc/xdg/lxsession/LXDE-pi/autostart` or equivalent):

```
@sleep 5
@chromium-browser --audio-output-device=gvbridge_sink --start-maximized https://voice.google.com
```

The 5-second delay gives `rotaryphone.service` time to start the WebSocket server before the extension tries to connect.

---

## Development Workflow

### Typical change cycle

```bash
# Backend change (C# service or adapter)
cd RotaryPhoneController.GVBridge
# Edit files
dotnet build
# Restart the running app (Ctrl+C then dotnet run, or use dotnet watch)

# Extension change (JS content/background/offscreen)
# Edit ChromeExtension/content/gv-bridge.js or similar
# chrome://extensions → ↺ refresh → reload voice.google.com

# React component change
cd RotaryPhone/ClientApp
npm start   # or the existing hot-reload setup in the project
```

### Running tests

```bash
dotnet test RotaryPhoneController.GVBridge.Tests
```

### Viewing call log directly

```bash
sqlite3 /home/pi/.local/share/rotaryphone/calllog.db \
  "SELECT * FROM CallLog ORDER BY StartedAt DESC LIMIT 20;"
```

---

## Notes for Claude Code Session

When starting a Claude Code session with this PRD, open it with:

```
claude code --context PRD-GVBrowserBridge.md --context SETUP-GVBridge.md
```

Or paste the contents of both files at the start of the session. Key things to tell Claude Code upfront:

1. **Start with `ICallAdapter` and `ICallAdapterRegistry` in Core** — everything else depends on these interfaces being in place first
2. **Do `GVBridgeService` (WebSocket server) second** — `GVBrowserAdapter` is a thin wrapper over it and can't be tested without it
3. **Do `AudioBridge` third** — it has the most unit-testable logic (codec, resampling) and the tests will catch subtle bugs before integration
4. **Do the Chrome extension last** — it's the hardest to iterate on and requires a running backend to test against
5. **The `CallManager` change is the only modification to existing code** — confirm with Claude Code that it makes no other changes to existing `.cs` files outside the new projects and Core interfaces

---

*This file should live at `RotaryPhone/docs/SETUP-GVBridge.md` in the repo.*
