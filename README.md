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
- **Event-Driven Architecture**: Clean interfaces with events for loose coupling
- **Comprehensive Logging**: Serilog integration with detailed debug information

### Web UI
- **Real-Time Monitoring**: Live state updates via Blazor Server/SignalR
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

### 3. Configure IP Addresses

Edit `src/RotaryPhoneController.WebUI/Program.cs` to set your network configuration:

```csharp
// Set your Raspberry Pi's IP address (or use 0.0.0.0 for all interfaces)
var adapter = new SIPSorceryAdapter(serilogLogger, "0.0.0.0", 5060);
```

Edit `src/RotaryPhoneController.Core/CallManager.cs` in the `SimulateIncomingCall()` method:

```csharp
// Set your HT801's IP address
_sipAdapter.SendInviteToHT801("1000", "192.168.1.10");
```

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

Configure your Grandstream HT801 with the following settings:

### FXS Port Settings
1. **Primary SIP Server**: Your Raspberry Pi's IP address
2. **SIP User ID**: Any identifier (e.g., "rotaryphone")
3. **Account Active**: Yes

### Audio Settings
1. **Preferred Vocoder**: choice1: PCMU
2. **Enable Call Waiting**: No (optional)

### Port Settings
1. **Hook Flash Timing**: Set according to your rotary phone
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

The web interface provides mock controls for testing:

- **Simulate Mobile Incoming Call**: Triggers the ringing flow
- **Simulate Handset OFF-HOOK**: Simulates lifting the handset
- **Simulate Handset ON-HOOK**: Simulates placing handset down
- **Simulate Digits Received**: Manually enter a phone number to simulate dial pulses

## Development

### Project Structure

```
RotaryPhone/
├── src/
│   ├── RotaryPhoneController.Core/       # Core business logic
│   │   ├── CallManager.cs                # State machine
│   │   ├── CallState.cs                  # State enum
│   │   ├── ISipAdapter.cs                # SIP interface
│   │   └── SIPSorceryAdapter.cs          # SIP implementation
│   └── RotaryPhoneController.WebUI/      # Web application
│       ├── Components/
│       │   └── Pages/
│       │       └── Home.razor            # Main UI
│       ├── Program.cs                    # Startup configuration
│       └── Properties/
│           └── launchSettings.json       # Launch configuration
├── RotaryPhoneController.sln             # Solution file
├── ARCHITECTURE.md                       # Architecture documentation
├── PROJECT_PLAN.md                       # Project plan
└── README.md                             # This file
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
1. Verify network connectivity: `ping <HT801_IP>`
2. Check HT801 SIP registration status in web interface
3. Verify firewall allows UDP port 5060
4. Check SIP logs in application output

### Rotary Phone Not Ringing
1. Verify INVITE is being sent (check logs)
2. Check HT801 FXS port settings
3. Verify rotary phone ringer coil is functional
4. Check HT801 ring voltage settings

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

## Future Enhancements

- [ ] Actual Bluetooth HFP integration (currently mocked)
  - **Audio Routing Requirement:** When HFP implementation is complete, the system must automatically route audio based on where the call is answered:
    - If call is answered on the rotary phone (handset lifted), audio routes through the rotary phone
    - If call is answered on the cell phone device, audio routes to the cell phone without any user intervention to select microphone/speaker
- [ ] RTP audio stream bridging
- [ ] Configuration file support
- [ ] Multiple phone support
- [ ] Call history logging
- [ ] Contact list integration
- [ ] Web-based HT801 configuration
- [ ] Automated testing suite
- [ ] Docker containerization
- [ ] HTTPS support for web UI
- [ ] Mobile app for remote monitoring

## Support

For issues, questions, or contributions, please visit:
https://github.com/mmackelprang/RotaryPhone/issues
