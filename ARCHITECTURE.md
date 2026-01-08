# **Rotary Phone Bluetooth Adapter Architecture (SIP/HT801 Based)**

## **1\. High-Level Block Diagram**

The system now operates as a network protocol bridge, converting analog telephone signals into SIP/RTP, and then translating those into Bluetooth HFP commands and audio streams.

| Block | Function | Interface Type |
| :---- | :---- | :---- |
| **Rotary Phone** | Input/Output Device | Analog Electrical Signals (FXS Compliant) |
| **Grandstream HT801 ATA** | **Protocol Converter (FXS to SIP/RTP)**. Provides talk voltage, ringing, pulse decoding, and audio hybrid. | Ethernet (SIP/RTP) |
| **Windows NUC** | **Protocol Bridge (SIP to Bluetooth HFP)**. Runs the C# SIP client, Call Manager, and Bluetooth stack. | Ethernet, Bluetooth HFP |

## **2\. Component and Interface Detail**

### **2.1 Rotary Phone & Grandstream HT801 ATA**

The HT801 handles all physical electrical complexity:

* **SIP/RTP Out:** The HT801 is configured to send SIP signaling (for call control, hook state, and dialed digits) and RTP packets (for audio data) to the NUC's IP address.  
* **Ringing Control:** The HT801 automatically applies the required 70-90V AC ring voltage to the phone line when it receives a SIP INVITE from the PC.  
* **Dialing Decode:** The HT801 decodes the rotary dial's pulses and reports the final dialed number via a SIP NOTIFY or INFO message.

### **2.2 Windows NUC (Protocol Bridge & Software)**

The NUC's role is purely software-driven network translation.

| Software Layer | Function | Interactions |
| :---- | :---- | :---- |
| **Configuration (appsettings.json)** | JSON-based configuration for SIP, phones, and features. Supports multiple phone instances. | **Application Startup** |
| **SIP Adapter (ISipAdapter/SIPSorceryAdapter)** | Acts as a lightweight SIP user agent. Listens for SIP messages (INVITE, BYE, NOTIFY) from the HT801. | **Grandstream HT801** (Ethernet/SIP) |
| **RTP Audio Bridge (IRtpAudioBridge)** | Decodes incoming RTP packets from the HT801 and encodes/packages audio data for the Bluetooth HFP stack. Routes audio based on call answer location. Uses **NAudio** for Windows audio integration. | **Grandstream HT801** (Ethernet/RTP), **Bluetooth HFP** |
| **Call Manager** | State machine (Idle, Dialing, Ringing, In-Call). Triggers Bluetooth commands based on SIP events. Integrates call history logging. | **SIP Adapter, Bluetooth HFP Adapter, RTP Bridge, Call History** |
| **Bluetooth HFP Adapter (IBluetoothHfpAdapter)** | Controls the Bluetooth stack to manage mobile phone pairing, call initiation, and termination. **Note:** Currently migrating from Linux BlueZ to Windows APIs. | **Mobile Phone** (Bluetooth HFP) |
| **Web API & Frontend** | Exposes REST/SignalR APIs and serves the TypeScript/React UI. | **User Interface** |

## **3\. Component Architecture**

### **A. Audio Routing System**

The system implements intelligent audio routing:

- **AudioRoute Enum**: Defines two routing destinations:
  - `RotaryPhone`: Audio flows through rotary phone handset
  - `CellPhone`: Audio flows through mobile phone Bluetooth
  
- **Automatic Routing Logic**:
  - When call answered on rotary phone (handset lifted): `AudioRoute.RotaryPhone`
  - When call answered on cell phone: `AudioRoute.CellPhone`
  - No user intervention required - system automatically routes audio

- **RTP Bridge**: Handles bidirectional audio streaming:
  - Rotary → RTP → Bluetooth → Mobile (outgoing audio)
  - Mobile → Bluetooth → RTP → Rotary (incoming audio)

### **B. Configuration System**

Configuration is stored in `appsettings.json`:

```json
{
  "RotaryPhone": {
    "SipListenAddress": "0.0.0.0",
    "SipPort": 5060,
    "RtpBasePort": 49000,
    "EnableCallHistory": true,
    "MaxCallHistoryEntries": 100,
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

### **C. Multiple Phone Support**

The system supports multiple rotary phones:
- Each phone has its own `RotaryPhoneConfig`
- `PhoneManagerService` manages multiple `CallManager` instances
- Call history tracks which phone handled each call

## **4\. Signal Flow Diagrams**

### **A. Outgoing Call Sequence (Dialing)**

1. **User lifts handset/dials:** Rotary Phone → HT801.  
2. **HT801:** Decodes pulse dialing → Sends SIP NOTIFY/INFO with the completed number string.  
3. **SIP Adapter:** Receives NOTIFY → Passes number to Call Manager.  
4. **Call Manager:** Creates call history entry → State transitions: DIALING → IN\_CALL → Triggers **Bluetooth HFP Adapter** to initiate the call.
5. **RTP Audio Bridge:** Starts bridging with `AudioRoute.RotaryPhone` (default for outgoing).

### **B. Incoming Call Sequence (Ringing)**

1. **Mobile Phone:** Receives incoming call → **Bluetooth HFP Adapter** detects event.  
2. **Call Manager:** Creates call history entry → State transitions: IDLE → RINGING.  
3. **Call Manager:** Generates and sends a SIP INVITE to the HT801.  
4. **HT801:** Receives SIP INVITE → Applies high AC voltage→ **Rotary Phone rings.**  
5a. **If answered on rotary phone:**
   - **User answers:** Hook Switch → HT801 → Sends SIP 200 OK/ACK back to the PC.  
   - **SIP Adapter:** Receives SIP 200 OK → Passes event to Call Manager.  
   - **Call Manager:** Updates call history with `AnsweredOn.RotaryPhone` → State transitions: RINGING → IN\_CALL.
   - **RTP Audio Bridge:** Starts with `AudioRoute.RotaryPhone` → Audio flows through rotary phone handset.
5b. **If answered on cell phone:**
   - **User answers:** Call accepted on mobile device → **Bluetooth HFP Adapter** fires `OnCallAnsweredOnCellPhone` event.
   - **Call Manager:** Updates call history with `AnsweredOn.CellPhone` → State transitions: RINGING → IN\_CALL (on mobile).
   - **RTP Audio Bridge:** Starts with `AudioRoute.CellPhone` → Audio automatically routes to cell phone.

### **C. Call Termination**

1. **User hangs up:** Either handset on-hook or mobile phone ends call.
2. **Call Manager:** Updates call history with end time and duration.
3. **RTP Audio Bridge:** Stops bridging.
4. **Bluetooth HFP Adapter:** Terminates call.
5. **State:** Transitions to IDLE.

