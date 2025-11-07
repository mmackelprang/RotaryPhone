# Rotary Phone Controller

A C# .NET solution that bridges a rotary telephone with modern mobile phones using SIP protocol and Bluetooth HFP. This system uses a Grandstream HT801 ATA (Analog Telephone Adapter) to interface with the rotary phone and a Raspberry Pi running this software to bridge to a mobile phone via Bluetooth.

## Architecture

```
Rotary Phone <--(FXS)--> HT801 ATA <--(SIP/RTP)--> Raspberry Pi <--(Bluetooth HFP)--> Mobile Phone
```

The system consists of two main components:
1. **RotaryPhoneController.Core**: Class library containing SIP logic and state machine
2. **RotaryPhoneController.WebUI**: Blazor Server web application for monitoring and control

## Features

### Core Library
- **CallState Management**: Four-state state machine (Idle, Dialing, Ringing, InCall)
- **SIP Integration**: Full SIPSorcery-based adapter supporting:
  - UDP transport on port 5060
  - NOTIFY/INFO message handling for dial pulses and hook state
  - INVITE generation to trigger HT801 ringing
  - BYE message handling for call termination
  - G.711 PCMU codec SDP generation
- **Bluetooth HFP Integration Framework**: Interfaces for Hands-Free Profile with:
  - Call initiation and termination
  - Automatic audio routing based on where call is answered
  - Event-driven architecture for call state changes
  - Mock implementation for testing (ready for actual HFP implementation)
- **RTP Audio Bridge Framework**: Interfaces for audio stream bridging between:
  - SIP/RTP audio from HT801
  - Bluetooth HFP audio to/from mobile phone
  - Automatic routing to rotary phone or cell phone
  - Mock implementation for testing (ready for actual implementation)
- **Configuration File Support**: JSON-based configuration via appsettings.json
  - SIP server settings
  - Multiple phone support
  - Call history settings
  - Individual phone configurations (HT801 IP, extension, Bluetooth MAC)
- **Call History Logging**: Comprehensive call tracking with:
  - Incoming/outgoing call direction
  - Phone number and timestamp
  - Call duration
  - Where call was answered (rotary phone vs cell phone)
  - Per-phone history with filtering
- **Multiple Phone Support**: Architecture supports multiple rotary phone instances
- **Event-Driven Architecture**: Clean interfaces with events for loose coupling
- **Comprehensive Logging**: Serilog integration with detailed debug information

### Web UI
- **Real-Time Monitoring**: Live state updates via Blazor Server/SignalR
- **Multiple Phone Support**: UI displays all configured phones
  - Phone selector to switch between phones
  - Status overview showing state of all phones
  - Mock controls operate on selected phone
- **Contact List Management**: Built-in contact management
  - Add, edit, delete contacts
  - Search contacts by name or number
  - Contact names displayed in call history
  - Contact names shown during calls
  - JSON import/export functionality
- **Call History Page**: View and manage call history
  - Filter by phone (for multiple phone setups)
  - See call duration and direction
  - Track where calls were answered
  - Display contact names instead of just numbers
  - Clear history
- **HT801 Configuration Helper**: Web-based configuration assistant
  - Manage HT801 settings for each phone
  - Test connection to HT801 devices
  - View and save configuration settings
  - Step-by-step manual configuration instructions
  - Network, SIP, and phone settings management
- **Mock Controls**: Testing interface for:
  - Simulating incoming calls
  - Simulating handset off-hook/on-hook events
  - Simulating received digits from rotary dial
- **Responsive Design**: Bootstrap 5-based interface
- **State Flow Visualization**: Clear display of state machine transitions

## Prerequisites

- .NET 9.0 SDK or later
- Linux environment (tested on Raspberry Pi)
- Grandstream HT801 ATA configured for your network
- Network connectivity between Pi and HT801
- **For Bluetooth HFP**: BlueZ Bluetooth stack and bluetoothctl (included in most Linux distributions)
- **For Audio**: NAudio library (included via NuGet)

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

Edit `src/RotaryPhoneController.WebUI/appsettings.json` to configure your setup:

