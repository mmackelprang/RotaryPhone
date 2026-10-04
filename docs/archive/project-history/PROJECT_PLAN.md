# **Project Plan: Rotary Phone to Raspberry Pi Audio Interface**

> **⚠️ DEPRECATED: 2026 UPDATE**
> This plan was for the original Raspberry Pi implementation.
> Please refer to **`2026_PROJECT_PLAN.md`** for the current Windows NUC & TypeScript UI roadmap.

## **Project Goal**

To adapt a legacy rotary telephone into a functional audio and dialing interface for a modern mobile phone via a Raspberry Pi and Bluetooth connection.

## **Phase 1: Research, Sourcing, and Schematic Finalization (4-6 Weeks)**

### **1.1 Rotary Telephone Analysis**

* **Acquire Hardware:** Source a functional rotary telephone.  
* **Internal Wiring Documentation:**  
  * Identify and map all internal components (Rotary dial contacts, hook switch contacts, carbon microphone, speaker/earpiece, ringer bell coils).  
  * Document the electrical characteristics (e.g., ringer coil resistance, speaker/mic impedance).  
* **Signal Requirements:** Re-confirm the required ringing voltage (typically 70-90V AC at \~20 Hz) and the pulse characteristics.

### **1.2 Adapter Circuitry Research & Decision**

* **Search for Existing Solutions (Primary):** Conduct a thorough search for existing open-source projects (e.g., "Rotary Phone Pi Adapter," "VOIP Rotary Phone") that provide schematics for:  
  * Dial Pulse Detection (Optocoupler/Transistor-based)  
  * Hook Switch Detection  
  * Ringing Voltage Generator  
  * Audio Hybrid/Interface (especially for the low-impedance carbon mic/speaker).  
* **Decision Point:**  
  * **IF** viable, open-source schematics/PCBs are found: **Adopt** and move to **1.3**.  
  * **ELSE** (Requires custom design): **Add Task 1.2.1.**  
* **Task 1.2.1: Custom Schematic Design (If needed):**  
  * Design signal conditioning circuits for pulse and hook switch interfaces (using optocouplers for isolation).  
  * Design a simple amplifier/microphone bias circuit for the handset.  
  * Select or design a ring voltage generator solution (e.g., low-voltage DC-AC inverter design using a step-up transformer and oscillator/H-bridge).  
  * Select appropriate audio codec HAT or USB sound card for the Raspberry Pi Zero 2\.

### **1.3 Component Sourcing and Procurement**

* Order all electronic components (Resistors, Capacitors, Optocouplers, Transistors, Relays/Solid-State Switches for ringing, Audio Codec, Amplifier chips, Step-up transformer for ringing).  
* Purchase Raspberry Pi Zero 2 and compatible power supply.  
* Source modular connectors (RJ11 or compatible) and enclosure materials.

## **Phase 2: Hardware Development and Prototyping (4-6 Weeks)**

### **2.1 Low-Voltage Interface Prototyping**

* **Pulse & Hook Detection:** Build the pulse detection and hook switch interface circuits on a breadboard.  
* **Verification:** Use a multimeter and oscilloscope (if available) to verify that dial pulses and hook state changes reliably trigger the appropriate 3.3V/0V signals for the Pi's GPIO pins.

### **2.2 Audio Interface Prototyping**

* **Handset Connection:** Build the microphone bias and speaker amplifier circuit.  
* **Testing:** Route audio through the Pi's chosen sound interface (HAT/USB) to the breadboard circuit. Test microphone input and speaker output quality. Adjust amplification and bias as necessary.

### **2.3 Ringing Circuit Prototyping (Highest Risk/Complexity)**

* **Generator Build:** Assemble the ring voltage generation circuit (oscillator/transformer).  
* **Safety First:** **Crucially**, implement and test the isolation and control stage (relay/SSR) *before* connecting it to the phone.  
* **Verification:** Test the circuit to ensure it reliably produces 70-90V AC at \~20 Hz when triggered by a low-voltage GPIO signal.  
* **Ringer Test:** Connect the high-voltage output safely to the rotary phone ringer coil and verify the bell rings correctly on command.

## **Phase 3: Software Implementation (4-5 Weeks)**

### **3.1 Base Operating System and Hardware Setup**

* Install Raspberry Pi OS on the Pi Zero 2\.  
* Configure necessary services (SSH, Bluetooth stack).

### **3.2 GPIO and State Machine Development**

* **Hook State Monitor:** Implement interrupt-driven GPIO monitoring for the hook switch, triggering state changes in the main application (e.g., IDLE \-\> OFF\_HOOK).  
* **Pulse Decoder:** Develop the rotary dial pulse counting and decoding logic, including software debouncing and timing to correctly segment individual digits (e.g., detecting a long pause between digits).  
* **Ring Control Logic:** Implement the function to toggle the GPIO pin connected to the ringing circuit relay.

### **3.3 Bluetooth Audio and Bridging (The modern interface)**

* **Bluetooth Pairing:** Write a script or configure services to pair the Pi with a mobile phone and establish an HFP (Hands-Free Profile) connection for call audio.  
* **Audio Routing:** Integrate ALSA/PulseAudio to route the HFP audio stream:  
  * **Incoming:** HFP Stream \-\> Pi Audio Out (DAC) \-\> Adapter \-\> Handset Speaker.  
  * **Outgoing:** Handset Mic \-\> Adapter \-\> Pi Audio In (ADC) \-\> HFP Stream.
* **Audio Routing Requirement:** The HFP implementation must intelligently route audio based on where the call is answered:
  * **If call answered on rotary phone (handset lifted):** Audio routes through the rotary phone (microphone and speaker).
  * **If call answered on cell phone device:** All audio must automatically route to the cell phone without any user intervention to select the cell phone as the microphone/speaker.

### **3.4 Application Layer and Call Flow**

* **State Machine Logic:** Implement the full call flow:  
  1. **Incoming Call:** Bluetooth event triggers RINGING state \-\> Activate Ring GPIO.  
  2. **Answering:** Hook switch goes OFF\_HOOK during RINGING \-\> Deactivate Ring GPIO, start audio stream through rotary phone.  
  3. **Answering on Cell Phone:** Call answered on mobile device during RINGING \-\> Audio automatically routes to cell phone without user intervention.
  4. **Dialing:** Hook switch goes OFF\_HOOK during IDLE \-\> Enter DIALING state, start dial tone (optional). Decode digits. Once a full number is dialed (via timeout), initiate Bluetooth call command.  
  5. **Hanging Up:** Hook switch goes ON\_HOOK \-\> Terminate Bluetooth call, return to IDLE.

## **Phase 4: Integration, Testing, and Troubleshooting (3 Weeks)**

* **Dialing Accuracy:** Rigorously test the pulse decoder with all digits (1 through 0\) to ensure 100% accuracy, especially at different dialing speeds.  
* **Call Quality:** Conduct multiple test calls to assess microphone gain, speaker volume, and overall audio clarity. Tune amplifier/software gains.  
* **Ringing Reliability:** Test ring activation and deactivation, ensuring the high-voltage circuit is stable and does not cause interference.  
* **Stress Testing:** Test long periods of on-hook and off-hook states.

## **Phase 5: Finalization and Documentation (1-2 Weeks)**

* **Enclosure:** Integrate the adapter board (PCB or perfboard) into a compact, safe enclosure.  
* **Final Wiring:** Assemble the final wiring harness for permanent connection between the phone and the adapter board.  
* **User Guide:** Document the operation, Bluetooth pairing procedure, and troubleshooting steps.  
* **Code Cleanup:** Finalize and comment all software, and prepare for version control/Git repository upload.
