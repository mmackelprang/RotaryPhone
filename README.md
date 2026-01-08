# Rotary Phone Controller

A C# .NET solution that bridges a rotary telephone with modern mobile phones using SIP protocol and Bluetooth HFP. This system uses a Grandstream HT801 ATA (Analog Telephone Adapter) to interface with the rotary phone and a Windows NUC running this software to bridge to a mobile phone via Bluetooth.

## Architecture

```
Rotary Phone <--(FXS)--> HT801 ATA <--(SIP/RTP)--> Windows NUC <--(Bluetooth HFP)--> Mobile Phone
```

The system consists of two main components:
1. **RotaryPhoneController.Core**: Class library containing SIP logic and state machine
2. **RotaryPhoneController.WebUI**: Web API and React/TypeScript frontend for monitoring and control (In Progress)

## Features

### Core Library
- **CallState Management**: Four-state state machine (Idle, Dialing, Ringing, InCall)
- **SIP Integration**: Full SIPSorcery-based adapter supporting:
  - UDP transport on port 5060
  - NOTIFY/INFO message handling for dial pulses and hook state
  - INVITE generation to trigger HT801 ringing
  - BYE message handling for call termination
  - G.711 PCMU codec SDP generation
- **Bluetooth HFP Integration**:
  - *Planned*: Windows Bluetooth HFP Adapter (replacing Linux BlueZ)
  - Interfaces for Hands-Free Profile call control
  - Automatic audio routing
- **RTP Audio Bridge**:
  - SIP/RTP audio from HT801
  - Windows Core Audio integration (via NAudio)
  - Automatic routing to rotary phone or cell phone
- **Configuration File Support**: JSON-based configuration
- **Call History Logging**: Comprehensive call tracking
- **Multiple Phone Support**: Architecture supports multiple rotary phone instances

### Web UI (Planned Migration to TypeScript)
- **Real-Time Monitoring**: Live state updates via SignalR
- **Modern Stack**: React + TypeScript frontend
- **Contact List Management**: Built-in contact management
- **Call History Page**: View and manage call history
- **HT801 Configuration Helper**: Web-based configuration assistant

## Prerequisites

- .NET 9.0 SDK or later
- Windows 10/11 (NUC or similar PC)
- Grandstream HT801 ATA configured for your network
- Network connectivity between PC and HT801
- **Bluetooth**: Bluetooth 4.0+ Adapter supporting HFP (Implementation Pending)
- **Audio**: NAudio library (included via NuGet)

## Installation

### 1. Clone the Repository

```bash
git clone https://github.com/mmackelprang/RotaryPhone.git
cd RotaryPhone
```

### 2. Build the Solution

```bash
dotnet build
```

### 3. Configure Application Settings

Edit `src/RotaryPhoneController.WebUI/appsettings.json` to configure your setup. Ensure `SipListenAddress` binds to your LAN IP.

```json
{
  "RotaryPhone": {
    "SipListenAddress": "0.0.0.0",
    "SipPort": 5060,
    "UseActualBluetoothHfp": false, 
    "UseActualRtpAudioBridge": true
  }
}
```

## Future Roadmap (2026)

See `2026_PROJECT_PLAN.md` for the detailed migration roadmap to Windows NUC and TypeScript UI.

## HT801 Configuration

To ensure the Grandstream HT801 communicates correctly with the controller, configure the following settings via its web interface:

### 1. Basic Settings
*   **SIP Server**: Enter the IP address of the Windows PC running this software (e.g., `192.168.1.100`).

### 2. FXS Port Settings
*   **Primary SIP Server**: Same as above.
*   **SIP User ID**: Any value (e.g., `1000`).
*   **Authenticate ID**: Any value.
*   **Authenticate Password**: Any value.
*   **Name**: Rotary Phone.
*   **NAT Traversal**: No (assuming local LAN).

### 3. Dialing & Pulse Settings
*   **Pulse Dialing**: **Yes** (Crucial for rotary phones).
*   **Enable Hook Flash**: **Yes** (via SIP INFO).
*   **Off-hook Auto-Dial**: **No** (Disable).
*   **No Key Entry Timeout**: **4** (Seconds to wait after dialing before sending the call).
*   **Use # as Dial Key**: **No**.

### 4. Audio Settings
*   **Preferred Vocoder**: **PCMU** (Choice 1).
*   **SLIC Setting**: **USA** (or match your region for correct ring voltage).
*   **High Ring Power**: **Enable** (Rotary phones often require higher voltage to ring the bell).

## Usage


### Completed Features ✅
- [x] **Configuration file support**
- [x] **Call history logging**
- [x] **Multiple phone support**
- [x] **Contact list integration**
- [x] **Web-based HT801 configuration helper**
- [x] **RTP audio stream bridging** (NAudio/Windows compatible)

### In Progress / Planned 🚧
- [ ] **Windows Bluetooth HFP Adapter** (Migrating from BlueZ)
- [ ] **React/TypeScript UI Migration**
- [ ] **SignalR Real-time API**
