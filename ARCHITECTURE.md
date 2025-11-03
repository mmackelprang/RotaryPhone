# **Rotary Phone Bluetooth Adapter Architecture**

## **1\. High-Level Block Diagram**

The system is composed of three primary functional blocks interacting to bridge the electrical signals of the vintage phone with the digital protocols of modern connectivity. .

| Block | Function | Interface Type |
| :---- | :---- | :---- |
| **Rotary Phone** | Input/Output Device | Analog Electrical Signals |
| **Adapter Board** | Signal Conditioning, Power Conversion, Audio I/O | GPIO (Digital), Analog Audio, High-Voltage AC |
| **Raspberry Pi** | Microcontroller, State Machine, Protocol Stack | Bluetooth HFP/Audio (Digital) |

## **2\. Component and Interface Detail**

### **2.1 Rotary Telephone Components**

* **Rotary Dial:** Produces momentary breaks (pulses) in the line circuit.  
  * *Output Signal:* Line Pulses (Contact open/close).  
  * *Interface to Adapter:* Simple two-wire connection to the pulse detection circuit.  
* **Hook Switch:** Detects if the handset is lifted (off-hook) or resting (on-hook).  
  * *Output Signal:* Simple contact switch (open/closed).  
  * *Interface to Adapter:* Two-wire connection to the hook switch detection circuit.  
* **Handset Audio:** Carbon microphone and speaker.  
  * *Interface to Adapter:* Low-level, high-impedance (mic) and low-impedance (speaker) audio signals.  
* **Ringer Bell:** An electromagnet coil designed for high AC voltage.  
  * *Input Signal:* 70-90V AC @ \~20 Hz.  
  * *Interface to Adapter:* Two-wire connection to the Ring Generation Circuit.

### **2.2 Adapter/Interface Board (The Bridge)**

This custom circuit board handles all necessary signal conversion and power supply.

| Sub-Component | Function | Pi Interface |
| :---- | :---- | :---- |
| **Pulse & Hook Detection** | Uses **Optocouplers** or **Transistor Switching** to isolate high-voltage signals and convert them into clean 3.3V/0V logic signals. Includes hardware filtering/pull-up resistors. | **GPIO Input Pins** (Digital) |
| **Audio Interface** | Microphone **Bias** and **Preamplifier** stage; Speaker **Amplifier** stage. May include a simple **Hybrid circuit** for 2-wire to 4-wire conversion. | **Pi Audio Interface** (via DAC/ADC of a HAT or USB Sound Card) |
| **Ring Generation Circuit** | Converts low-voltage DC (e.g., 12V) into high-voltage AC (e.g., 85V @ 20Hz). **Isolation Transformer** required. Controlled by a solid-state relay (SSR) or mechanical relay. | **GPIO Output Pin** (Digital control for the Relay/SSR) |
| **Power Management** | Provides regulated power for all adapter components and the Raspberry Pi. | Shared Power Rails |

### **2.3 Raspberry Pi Zero 2 (Microcontroller & Software)**

The Pi acts as the central processor and protocol converter.

| Software Layer | Function | Interactions |
| :---- | :---- | :---- |
| **GPIO Handler** | Reads digital inputs (Hook, Pulses) and controls the Ring Output. Uses RPi.GPIO and interrupt handlers. | **Adapter Board** |
| **Dial Decode State Machine** | Monitors pulse GPIO to count pulses, implement debouncing, and apply digit timing logic (e.g., timeout after last pulse to finalize digit/number). | **GPIO Handler, Call Manager** |
| **Bluetooth HFP Stack** | Manages pairing, connection, and the Hands-Free Profile (HFP) for initiating and receiving calls. | **Mobile Phone** |
| **ALSA/PulseAudio Manager** | Routes audio data between the HFP stream and the Pi's physical audio I/O hardware (HAT/USB). | **Adapter Audio Interface, Bluetooth Stack** |
| **Call Manager (Application Logic)** | The central application state machine (Idle, Dialing, Ringing, In-Call). Triggers ring control, initiates calls based on decoded digits, and manages hang-up. | **All layers** |

## **3\. Signal Flow Diagrams**

### **A. Outgoing Call Sequence (Dialing)**

1. **User lifts handset:** Hook Switch **CLOSED**.  
2. **Adapter/GPIO:** Hook Switch GPIO changes state.  
3. **Call Manager:** State transitions: IDLE \-\> DIALING. (Optional: Play dial tone).  
4. **User dials a digit:** Rotary dial pulses the line (e.g., 5 pulses for '5').  
5. **Adapter/GPIO:** Pulse GPIO receives signal interrupts.  
6. **Dial Decoder:** Counts interrupts, applies timing logic, decodes digit ('5'). Stores number.  
7. **Call Manager:** After a final inter-digit timeout, the full number is ready. Initiates call via **Bluetooth HFP Stack**.

### **B. Incoming Call Sequence (Ringing)**

1. **Mobile Phone:** Receives an incoming call.  
2. **Bluetooth HFP Stack:** Notifies the Pi of an incoming call event.  
3. **Call Manager:** State transitions: IDLE \-\> RINGING.  
4. **Call Manager/GPIO:** Activates the Ring Control GPIO Output.  
5. **Adapter/Ringing Circuit:** Ring Generator activates, supplying high AC voltage to the Ringer Bell. The phone rings.  
6. **User lifts handset:** Hook Switch **CLOSED**.  
7. **Call Manager:** State transitions: RINGING \-\> IN\_CALL. Deactivates Ring Control GPIO.  
8. **ALSA/Audio Manager:** Starts routing bidirectional audio.