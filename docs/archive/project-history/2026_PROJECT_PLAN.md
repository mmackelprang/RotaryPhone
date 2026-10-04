# 2026 Project Plan: Windows NUC Migration & TypeScript UI

## 🚦 Project Status Dashboard

| Phase | Task | Status |
| :--- | :--- | :--- |
| **1. Migration** | **Prompt 1: Windows Bluetooth Investigation** | 🟢 Completed (Requires MSIX) |
| **2. Backend** | **Prompt 2: API & SignalR Skeleton** | 🟢 Completed |
| **3. Frontend** | **Prompt 3: Project Scaffold & Material UI** | 🟢 Completed |
| **3. Frontend** | **Prompt 4: Dashboard & Controls** | 🟢 Completed |
| **3. Frontend** | **Prompt 5: Contacts & History** | 🟢 Completed |
| **4. Integration** | **Prompt 6: Production Build & Deployment** | 🟢 Completed |

## Overview

✅ **MIGRATION COMPLETE**
The project has been successfully migrated to a Windows-compatible architecture with a new React/TypeScript frontend. 
The application can be built and run using `publish.ps1`.

### Build & Run
```powershell
./publish.ps1
cd publish
./RotaryPhoneController.WebUI.exe
```

This plan outlines the migration of the Rotary Phone Controller from a Linux/Raspberry Pi environment to a Windows NUC platform, and the modernization of the User Interface from Blazor to a TypeScript-based Single Page Application (SPA).

---

## 🛠️ Execution Guide: Prompts for Coding Agents

*Use the following prompts to execute this plan. Each prompt is self-contained and provides necessary context to prevent hallucination or scope drift.*

### 1. Platform Migration (Windows Bluetooth)

#### Prompt 1: Windows Bluetooth Investigation
> **Role**: Windows System Developer (.NET 9)
> **Objective**: Create a Proof of Concept (PoC) for Bluetooth Hands-Free Profile (HFP) on Windows.
> **Context**: We are migrating a Rotary Phone controller from Linux (BlueZ) to Windows 10/11. The current implementation uses `BlueZHfpAdapter.cs` which relies on D-Bus. We need a replacement `WindowsHfpAdapter.cs`.
> **Task**:
> 1. Investigate the `Windows.Devices.Bluetooth` and `Windows.Devices.Bluetooth.Rfcomm` APIs.
> 2. Determine if it is possible to act as a **Hands-Free Unit (HF)** (sink) that a mobile phone can pair with. *Note: Windows typically acts as the Audio Gateway (AG).*
> 3. Create a small console app PoC that:
>    - Advertises a service UUID for HFP.
>    - Accepts a pairing request from a mobile phone.
>    - Logs if an HFP connection is established.
> **Constraints**:
> - Use .NET 9.
> - Do not use 3rd party paid libraries.
> - If native Windows APIs block the "Hands-Free Unit" role, document this clearly as a blocker and propose a hardware alternative (e.g., external Bluetooth module via Serial).
> **Output**: A report on feasibility and a `BluetoothPoC` console project if successful.

### 2. Backend Refactoring (API & SignalR)

#### Prompt 2: API & SignalR Skeleton
> **Role**: Backend Developer (.NET 9)
> **Objective**: Refactor the existing Blazor Server project (`RotaryPhoneController.WebUI`) into a Hybrid Web API + SignalR host.
> **Context**: The current project uses Blazor Server for UI. We are moving to a React SPA. The backend needs to serve data via REST and real-time state via SignalR.
> **Task**:
> 1. Modify `Program.cs` to add `AddControllers()` and `AddSignalR()`.
> 2. Create `RotaryHub.cs` inheriting from `Hub`. Define methods: `SendCallState(CallState state)`, `SendIncomingCall()`.
> 3. Create `PhoneController.cs`:
>    - `GET /api/status`: Returns current state of the phone.
>    - `POST /api/dial`: (Dev testing) Simulates dialing a number.
> 4. Create `ContactsController.cs`:
>    - CRUD endpoints matching the existing `IContactService`.
> 5. **Crucial**: Ensure existing `CallManager` logic remains untouched but injects `IHubContext<RotaryHub>` to broadcast events when state changes.
> **Constraints**:
> - Keep `RotaryPhoneController.Core` mostly unchanged.
> - Remove `_Imports.razor` and Blazor pages *only after* the API is verified.
> - Enable CORS for `localhost:5173` (Vite default).

### 3. Frontend Development (React + TypeScript)

