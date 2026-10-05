# PBAP CallerResolved — RotaryPhone Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `ReportCallerResolved` hub method so Radio.API can send resolved caller names back to RotaryPhone for UI display and call logging.

**Architecture:** RotaryPhone's existing SignalR call hub gains a new server method that Radio.API (as a client) invokes. The resolved name flows into CallManager state, CallHistoryEntry, the Zustand store, and the Dashboard UI.

**Tech Stack:** ASP.NET Core SignalR, React/TypeScript, Zustand

**Spec:** `docs/superpowers/specs/2026-03-12-pbap-contact-sync-design.md`

**Branch:** `feature/pbap-contact-sync` (already created)

**Parallel with:** RTest repo PBAP implementation — can be built and tested independently. End-to-end testing requires both deployed.

---

## Chunk 1: Backend — CallerName Plumbing

### Task 1: Add CallerName to CallHistoryEntry

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallHistory/CallHistoryEntry.cs`

- [ ] **Step 1: Add CallerName property**

In `CallHistoryEntry.cs`, add after the `PhoneNumber` property (around line 53):

```csharp
/// <summary>
/// Resolved caller name from PBAP contact lookup (null if unresolved)
/// </summary>
public string? CallerName { get; set; }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.Core/RotaryPhoneController.Core.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/CallHistory/CallHistoryEntry.cs
git commit -m "feat: add CallerName property to CallHistoryEntry"
```

---

### Task 2: Add CallerName tracking to CallManager

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallManager.cs`

- [ ] **Step 1: Add a public method to receive resolved caller name**

Add this method to `CallManager` (after `HandleBluetoothIncomingCall`):

```csharp
/// <summary>
/// Called by Radio.API (via SignalR) when it resolves a caller's name from PBAP contacts.
/// </summary>
public void SetResolvedCallerName(string phoneNumber, string displayName)
{
    if (_currentCallHistory != null && _currentCallHistory.PhoneNumber == phoneNumber)
    {
        _currentCallHistory.CallerName = displayName;
        _logger.LogInformation("Caller resolved: {PhoneNumber} → {DisplayName}", phoneNumber, displayName);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.Core/RotaryPhoneController.Core.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Core/CallManager.cs
git commit -m "feat: add SetResolvedCallerName to CallManager"
```

---

### Task 3: Add ReportCallerResolved hub method

**Files:**
- Modify: `src/RotaryPhoneController.Server/Hubs/RotaryHub.cs`

- [ ] **Step 1: Add constructor injection and the hub method**

The existing `RotaryHub` has no constructor — add one. `PhoneManagerService` is registered as a singleton in `Program.cs` (line 89), so DI will resolve it.

Replace the entire `RotaryHub` class with:

```csharp
public class RotaryHub : Hub
{
    private readonly PhoneManagerService _phoneManager;

    public RotaryHub(PhoneManagerService phoneManager)
    {
        _phoneManager = phoneManager;
    }

    // ... keep all existing methods (SendCallState, SendIncomingCall, etc.) unchanged ...

    /// <summary>
    /// Called by Radio.API to report a resolved caller name from PBAP contacts.
    /// Updates CallManager state and broadcasts to all connected UI clients.
    /// </summary>
    public async Task ReportCallerResolved(string phoneNumber, string displayName)
    {
        // Update CallManager state for all phones
        // GetAllPhones() returns IEnumerable<(string PhoneId, CallManager CallManager)>
        foreach (var phone in _phoneManager.GetAllPhones())
        {
            phone.CallManager.SetResolvedCallerName(phoneNumber, displayName);
        }

        // Broadcast to UI clients
        await Clients.All.SendAsync("CallerResolved", phoneNumber, displayName);
    }
}
```

- [ ] **Step 2: Build the server project**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Server/Hubs/RotaryHub.cs
git commit -m "feat: add ReportCallerResolved hub method"
```

---

### Task 4: Add CallerName to existing tests

**Files:**
- Modify: `src/RotaryPhoneController.Tests/CallManagerTests.cs`

- [ ] **Step 1: Add test for SetResolvedCallerName**

Note: `_currentCallHistory` is private. To verify `CallerName` was set, capture the `CallHistoryEntry` via the mock call history service. Check how `ICallHistoryService` is mocked in the existing test setup and adapt accordingly.

```csharp
[Fact]
public void SetResolvedCallerName_WhenRinging_ShouldUpdateCallHistory()
{
    // Arrange - simulate incoming call (triggers HandleBluetoothIncomingCall)
    _mockBluetoothAdapter.Raise(x => x.OnIncomingCall += null, "5551234567");

    // Act
    _callManager.SetResolvedCallerName("5551234567", "John Smith");

    // Assert - when call ends, the saved CallHistoryEntry should have CallerName
    // Trigger call end to flush call history
    _callManager.HangUp();

    // Verify the call history entry was saved with the resolved name
    _mockCallHistory.Verify(x => x.AddCallHistory(
        It.Is<CallHistoryEntry>(e => e.CallerName == "John Smith" && e.PhoneNumber == "5551234567")),
        Times.Once);
}