```json
{
  "RotaryPhone": {
    "SipListenAddress": "0.0.0.0",
    "SipPort": 5060,
    "RtpBasePort": 49000,
    "EnableCallHistory": true,
    "MaxCallHistoryEntries": 100,
    "EnableContacts": true,
    "ContactsStoragePath": "data/contacts.json",
    "BluetoothDeviceName": "Rotary Phone",
    "UseActualBluetoothHfp": false,
    "UseActualRtpAudioBridge": false,
    "Phones": [
      {
        "Id": "default",
        "Name": "Rotary Phone",
        "HT801IpAddress": "192.168.1.10",
        "HT801Extension": "1000",
        "BluetoothMacAddress": null
      }
    ]
  }
}
```

Key settings:
- **SipListenAddress**: IP address for SIP listening (0.0.0.0 for all interfaces)
- **SipPort**: SIP server port (default: 5060)
- **EnableCallHistory**: Enable/disable call history logging
- **MaxCallHistoryEntries**: Maximum number of call history entries to keep
- **EnableContacts**: Enable/disable contact list feature (default: true)
- **ContactsStoragePath**: Path to store contacts JSON file
- **BluetoothDeviceName**: Name shown to phones when pairing (default: "Rotary Phone")
- **UseActualBluetoothHfp**: Enable actual BlueZ HFP implementation (default: false for mock)
- **UseActualRtpAudioBridge**: Enable actual RTP audio bridge (default: false for mock)
- **Phones**: Array of phone configurations (supports multiple phones)
  - **Id**: Unique identifier for this phone
  - **Name**: Friendly display name
  - **HT801IpAddress**: IP address of your Grandstream HT801 ATA
  - **HT801Extension**: SIP extension to ring on the HT801
  - **BluetoothMacAddress**: Optional MAC address of paired mobile phone

## Running the Application

### Development Mode

```bash
cd src/RotaryPhoneController.WebUI
dotnet run
```

The web UI will be available at `http://localhost:5555` (or your configured address).

### Production Deployment

```bash
cd src/RotaryPhoneController.WebUI
dotnet publish -c Release -o /path/to/deploy
cd /path/to/deploy
dotnet RotaryPhoneController.WebUI.dll
```

### Running as a Service (Linux)

Create a systemd service file `/etc/systemd/system/rotaryphone.service`:

```ini
[Unit]
Description=Rotary Phone Controller
After=network.target

[Service]
Type=notify
WorkingDirectory=/path/to/deploy
ExecStart=/usr/bin/dotnet /path/to/deploy/RotaryPhoneController.WebUI.dll
Restart=always
User=pi
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5555

[Install]
WantedBy=multi-user.target
```

Enable and start the service:

```bash
sudo systemctl enable rotaryphone
sudo systemctl start rotaryphone
sudo systemctl status rotaryphone
```

## HT801 Configuration

You can configure your Grandstream HT801 in two ways:

### Option 1: Web-Based Configuration Helper (Recommended)

1. Navigate to the **HT801 Config** page in the web UI
2. Select the phone you want to configure
3. Enter the HT801 device details:
   - IP address
   - Admin credentials
4. Click **Test Connection** to verify connectivity
5. Configure settings:
   - SIP server settings (IP, port, extension)
   - Audio codec preferences
   - Pulse dialing settings
   - Ring voltage and hook flash timing
6. Click **Save Configuration**
7. Follow the manual configuration instructions displayed on the page to apply settings to your HT801

### Option 2: Manual Configuration via HT801 Web Interface

Configure your Grandstream HT801 with the following settings:

#### FXS Port Settings
1. **Primary SIP Server**: Your Raspberry Pi's IP address
2. **SIP User ID**: Any identifier (e.g., "rotaryphone")
3. **Account Active**: Yes

#### Audio Settings
1. **Preferred Vocoder**: choice1: PCMU
2. **Enable Call Waiting**: No (optional)

#### Port Settings
1. **Hook Flash Timing**: Set according to your rotary phone (typically 300ms)
2. **Pulse Dial**: Enable
3. **Pulse Rate**: 10 pps (typical for rotary phones)

## Usage

### Call Flows

#### Incoming Call (Mobile → Rotary Phone)
1. Mobile phone receives a call
2. System triggers `SimulateIncomingCall()`
3. State: Idle → Ringing
4. SIP INVITE sent to HT801
5. Rotary phone rings
6. **If answered on rotary phone:** User lifts handset (OFF-HOOK)
   - State: Ringing → InCall
   - Audio bridge established through rotary phone