#### Prompt 3: Project Scaffold & Material UI Setup
> **Role**: Frontend Developer (React/TypeScript)
> **Objective**: Initialize the new client application.
> **Context**: We are replacing a Blazor interface with a modern React SPA.
> **Task**:
> 1. Create a new directory `src/RotaryPhoneController.Client`.
> 2. Initialize a Vite app: `npm create vite@latest . -- --template react-ts`.
> 3. Install core dependencies:
>    - `@mui/material @emotion/react @emotion/styled` (UI Framework)
>    - `@microsoft/signalr` (Real-time comms)
>    - `zustand` (State management)
>    - `axios` (HTTP requests)
> 4. Set up a basic Layout component with a dark-mode theme (matching the "Cyberpunk/Hacker" aesthetic of the original CLI or a clean modern look).
> 5. Create a `SignalRService.ts` utility to handle connection/reconnection to the .NET backend.
> **Output**: A runnable Vite project that connects to the SignalR hub and logs "Connected".

#### Prompt 4: Dashboard & Controls
> **Role**: Frontend Developer
> **Objective**: Implement the main phone dashboard.
> **Context**: The user needs to see the phone's status and simulated controls.
> **Task**:
> 1. Create a `Dashboard.tsx` component.
> 2. **Status Display**: Show a large, visible indicator of the current state (IDLE, RINGING, INCALL, DIALING).
> 3. **Mock Controls** (Dev Mode):
>    - Buttons to trigger API endpoints: "Simulate Incoming Call", "Lift Handset", "Drop Handset".
>    - A text input and button for "Simulate Dial Digits".
> 4. **Integration**: Use `SignalRService` to listen for `CallStateChanged` events and update the Zustand store.
> **Constraints**:
> - Use Material UI components (Cards, Buttons, Chips).
> - Ensure the UI is responsive (mobile-friendly).

#### Prompt 5: Contacts & History
> **Role**: Frontend Developer
> **Objective**: Implement data management pages.
> **Context**: Users need to manage an address book and view call logs.
> **Task**:
> 1. **Contacts**:
>    - Create `Contacts.tsx` with a DataGrid showing Name and Number.
>    - Implement an "Add Contact" Dialog form.
>    - Wire up to `GET/POST/DELETE /api/contacts`.
> 2. **History**:
>    - Create `CallHistory.tsx`.
>    - Display a list of past calls (Time, Duration, Direction, Name).
>    - Wire up to `GET /api/history`.
> **Constraints**:
> - Handle loading states (skeletons/spinners).
> - Handle empty states gracefully.

### 4. Integration & Polish

#### Prompt 6: Production Build & Deployment
> **Role**: DevOps / Full Stack Developer
> **Objective**: Merge the frontend and backend into a single deployable unit.
> **Context**: We want to run a single .NET executable on the NUC that serves the React frontend.
> **Task**:
> 1. Configure the Vite build output to go to `../RotaryPhoneController.WebUI/wwwroot`.
> 2. Update `RotaryPhoneController.WebUI/Program.cs` to:
>    - Use `app.UseStaticFiles()`.
>    - Map fallback to `index.html` (SPA routing pattern).
> 3. Create a `publish.ps1` script that:
>    - Runs `npm install && npm run build` in the client folder.
>    - Runs `dotnet publish` in the WebUI folder.
> **Verification**: Running the published .NET app should launch the web server, and navigating to `localhost:5000` should load the React app.

---

## Testing & Verification Steps

### 1. Audio/SIP Verification (Windows)
- [ ] **SIP Test**: Run the app on the NUC. Configure HT801 to point to NUC IP.
    - Lift handset → Verify logs show "OFF-HOOK".
    - Dial number → Verify logs show "Digits Received".
- [ ] **RTP Test**: Establish a call.
    - Speak into handset → Verify audio is received by app (visualize or record).
    - Play audio from app → Verify sound in handset earpiece.

### 2. Bluetooth Verification
- [ ] **Pairing**: Can the NUC pair with a mobile phone?
- [ ] **HFP Profile**: Does the NUC appear as a "Headset" or "Hands-Free" device to the phone?
- [ ] **Audio Routing**:
    - Incoming call → Answer on NUC → Audio flows to HFP?
    - Outgoing call → Audio flows from HFP?

### 3. UI Verification
- [ ] **Real-time Sync**: Open UI in two browser tabs. Change state in one (or via hardware). Verify both tabs update instantly.
- [ ] **API Stability**: Ensure all CRUD operations on Contacts/History persist to disk (`json` files).