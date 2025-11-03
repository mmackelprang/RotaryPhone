# Rotary Phone Controller

A C# solution for controlling a vintage rotary telephone via Bluetooth adapter on Raspberry Pi.

## Solution Structure

```
RotaryPhoneController/
├── RotaryPhoneController.Core/       # Core class library
│   ├── CallState.cs                   # State enum (Idle, Dialing, Ringing, InCall)
│   ├── IGpioService.cs                # GPIO abstraction interface
│   ├── MockGpioService.cs             # Mock GPIO for development/testing
│   ├── RotaryDecoder.cs               # Pulse counting and digit decoding
│   └── CallManager.cs                 # Main state machine controller
│
└── RotaryPhoneController.WebUI/      # Blazor Server UI
    ├── Components/Pages/Home.razor    # Main monitoring/control interface
    └── Program.cs                     # Serilog configuration
```

## Features

### Core Library
- **State Machine**: Manages call states (Idle, Dialing, Ringing, InCall)
- **GPIO Abstraction**: Hardware-independent interface for GPIO operations
- **Rotary Decoder**: Decodes rotary dial pulses into digits (500ms inter-digit timeout)
- **Mock Implementation**: Test without physical hardware
- **Thread Safety**: All operations are thread-safe with proper locking
- **Resource Management**: Implements IDisposable for proper cleanup

### Web UI
- **Real-time Updates**: Live display of call state and dialed number
- **Mock Controls**: 
  - Hook switch toggle (Pick Up / Hang Up)
  - Incoming call simulation
  - Digit dialing (0-9)
- **Responsive Design**: Bootstrap-based UI
- **Comprehensive Logging**: Serilog output to console and debug

## Getting Started

### Prerequisites
- .NET 8.0 SDK or later
- Compatible with Windows, Linux, macOS

### Building the Solution

```bash
# Clone the repository
git clone https://github.com/mmackelprang/RotaryPhone.git
cd RotaryPhone

# Build the solution
dotnet build

# Run the Web UI
cd RotaryPhoneController.WebUI
dotnet run
```

The application will start on `http://localhost:5000` (HTTP) and `https://localhost:5001` (HTTPS).

## Usage

### Testing with Mock Controls

1. **Pick Up the Phone**
   - Click "Toggle Hook Switch (Pick Up)"
   - State changes from Idle → Dialing
   - Digit buttons become enabled

2. **Dial a Number**
   - Click digit buttons to simulate rotary dial
   - Each digit is decoded from simulated pulses
   - Number accumulates in the "Dialed Number" field

3. **Receive an Incoming Call**
   - Click "Simulate Incoming Call"
   - State changes to Ringing
   - Ringer activates (1.5s on, 3s off cycle)
   - Click "Toggle Hook Switch" to answer → InCall state

4. **Hang Up**
   - Click "Toggle Hook Switch" anytime to return to Idle

### State Transitions

```
Idle ──[Pick Up]──> Dialing ──[Complete Number]──> InCall ──[Hang Up]──> Idle
  │                                                    ▲
  └──[Incoming Call]──> Ringing ──[Pick Up]───────────┘
```

## Architecture

### CallManager (Singleton)
The central controller managing:
- State transitions with thread safety
- Hook switch event handling
- Rotary decoder integration
- Ringer control (background task)
- Mock Bluetooth HFP operations

### RotaryDecoder
- Counts pulses from rotary dial
- Converts pulse count to digits (1-9, 10=0)
- 500ms timeout to finalize digit
- Thread-safe pulse counting

### GPIO Service
- **IGpioService**: Abstract interface for hardware operations
- **MockGpioService**: Development/testing implementation
- Ready for real GPIO implementation (System.Device.Gpio)

## Logging

All operations are logged using Serilog:
- **Debug**: Pulse counts, timer events, detailed flow
- **Information**: State transitions, decoded digits, method calls
- **Warning**: Invalid inputs
- **Error**: Exceptions in async operations

View logs in:
- Console output (when running)
- Debug output (Visual Studio, VS Code)

## Future Enhancements

- [ ] Real GPIO implementation for Raspberry Pi
- [ ] Bluetooth HFP integration for actual phone calls
- [ ] Audio routing (ALSA/PulseAudio)
- [ ] Configuration UI for GPIO pin mapping
- [ ] Call history and logging
- [ ] Rotary dial debouncing tuning

## Dependencies

### Core Library
- Serilog 4.3.0
- System.Device.Gpio 4.0.1

### Web UI
- Serilog.AspNetCore 9.0.0
- Serilog.Sinks.Console 6.1.1
- Serilog.Sinks.Debug 3.0.0

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please ensure:
- Code follows existing patterns
- Thread safety is maintained
- All changes are logged appropriately
- Tests are added for new functionality