7. **If answered on cell phone:** User accepts call on mobile device
   - State: Ringing → InCall (on mobile)
   - Audio automatically routes to cell phone without user intervention

#### Outgoing Call (Rotary Phone → Mobile)
1. User lifts handset (OFF-HOOK)
2. State: Idle → Dialing
3. User dials number on rotary dial
4. HT801 sends NOTIFY with digits
5. State: Dialing → InCall
6. Bluetooth HFP initiates call on mobile
7. Audio bridge established

#### Hang Up
1. User places handset on-hook (ON-HOOK)
2. State: Any → Idle
3. Call terminated

### Web UI Controls

The web interface provides several pages for managing your rotary phone:

#### Home Page
- **Phone Selection** (multiple phones): Select which phone to monitor and control
- **All Phones Status**: View the current state of all configured phones at once
- **Mock Controls** for testing:
  - **Simulate Mobile Incoming Call**: Triggers the ringing flow
  - **Simulate Handset OFF-HOOK**: Simulates lifting the handset
  - **Simulate Handset ON-HOOK**: Simulates placing handset down
  - **Simulate Digits Received**: Manually enter a phone number to simulate dial pulses

#### Call History Page
- View all incoming and outgoing calls
- See contact names (if saved in contacts)
- Filter by phone (for multiple phone setups)
- Track call duration and where calls were answered
- Clear history

#### Contacts Page
- Add, edit, and delete contacts
- Search contacts by name or phone number
- Contacts automatically appear in call history and during active calls
- Import/export contacts as JSON

#### HT801 Configuration Page
- Manage HT801 settings for each phone
- Test connection to HT801 devices
- Configure SIP, network, and phone settings
- View step-by-step manual configuration instructions

## Development

### Project Structure

```
RotaryPhone/
├── src/
│   ├── RotaryPhoneController.Core/           # Core business logic
│   │   ├── Audio/                            # Audio components
│   │   │   ├── IBluetoothHfpAdapter.cs       # Bluetooth HFP interface
│   │   │   ├── IRtpAudioBridge.cs            # RTP bridge interface
│   │   │   ├── MockBluetoothHfpAdapter.cs    # Mock HFP for testing
│   │   │   ├── BlueZHfpAdapter.cs            # Actual HFP implementation
│   │   │   ├── MockRtpAudioBridge.cs         # Mock RTP for testing
│   │   │   └── RtpAudioBridge.cs             # Actual RTP implementation
│   │   ├── CallHistory/                      # Call history tracking
│   │   │   ├── CallHistoryEntry.cs           # Call history data model
│   │   │   ├── CallHistoryService.cs         # History service impl
│   │   │   └── ICallHistoryService.cs        # History service interface
│   │   ├── Contacts/                         # Contact management
│   │   │   ├── Contact.cs                    # Contact data model
│   │   │   ├── ContactService.cs             # Contact service impl
│   │   │   └── IContactService.cs            # Contact service interface
│   │   ├── HT801/                            # HT801 configuration
│   │   │   ├── HT801Config.cs                # HT801 config models
│   │   │   ├── HT801ConfigService.cs         # Config service impl
│   │   │   └── IHT801ConfigService.cs        # Config service interface
│   │   ├── Configuration/                    # Configuration models
│   │   │   └── AppConfiguration.cs           # Config data models
│   │   ├── CallManager.cs                    # State machine
│   │   ├── CallState.cs                      # State enum
│   │   ├── ISipAdapter.cs                    # SIP interface
│   │   ├── PhoneManagerService.cs            # Multiple phone manager
│   │   └── SIPSorceryAdapter.cs              # SIP implementation
│   └── RotaryPhoneController.WebUI/          # Web application
│       ├── Components/
│       │   ├── Layout/
│       │   │   └── NavMenu.razor             # Navigation menu
│       │   └── Pages/
│       │       ├── CallHistory.razor         # Call history UI
│       │       ├── Contacts.razor            # Contact management UI
│       │       ├── HT801Configuration.razor  # HT801 config UI
│       │       └── Home.razor                # Main UI
│       ├── appsettings.json                  # Configuration file
│       ├── Program.cs                        # Startup configuration
│       └── Properties/
│           └── launchSettings.json           # Launch configuration
├── RotaryPhoneController.sln                 # Solution file
├── ARCHITECTURE.md                           # Architecture documentation
├── PROJECT_PLAN.md                           # Project plan
└── README.md                                 # This file
```