[Fact]
public void SetResolvedCallerName_WrongNumber_ShouldNotUpdate()
{
    // Arrange
    _mockBluetoothAdapter.Raise(x => x.OnIncomingCall += null, "5551234567");

    // Act - different number should be ignored
    _callManager.SetResolvedCallerName("9999999999", "Wrong Person");

    // Assert - end call and verify CallerName is NOT set
    _callManager.HangUp();

    _mockCallHistory.Verify(x => x.AddCallHistory(
        It.Is<CallHistoryEntry>(e => e.CallerName == null && e.PhoneNumber == "5551234567")),
        Times.Once);
}
```

Adapt the mock setup and event-raising pattern to match the existing test fixtures — the key point is that the assert must verify `CallerName` on the persisted `CallHistoryEntry`, not just check for no exception.

- [ ] **Step 2: Run tests**

Run: `dotnet test src/RotaryPhoneController.Tests/RotaryPhoneController.Tests.csproj --verbosity normal`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Tests/CallManagerTests.cs
git commit -m "test: add SetResolvedCallerName tests"
```

---

## Chunk 2: Frontend — Display Caller Name

### Task 5: Add callerName to Zustand store

**Files:**
- Modify: `src/RotaryPhoneController.Client/src/store/useStore.ts`

- [ ] **Step 1: Add callerName to PhoneState interface and store**

In the `PhoneState` interface (around line 25), add:
```typescript
callerName: string | null;
```

In the store initial state, add:
```typescript
callerName: null,
```

Add setter:
```typescript
setCallerName: (name: string | null) => set({ callerName: name }),
```

Also update `setIncomingNumber` to clear callerName when a new incoming number is set:
```typescript
setIncomingNumber: (number: string | null) => set({ incomingNumber: number, callerName: null }),
```

And clear callerName in `setCallState` when transitioning to Idle:
```typescript
setCallState: (state: CallState) => set({
  callState: state,
  ...(state === CallState.Idle ? { callerName: null, incomingNumber: null } : {})
}),
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Client/src/store/useStore.ts
git commit -m "feat: add callerName to store"
```

---

### Task 6: Handle CallerResolved SignalR event

**Files:**
- Modify: `src/RotaryPhoneController.Client/src/services/SignalRService.ts`

- [ ] **Step 1: Add CallerResolved handler**

After the existing `IncomingCall` handler (around line 29), add:

```typescript
this.connection.on('CallerResolved', (phoneNumber: string, displayName: string) => {
  console.log(`SignalR: Caller resolved ${phoneNumber} → ${displayName}`);
  useStore.getState().setCallerName(displayName);
});
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Client/src/services/SignalRService.ts
git commit -m "feat: handle CallerResolved SignalR event in client"
```

---

### Task 7: Display caller name in Dashboard

**Files:**
- Modify: `src/RotaryPhoneController.Client/src/pages/Dashboard.tsx`

- [ ] **Step 1: Read the current Dashboard.tsx to understand the layout**

Find where `incomingNumber` is displayed during Ringing state. Update to show the caller name alongside or instead of the raw number.

Example pattern (adapt to actual JSX structure):
```typescript
const callerName = useStore(state => state.callerName);

// In the Ringing display section:
// Before: "Incoming Call: 555-1234"
// After:  "Incoming Call: John Smith (555-1234)" or "Incoming Call: 555-1234" if no name
```

- [ ] **Step 2: Build frontend**

Run: `cd src/RotaryPhoneController.Client && npm run build`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Client/src/pages/Dashboard.tsx
git commit -m "feat: display resolved caller name in Dashboard"
```

---

### Task 8: Update Dashboard tests

**Files:**
- Modify: `src/RotaryPhoneController.Client/src/__tests__/Dashboard.test.tsx`

- [ ] **Step 1: Add test for caller name display**

```typescript
it('renders resolved caller name with incoming number', () => {
  useStore.setState({
    callState: CallState.Ringing,
    incomingNumber: '555-1234',
    callerName: 'John Smith'
  })

  render(<Dashboard />)

  expect(screen.getByText(/John Smith/)).toBeInTheDocument()
  expect(screen.getByText(/555-1234/)).toBeInTheDocument()
})
```

- [ ] **Step 2: Update store test if needed**

If `src/RotaryPhoneController.Client/src/__tests__/store.test.ts` exists with store state tests, add callerName coverage.

- [ ] **Step 3: Run frontend tests**

Run: `cd src/RotaryPhoneController.Client && npx vitest run`
Expected: All tests pass

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Client/src/__tests__/Dashboard.test.tsx
git commit -m "test: add caller name display tests"
```

---

### Task 9: Final build verification

- [ ] **Step 1: Run all .NET tests**

Run: `dotnet test src/RotaryPhoneController.Tests/RotaryPhoneController.Tests.csproj --verbosity normal`
Expected: All tests pass

- [ ] **Step 2: Run all frontend tests**

Run: `cd src/RotaryPhoneController.Client && npx vitest run`
Expected: All tests pass

- [ ] **Step 3: Full build**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Push branch**

```bash
git push origin feature/pbap-contact-sync
```

---

## Deployment Note

This branch is ready to deploy independently — the new hub method is additive and won't break existing functionality. However, `CallerResolved` events will only arrive once Radio.API's PBAP implementation is also deployed. Coordinated deployment is managed from the RTest repo (see `d:/prj/RTest/Rtest/docs/superpowers/plans/2026-03-12-pbap-radio-api.md`).
