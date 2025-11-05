# **Rotary Phone Bluetooth Adapter Architecture (SIP/HT801 Based)**

## **1\. High-Level Block Diagram**

The system now operates as a network protocol bridge, converting analog telephone signals into SIP/RTP, and then translating those into Bluetooth HFP commands and audio streams.

| Block | Function | Interface Type |
| :---- | :---- | :---- |
| **Rotary Phone** | Input/Output Device | Analog Electrical Signals (FXS Compliant) |
| **Grandstream HT801 ATA** | **Protocol Converter (FXS to SIP/RTP)**. Provides talk voltage, ringing, pulse decoding, and audio hybrid. | Ethernet (SIP/RTP) |
| **Raspberry Pi** | **Protocol Bridge (SIP to Bluetooth HFP)**. Runs the C\# SIP client, Call Manager, and Bluetooth stack. | Ethernet, Bluetooth HFP |

## **2\. Component and Interface Detail**

### **2.1 Rotary Phone & Grandstream HT801 ATA**

The HT801 handles all physical electrical complexity:

* **SIP/RTP Out:** The HT801 is configured to send SIP signaling (for call control, hook state, and dialed digits) and RTP packets (for audio data) to the Raspberry Pi's IP address.  
* **Ringing Control:** The HT801 automatically applies the required 70-90V AC ring voltage to the phone line when it receives a SIP INVITE from the Pi.  
* **Dialing Decode:** The HT801 decodes the rotary dial's pulses and reports the final dialed number via a SIP NOTIFY or INFO message.

### **2.2 Raspberry Pi Zero 2 (Protocol Bridge & Software)**

The Pi's role is purely software-driven network translation.

| Software Layer | Function | Interactions |
| :---- | :---- | :---- |
| **SIP Adapter (C\#)** | Acts as a lightweight SIP user agent. Listens for SIP messages (INVITE, BYE, NOTIFY) from the HT801. | **Grandstream HT801** (Ethernet/SIP) |
| **RTP Audio Handler (C\#)** | Decodes incoming RTP packets from the HT801 and encodes/packages audio data for the Bluetooth HFP stack. | **Grandstream HT801** (Ethernet/RTP) |
| **Call Manager (Application Logic)** | State machine (Idle, Dialing, Ringing, In-Call). Triggers Bluetooth commands based on SIP events. | **SIP Adapter, Bluetooth Manager** |
| **Bluetooth HFP Manager** | C\# code responsible for controlling the Pi's native Bluetooth stack (e.g., BlueZ) to manage mobile phone pairing, call initiation, and termination. **Audio Routing Requirement:** Must automatically route audio based on where call is answered (rotary phone vs cell phone). | **Mobile Phone** (Bluetooth HFP) |
| **OS Audio Bridge (Linux)** | Manages routing the RTP audio stream to the Bluetooth audio driver (e.g., using PulseAudio or ALSA configuration). | **RTP Handler, Bluetooth HFP** |

## **3\. Signal Flow Diagrams**

### **A. Outgoing Call Sequence (Dialing)**

1. **User lifts handset/dials:** Rotary Phone → HT801.  
2. **HT801:** Decodes pulse dialing → Sends SIP NOTIFY/INFO with the completed number string.  
3. **SIP Adapter:** Receives NOTIFY → Passes number to Call Manager.  
4. **Call Manager:** State transitions: DIALING → IN\_CALL → Triggers **Bluetooth HFP Manager** to initiate the call.

### **B. Incoming Call Sequence (Ringing)**

1. **Mobile Phone:** Receives incoming call → **Bluetooth HFP Manager** detects event.  
2. **Call Manager:** State transitions: IDLE → RINGING.  
3. **Call Manager:** Generates and sends a SIP INVITE to the HT801.  
4. **HT801:** Receives SIP INVITE → Applies high AC voltage→ **Rotary Phone rings.**  
5a. **If answered on rotary phone:**
   - **User answers:** Hook Switch → HT801 → Sends SIP 200 OK/ACK back to the Pi.  
   - **SIP Adapter:** Receives SIP 200 OK → Passes event to Call Manager.  
   - **Call Manager:** State transitions: RINGING → IN\_CALL. **RTP Audio Bridge** begins data transfer through rotary phone.
5b. **If answered on cell phone:**
   - **User answers:** Call accepted on mobile device → **Bluetooth HFP Manager** detects answer event.
   - **Call Manager:** State transitions: RINGING → IN\_CALL (on mobile).
   - **Audio Routing Requirement:** Audio automatically routes to cell phone without user intervention (the HFP implementation must ensure all audio is routed to the cell phone without any user action to select microphone/speaker).