### Adding Features

1. **Extend ISipAdapter**: Add new events or methods to the interface
2. **Implement in SIPSorceryAdapter**: Add SIP message handling logic
3. **Update CallManager**: Add state machine logic
4. **Update UI**: Add controls or displays in Home.razor

### Logging

The system uses Serilog for logging. Logs include:
- SIP message reception and transmission
- State transitions
- Event firing
- Error conditions

View logs in:
- Console output (development)
- Systemd journal (production): `journalctl -u rotaryphone -f`

## Troubleshooting

### SIP Port Conflict
If port 5060 is in use:
```bash
sudo lsof -i :5060
```
Kill the conflicting process or change the port in Program.cs.

### HT801 Not Responding
1. Use the **HT801 Config** page in the web UI to test connection
2. Verify network connectivity: `ping <HT801_IP>`
3. Check HT801 SIP registration status in web interface
4. Verify firewall allows UDP port 5060
5. Check SIP logs in application output

### Rotary Phone Not Ringing
1. Verify INVITE is being sent (check logs)
2. Check HT801 FXS port settings using the HT801 Config page
3. Adjust ring voltage level in the HT801 Config page
4. Verify rotary phone ringer coil is functional
5. Check HT801 ring voltage settings

### Audio Issues
1. Verify G.711 codec is enabled on HT801
2. Check RTP port forwarding
3. Verify microphone bias voltage on rotary phone
4. Test audio with direct HT801 connection

## Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- **SIPSorcery**: Excellent .NET SIP library
- **Serilog**: Structured logging framework
- **Blazor**: Microsoft's web UI framework
- Grandstream for the HT801 ATA documentation

## Implementation Status

### Completed Features ✅
- [x] **Configuration file support** - Full JSON-based configuration via appsettings.json
- [x] **Call history logging** - Comprehensive call tracking with web UI
- [x] **Multiple phone support** - Full support for multiple phones
  - ✅ Backend architecture with PhoneManagerService
  - ✅ UI displays all phones with phone selector
  - ✅ Call history filtering by phone
  - ✅ Individual phone status monitoring
- [x] **Contact list integration** - Built-in contact management
  - ✅ Add, edit, delete contacts
  - ✅ Search functionality
  - ✅ JSON import/export
  - ✅ Contact names in call history
  - ✅ Contact names during active calls
- [x] **Web-based HT801 configuration helper**
  - ✅ Configuration UI for all HT801 settings
  - ✅ Connection testing
  - ✅ Per-phone configuration management
  - ✅ Step-by-step manual configuration instructions
- [x] **Bluetooth HFP integration framework** - Interfaces defined with mock implementation
  - Audio routing logic implemented
  - Automatic routing based on where call is answered
  - Ready for actual Bluetooth stack integration
- [x] **RTP audio stream bridging framework** - Interfaces defined with mock implementation
  - Ready for actual audio codec and streaming implementation

### Future Enhancements

#### High Priority
- [x] **Actual Bluetooth HFP implementation** (✅ Completed)
  - ✅ Implemented using BlueZ on Linux for HFP integration
  - ✅ Bluetooth device name configurable as "Rotary Phone"
  - ✅ Supports multiple device pairing (at least 2 phones)
  - ✅ All interfaces are implemented with HFP call control
  - ✅ Audio routing logic is complete - automatically routes based on where call is answered
  - ⚠️ Note: Requires BlueZ and bluetoothctl to be installed on the system
  - ⚠️ Configuration flag `UseActualBluetoothHfp` to enable (default: false for compatibility)
- [x] **Actual RTP audio stream bridging implementation** (✅ Completed)
  - ✅ Audio codec integration (G.711 PCMU) using NAudio
  - ✅ Bidirectional audio streaming between HT801 and Bluetooth
  - ✅ All interfaces are implemented with proper routing
  - ⚠️ Configuration flag `UseActualRtpAudioBridge` to enable (default: false for compatibility)

#### Medium Priority
- [ ] Automated testing suite
- [ ] Docker containerization
- [ ] Full HT801 web API integration for automatic configuration push
- [ ] Voice mail integration

#### Low Priority
- [ ] HTTPS support for web UI
- [ ] Mobile app for remote monitoring
- [ ] Call recording capability

## Support

For issues, questions, or contributions, please visit:
https://github.com/mmackelprang/RotaryPhone/issues
