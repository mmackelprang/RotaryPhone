# Google Voice Trunk Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a parallel SIP trunk call path via Google Voice / VoIP.ms, enabling the rotary phone to make and receive calls without a paired mobile phone, with a dashboard for call history and SMS notifications.

**Architecture:** New Razor Class Library (RCL) project `RotaryPhoneController.GVTrunk` that implements `ITrunkAdapter` (extends `ISipAdapter`) using SIPSorcery for SIP registration and call handling with VoIP.ms. Gmail API polls for GV SMS/missed-call notifications. SQLite stores call history. Blazor components for RTest, REST API + SignalR hub for RotaryPhone React client. Both hosts integrate via `AddGVTrunk()` / `MapGVTrunk()` extension methods.

**Tech Stack:** .NET 10, SIPSorcery 8.0.23, Google.Apis.Gmail.v1, Microsoft.Data.Sqlite, xUnit + Moq, React 19 + SignalR, Blazor Server (Razor components)

**Spec:** `docs/prompts/google-voice-trunk.md`

---

## File Structure

### New Project: `src/RotaryPhoneController.GVTrunk/`

| File | Responsibility |
|------|---------------|
| `RotaryPhoneController.GVTrunk.csproj` | RCL project (Sdk="Microsoft.NET.Sdk.Razor"), references Core |
| `Models/TrunkConfig.cs` | Config POCO bound from appsettings `"GVTrunk"` section |
| `Models/CallLogEntry.cs` | Call record model for SQLite |
| `Models/SmsNotification.cs` | SMS/missed-call notification record + SmsType enum |
| `Interfaces/ITrunkAdapter.cs` | Extends ISipAdapter with registration state + methods |
| `Interfaces/ISmsProvider.cs` | Abstraction for Gmail/other SMS sources |
| `Interfaces/ICallLogService.cs` | Abstraction for call history persistence |
| `Adapters/GVTrunkAdapter.cs` | ITrunkAdapter impl — SIP registration, inbound/outbound calls, RTP bridge |
| `Services/TrunkRegistrationService.cs` | IHostedService — periodic SIP re-registration |
| `Services/GmailSmsService.cs` | ISmsProvider impl — polls Gmail for GV notifications |
| `Services/CallLogService.cs` | ICallLogService impl — SQLite call history |
| `Api/GVTrunkController.cs` | REST endpoints for React client |
| `Api/GVTrunkHub.cs` | SignalR hub for real-time React push |
| `Components/_Imports.razor` | Blazor component imports (namespaces) |
| `Components/GVTrunkDashboard.razor` | Top-level Blazor component for RTest |
| `Components/TrunkStatusPanel.razor` | Registration badge, call state, re-register button |
| `Components/CallHistoryTable.razor` | Last 50 call log entries |
| `Components/SmsNotificationsPanel.razor` | Inbound SMS / missed call feed |
| `Components/OutboundDialPanel.razor` | E.164 dial input + button |
| `Extensions/GVTrunkServiceExtensions.cs` | AddGVTrunk() / MapGVTrunk() DI helpers |

### New Test Project: `src/RotaryPhoneController.GVTrunk.Tests/`

| File | Responsibility |
|------|---------------|
| `RotaryPhoneController.GVTrunk.Tests.csproj` | xUnit + Moq test project |
| `GVTrunkAdapterTests.cs` | SIP registration, inbound/outbound call tests |
| `CallLogServiceTests.cs` | SQLite round-trip tests (in-memory) |
| `GmailSmsServiceTests.cs` | Gmail parsing tests |

### Modified Files

| File | Changes |
|------|---------|
| `RotaryPhone.sln` | Add GVTrunk + GVTrunk.Tests projects |
| `src/RotaryPhoneController.Server/Program.cs` | Add `builder.Services.AddGVTrunk(...)` + `app.MapGVTrunk()` |
| `src/RotaryPhoneController.Server/appsettings.json` | Add `"GVTrunk"` config section |
| `src/RotaryPhoneController.Client/src/App.tsx` | Add `/gvtrunk` route |
| `src/RotaryPhoneController.Client/src/components/Layout.tsx` | Add GV Trunk nav link |

### New React Files

| File | Responsibility |
|------|---------------|
| `src/RotaryPhoneController.Client/src/hooks/useGVTrunk.ts` | SignalR + REST hook |
| `src/RotaryPhoneController.Client/src/pages/GVTrunk.tsx` | Dashboard page with 4 panels |

---

## Chunk 1: Project Scaffolding + Models + Interfaces

### Task 1: Create the GVTrunk RCL project

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/RotaryPhoneController.GVTrunk.csproj`

- [ ] **Step 1: Create project via dotnet CLI**

```bash
cd src
dotnet new razorclasslib -n RotaryPhoneController.GVTrunk -f net10.0
```

- [ ] **Step 2: Update .csproj for dual TFM and dependencies**

Replace the generated csproj content with:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <!-- Both TFMs on Windows (for cross-compile to Linux); single net10.0 on Linux -->
    <TargetFrameworks Condition="'$(OS)' == 'Windows_NT'">net10.0;net10.0-windows10.0.19041.0</TargetFrameworks>
    <TargetFrameworks Condition="'$(OS)' != 'Windows_NT'">net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- Define WINDOWS constant for Windows TFM -->
  <PropertyGroup Condition="$(TargetFramework.Contains('windows'))">
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </PropertyGroup>

  <!-- Platform-specific Core project reference, keyed off TFM not host OS -->
  <ItemGroup Condition="$(TargetFramework.Contains('windows'))">
    <ProjectReference Include="..\RotaryPhoneController.Core\RotaryPhoneController.Core.csproj"
                      SetTargetFramework="TargetFramework=net10.0-windows10.0.19041.0" />
  </ItemGroup>

  <ItemGroup Condition="!$(TargetFramework.Contains('windows'))">
    <ProjectReference Include="..\RotaryPhoneController.Core\RotaryPhoneController.Core.csproj"
                      SetTargetFramework="TargetFramework=net10.0" />
  </ItemGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="SIPSorcery" Version="8.0.23" />
    <PackageReference Include="Google.Apis.Gmail.v1" Version="1.68.*" />
    <PackageReference Include="Google.Apis.Auth" Version="1.68.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
    <PackageReference Include="Serilog" Version="4.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.*" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add to solution and add Server reference**

```bash
cd D:/prj/RotaryPhone
dotnet sln add src/RotaryPhoneController.GVTrunk/RotaryPhoneController.GVTrunk.csproj
dotnet add src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj reference src/RotaryPhoneController.GVTrunk/RotaryPhoneController.GVTrunk.csproj
```

- [ ] **Step 4: Remove generated template files**

Delete any auto-generated files from the razorclasslib template (Component1.razor, wwwroot/, etc.) that we don't need.

- [ ] **Step 5: Build to verify project compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/RotaryPhoneController.GVTrunk.csproj RotaryPhone.sln
git add src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj
git commit -m "feat: scaffold RotaryPhoneController.GVTrunk RCL project"
```

---

### Task 2: Create models

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Models/TrunkConfig.cs`
- Create: `src/RotaryPhoneController.GVTrunk/Models/CallLogEntry.cs`
- Create: `src/RotaryPhoneController.GVTrunk/Models/SmsNotification.cs`

- [ ] **Step 1: Create TrunkConfig**

```csharp
namespace RotaryPhoneController.GVTrunk.Models;

public class TrunkConfig
{
    public string SipServer { get; set; } = "sip.voip.ms";
    public int SipPort { get; set; } = 5060;
    public string SipUsername { get; set; } = "";
    public string SipPassword { get; set; } = "";
    public int LocalSipPort { get; set; } = 5061;
    public string LocalIp { get; set; } = "0.0.0.0";
    public string GoogleVoiceForwardingNumber { get; set; } = "";
    public string OutboundCallerId { get; set; } = "";
    public int RegisterIntervalSeconds { get; set; } = 60;
    public int GmailPollIntervalSeconds { get; set; } = 30;
    public string GmailCredentialsPath { get; set; } = "";
    public string CallLogDbPath { get; set; } = "calllog.db";
}
```

- [ ] **Step 2: Create CallLogEntry**

```csharp
namespace RotaryPhoneController.GVTrunk.Models;

public record CallLogEntry(
    int Id,
    DateTime StartedAt,
    DateTime? EndedAt,
    string Direction,       // "Inbound" | "Outbound"
    string RemoteNumber,
    string Status,          // "Answered" | "Missed" | "Rejected"
    int? DurationSeconds,
    string? Notes = null    // Reserved for IVR transcripts (Phase 2)
);
```

- [ ] **Step 3: Create SmsNotification**

```csharp
namespace RotaryPhoneController.GVTrunk.Models;

public record SmsNotification(
    string FromNumber,
    string? Body,
    DateTime ReceivedAt,
    SmsType Type
);

public enum SmsType { Sms, MissedCall }
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Models/
git commit -m "feat: add GVTrunk models — TrunkConfig, CallLogEntry, SmsNotification"
```

---

### Task 3: Create interfaces

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Interfaces/ITrunkAdapter.cs`
- Create: `src/RotaryPhoneController.GVTrunk/Interfaces/ISmsProvider.cs`
- Create: `src/RotaryPhoneController.GVTrunk/Interfaces/ICallLogService.cs`

- [ ] **Step 1: Create ITrunkAdapter**

```csharp
using RotaryPhoneController.Core;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ITrunkAdapter : ISipAdapter
{
    bool IsRegistered { get; }
    event Action<bool> OnRegistrationChanged;
    event Action<string>? OnDtmfReceived;
    void StartListening();
    Task RegisterAsync(CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);
    Task<string> PlaceOutboundCallAsync(string e164Number);
}
```

- [ ] **Step 2: Create ISmsProvider**

```csharp
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ISmsProvider
{
    event Action<SmsNotification> OnSmsReceived;
    event Action<SmsNotification> OnMissedCallReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SendSmsAsync(string toNumber, string body) => throw new NotSupportedException("SMS sending not implemented");
}
```

- [ ] **Step 3: Create ICallLogService**

```csharp
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ICallLogService
{
    Task<int> AddEntryAsync(CallLogEntry entry);
    Task UpdateEntryAsync(int id, DateTime endedAt, string status, int durationSeconds);
    Task<IReadOnlyList<CallLogEntry>> GetRecentAsync(int count = 50);
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Interfaces/
git commit -m "feat: add GVTrunk interfaces — ITrunkAdapter, ISmsProvider, ICallLogService"
```

---

### Task 4: Create test project

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk.Tests/RotaryPhoneController.GVTrunk.Tests.csproj`

- [ ] **Step 1: Create test project**

```bash
cd src
dotnet new xunit -n RotaryPhoneController.GVTrunk.Tests -f net10.0
```

- [ ] **Step 2: Update .csproj for references and dependencies**

Replace content:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\RotaryPhoneController.GVTrunk\RotaryPhoneController.GVTrunk.csproj" />
    <ProjectReference Include="..\RotaryPhoneController.Core\RotaryPhoneController.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Moq" Version="4.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add to solution**

```bash
cd D:/prj/RotaryPhone
dotnet sln add src/RotaryPhoneController.GVTrunk.Tests/RotaryPhoneController.GVTrunk.Tests.csproj
```

- [ ] **Step 4: Remove generated test file, build**

Delete `UnitTest1.cs`. Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk.Tests/RotaryPhoneController.GVTrunk.Tests.csproj RotaryPhone.sln
git commit -m "feat: add GVTrunk test project"
```

---

## Chunk 2: CallLogService (SQLite)

### Task 5: Write CallLogService tests

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk.Tests/CallLogServiceTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using RotaryPhoneController.GVTrunk.Models;
using RotaryPhoneController.GVTrunk.Services;

namespace RotaryPhoneController.GVTrunk.Tests;

public class CallLogServiceTests : IAsyncLifetime
{
    private CallLogService _service = null!;

    public async Task InitializeAsync()
    {
        _service = new CallLogService(":memory:");
        await _service.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddEntry_ReturnsPositiveId()
    {
        var entry = new CallLogEntry(0, DateTime.UtcNow, null, "Inbound", "+15551234567", "Answered", null);
        var id = await _service.AddEntryAsync(entry);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task GetRecent_ReturnsEntriesInDescendingOrder()
    {
        var now = DateTime.UtcNow;
        await _service.AddEntryAsync(new CallLogEntry(0, now.AddMinutes(-2), null, "Inbound", "+1111", "Missed", null));
        await _service.AddEntryAsync(new CallLogEntry(0, now.AddMinutes(-1), null, "Outbound", "+2222", "Answered", null));
        await _service.AddEntryAsync(new CallLogEntry(0, now, null, "Inbound", "+3333", "Answered", null));

        var recent = await _service.GetRecentAsync(10);

        Assert.Equal(3, recent.Count);
        Assert.Equal("+3333", recent[0].RemoteNumber);
        Assert.Equal("+1111", recent[2].RemoteNumber);
    }

    [Fact]
    public async Task UpdateEntry_SetsEndTimeAndDuration()
    {
        var entry = new CallLogEntry(0, DateTime.UtcNow, null, "Inbound", "+15551234567", "Answered", null);
        var id = await _service.AddEntryAsync(entry);

        var endTime = DateTime.UtcNow.AddMinutes(5);
        await _service.UpdateEntryAsync(id, endTime, "Answered", 300);

        var recent = await _service.GetRecentAsync(1);
        Assert.Equal(300, recent[0].DurationSeconds);
        Assert.NotNull(recent[0].EndedAt);
    }

    [Fact]
    public async Task GetRecent_RespectsCountLimit()
    {
        for (int i = 0; i < 10; i++)
            await _service.AddEntryAsync(new CallLogEntry(0, DateTime.UtcNow.AddMinutes(i), null, "Inbound", $"+{i}", "Missed", null));

        var recent = await _service.GetRecentAsync(3);
        Assert.Equal(3, recent.Count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVTrunk.Tests/ --filter CallLogServiceTests --verbosity quiet`
Expected: Build error — `CallLogService` does not exist yet.

- [ ] **Step 3: Commit failing tests**

```bash
git add src/RotaryPhoneController.GVTrunk.Tests/CallLogServiceTests.cs
git commit -m "test: add CallLogService tests (failing — no implementation yet)"
```

---

### Task 6: Implement CallLogService

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Services/CallLogService.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.Data.Sqlite;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Services;

public class CallLogService : ICallLogService
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public CallLogService(string dbPath)
    {
        _connectionString = dbPath == ":memory:"
            ? "Data Source=:memory:"
            : $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS CallLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT,
                Direction TEXT NOT NULL,
                RemoteNumber TEXT NOT NULL,
                Status TEXT NOT NULL,
                DurationSeconds INTEGER,
                Notes TEXT
            )";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> AddEntryAsync(CallLogEntry entry)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO CallLog (StartedAt, EndedAt, Direction, RemoteNumber, Status, DurationSeconds, Notes)
            VALUES (@started, @ended, @dir, @number, @status, @duration, @notes);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@started", entry.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ended", entry.EndedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@dir", entry.Direction);
        cmd.Parameters.AddWithValue("@number", entry.RemoteNumber);
        cmd.Parameters.AddWithValue("@status", entry.Status);
        cmd.Parameters.AddWithValue("@duration", entry.DurationSeconds ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", entry.Notes ?? (object)DBNull.Value);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateEntryAsync(int id, DateTime endedAt, string status, int durationSeconds)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE CallLog SET EndedAt = @ended, Status = @status, DurationSeconds = @duration
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ended", endedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@duration", durationSeconds);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<CallLogEntry>> GetRecentAsync(int count = 50)
    {
        if (_connection == null) throw new InvalidOperationException("Not initialized");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, StartedAt, EndedAt, Direction, RemoteNumber, Status, DurationSeconds, Notes FROM CallLog ORDER BY StartedAt DESC LIMIT @count";
        cmd.Parameters.AddWithValue("@count", count);

        var entries = new List<CallLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new CallLogEntry(
                reader.GetInt32(0),
                DateTime.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return entries;
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test src/RotaryPhoneController.GVTrunk.Tests/ --filter CallLogServiceTests --verbosity quiet`
Expected: All 4 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Services/CallLogService.cs
git commit -m "feat: implement CallLogService with SQLite persistence"
```

---

## Chunk 3: GVTrunkAdapter (SIP Registration + Calls)

### Task 7: Write GVTrunkAdapter tests

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk.Tests/GVTrunkAdapterTests.cs`

- [ ] **Step 1: Write tests for registration and inbound call detection**

```csharp
using Moq;
using RotaryPhoneController.GVTrunk.Adapters;
using RotaryPhoneController.GVTrunk.Models;
using Microsoft.Extensions.Options;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Tests;

public class GVTrunkAdapterTests
{
    private readonly TrunkConfig _config = new()
    {
        SipServer = "sip.voip.ms",
        SipPort = 5060,
        SipUsername = "testuser",
        SipPassword = "testpass",
        LocalSipPort = 15061,  // Use high port to avoid conflicts
        LocalIp = "127.0.0.1",
        OutboundCallerId = "+15551234567"
    };

    private GVTrunkAdapter CreateAdapter()
    {
        var options = Options.Create(_config);
        var logger = new Mock<ILogger>().Object;
        return new GVTrunkAdapter(options, logger);
    }

    [Fact]
    public void InitialState_IsNotRegistered()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsRegistered);
    }

    [Fact]
    public void InitialState_IsNotListening()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsListening);
    }

    [Fact]
    public void StartListening_SetsIsListeningTrue()
    {
        var adapter = CreateAdapter();
        adapter.StartListening();
        Assert.True(adapter.IsListening);
        adapter.Dispose();
    }

    [Fact]
    public void OnIncomingCall_EventCanBeSubscribed()
    {
        var adapter = CreateAdapter();
        bool fired = false;
        adapter.OnIncomingCall += () => fired = true;
        // Event wiring verified — actual SIP triggering tested in integration
        Assert.False(fired);
    }

    [Fact]
    public void OnRegistrationChanged_EventCanBeSubscribed()
    {
        var adapter = CreateAdapter();
        bool? lastValue = null;
        adapter.OnRegistrationChanged += (registered) => lastValue = registered;
        Assert.Null(lastValue);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVTrunk.Tests/ --filter GVTrunkAdapterTests --verbosity quiet`
Expected: Build error — `GVTrunkAdapter` does not exist yet.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk.Tests/GVTrunkAdapterTests.cs
git commit -m "test: add GVTrunkAdapter tests (failing — no implementation yet)"
```

---

### Task 8: Implement GVTrunkAdapter

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Adapters/GVTrunkAdapter.cs`

This is the largest single file. It handles SIP registration with VoIP.ms, inbound INVITE handling, outbound call origination, and RTP bridging.

- [ ] **Step 1: Implement GVTrunkAdapter**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using Serilog;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace RotaryPhoneController.GVTrunk.Adapters;

public class GVTrunkAdapter : ITrunkAdapter, IDisposable
{
    private readonly TrunkConfig _config;
    private readonly ILogger _logger;
    private SIPTransport? _sipTransport;
    private SIPRegistrationUserAgent? _regAgent;
    private string? _activeCallId;

    public bool IsRegistered { get; private set; }
    public bool IsListening => _sipTransport != null;

    public event Action<bool>? OnHookChange;
    public event Action<string>? OnDigitsReceived;
    public event Action? OnIncomingCall;
    public event Action<bool>? OnRegistrationChanged;
    public event Action<string>? OnDtmfReceived;

    public GVTrunkAdapter(IOptions<TrunkConfig> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public void StartListening()
    {
        if (_sipTransport != null) return;

        _sipTransport = new SIPTransport();

        var localIP = _config.LocalIp == "0.0.0.0"
            ? GetLocalIPForTarget(_config.SipServer)
            : _config.LocalIp;

        var listenEndpoint = new IPEndPoint(IPAddress.Parse(localIP), _config.LocalSipPort);
        _sipTransport.AddSIPChannel(new SIPUDPChannel(listenEndpoint));

        _sipTransport.SIPTransportRequestReceived += OnSIPRequestReceived;

        _logger.Information("GVTrunk SIP transport started on {IP}:{Port}", localIP, _config.LocalSipPort);
    }

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_sipTransport == null)
            StartListening();

        try
        {
            var localIP = _config.LocalIp == "0.0.0.0"
                ? GetLocalIPForTarget(_config.SipServer)
                : _config.LocalIp;

            var regUri = SIPURI.ParseSIPURIRelaxed($"sip:{_config.SipServer}:{_config.SipPort}");
            var localContact = new SIPURI(SIPSchemesEnum.sip,
                SIPEndPoint.ParseSIPEndPoint($"{localIP}:{_config.LocalSipPort}"));

            _regAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _config.SipUsername,
                _config.SipPassword,
                _config.SipServer,
                _config.RegisterIntervalSeconds);

            _regAgent.RegistrationSuccessful += (uri) =>
            {
                IsRegistered = true;
                OnRegistrationChanged?.Invoke(true);
                _logger.Information("GVTrunk registered with {Server}", _config.SipServer);
            };

            _regAgent.RegistrationFailed += (uri, resp, retry) =>
            {
                IsRegistered = false;
                OnRegistrationChanged?.Invoke(false);
                _logger.Warning("GVTrunk registration failed: {Response}", resp?.ReasonPhrase ?? "timeout");
            };

            _regAgent.RegistrationRemoved += (uri) =>
            {
                IsRegistered = false;
                OnRegistrationChanged?.Invoke(false);
                _logger.Information("GVTrunk registration removed");
            };

            _regAgent.Start();
            _logger.Information("GVTrunk registration started for {User}@{Server}",
                _config.SipUsername, _config.SipServer);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GVTrunk registration error");
            IsRegistered = false;
            OnRegistrationChanged?.Invoke(false);
        }
    }

    public Task UnregisterAsync(CancellationToken ct = default)
    {
        _regAgent?.Stop();
        IsRegistered = false;
        OnRegistrationChanged?.Invoke(false);
        _logger.Information("GVTrunk unregistered");
        return Task.CompletedTask;
    }

    private Task<SocketError> OnSIPRequestReceived(SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        switch (sipRequest.Method)
        {
            case SIPMethodsEnum.INVITE:
                HandleInboundInvite(sipRequest, remoteEndPoint);
                break;
            case SIPMethodsEnum.BYE:
                HandleBye(sipRequest);
                break;
            case SIPMethodsEnum.CANCEL:
                HandleCancel(sipRequest);
                break;
            case SIPMethodsEnum.OPTIONS:
                var optResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                _sipTransport?.SendResponseAsync(optResp);
                break;
        }
        return Task.FromResult(SocketError.Success);
    }

    private void HandleInboundInvite(SIPRequest sipRequest, SIPEndPoint remoteEndPoint)
    {
        _logger.Information("GVTrunk inbound INVITE from {Remote}, caller: {From}",
            remoteEndPoint, sipRequest.Header.From?.FromURI?.User);

        // Send 180 Ringing
        var ringingResp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ringing, null);
        _sipTransport?.SendResponseAsync(ringingResp);

        _activeCallId = sipRequest.Header.CallId;

        // Fire incoming call event — CallManager will ring the HT801
        OnIncomingCall?.Invoke();
    }

    private void HandleBye(SIPRequest sipRequest)
    {
        _logger.Information("GVTrunk BYE received — call ended");
        var resp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(resp);
        _activeCallId = null;
        OnHookChange?.Invoke(false);
    }

    private void HandleCancel(SIPRequest sipRequest)
    {
        _logger.Information("GVTrunk CANCEL received — caller hung up");
        var resp = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
        _sipTransport?.SendResponseAsync(resp);
        _activeCallId = null;
        OnHookChange?.Invoke(false);
    }

    public async Task<string> PlaceOutboundCallAsync(string e164Number)
    {
        if (!IsRegistered)
            throw new InvalidOperationException("Trunk not registered");
        if (_sipTransport == null)
            throw new InvalidOperationException("SIP transport not started");

        var localIP = _config.LocalIp == "0.0.0.0"
            ? GetLocalIPForTarget(_config.SipServer)
            : _config.LocalIp;

        var destUri = SIPURI.ParseSIPURIRelaxed($"sip:{e164Number}@{_config.SipServer}");
        var fromHeader = new SIPFromHeader(_config.OutboundCallerId,
            new SIPURI(_config.SipUsername, _config.SipServer, null),
            CallProperties.CreateNewTag());

        var inviteRequest = SIPRequest.GetRequest(
            SIPMethodsEnum.INVITE,
            destUri,
            new SIPToHeader(null, destUri, null),
            fromHeader);

        inviteRequest.Header.Contact = new List<SIPContactHeader>
        {
            new SIPContactHeader(null, new SIPURI(SIPSchemesEnum.sip,
                SIPEndPoint.ParseSIPEndPoint($"{localIP}:{_config.LocalSipPort}")))
        };
        inviteRequest.Header.UserAgent = "RotaryPhoneController-GVTrunk/1.0";

        var targetEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp,
            IPAddress.Parse(await ResolveHostAsync(_config.SipServer)), _config.SipPort);

        var sendResult = await _sipTransport.SendRequestAsync(targetEndpoint, inviteRequest);
        _activeCallId = inviteRequest.Header.CallId;

        _logger.Information("GVTrunk outbound INVITE to {Number} via {Server}", e164Number, _config.SipServer);
        return _activeCallId;
    }

    public void SendInviteToHT801(string extensionToRing, string targetIP)
    {
        // Delegate to the existing SIPSorceryAdapter — GVTrunkAdapter doesn't
        // directly ring the HT801. CallManager handles this via its ISipAdapter reference.
        _logger.Debug("GVTrunk SendInviteToHT801 called — delegating to primary SIP adapter");
    }

    public void CancelPendingInvite()
    {
        _logger.Debug("GVTrunk CancelPendingInvite called");
        _activeCallId = null;
    }

    private string GetLocalIPForTarget(string targetHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var targetIP = Dns.GetHostAddresses(targetHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (targetIP != null)
            {
                socket.Connect(targetIP, 1);
                if (socket.LocalEndPoint is IPEndPoint ep)
                    return ep.Address.ToString();
            }
        }
        catch { }
        return "0.0.0.0";
    }

    private async Task<string> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString()
            ?? throw new Exception($"Cannot resolve {host}");
    }

    public void Dispose()
    {
        _regAgent?.Stop();
        _sipTransport?.Shutdown();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test src/RotaryPhoneController.GVTrunk.Tests/ --filter GVTrunkAdapterTests --verbosity quiet`
Expected: All 5 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Adapters/GVTrunkAdapter.cs
git commit -m "feat: implement GVTrunkAdapter — SIP registration, inbound/outbound calls"
```

---

### Task 9: Implement TrunkRegistrationService

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Services/TrunkRegistrationService.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.Extensions.Hosting;
using RotaryPhoneController.GVTrunk.Interfaces;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Services;

public class TrunkRegistrationService : IHostedService
{
    private readonly ITrunkAdapter _trunk;
    private readonly ILogger _logger;

    public TrunkRegistrationService(ITrunkAdapter trunk, ILogger logger)
    {
        _trunk = trunk;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Information("TrunkRegistrationService starting");
        _trunk.StartListening();
        await _trunk.RegisterAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("TrunkRegistrationService stopping");
        await _trunk.UnregisterAsync(cancellationToken);
    }
}
```

- [ ] **Step 2: Build and run all tests**

Run: `dotnet build && dotnet test --verbosity quiet`
Expected: Build succeeded, all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Services/TrunkRegistrationService.cs
git commit -m "feat: add TrunkRegistrationService — hosted service for SIP keepalive"
```

---

## Chunk 4: GmailSmsService

### Task 10: Write GmailSmsService tests

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk.Tests/GmailSmsServiceTests.cs`

- [ ] **Step 1: Write parsing tests**

The Gmail service parses email subjects from Google Voice. Test the parsing logic in isolation.

```csharp
using RotaryPhoneController.GVTrunk.Services;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Tests;

public class GmailSmsServiceTests
{
    [Theory]
    [InlineData("New text message from +15551234567", SmsType.Sms, "+15551234567")]
    [InlineData("New text message from (555) 123-4567", SmsType.Sms, "(555) 123-4567")]
    [InlineData("Missed call from +15559876543", SmsType.MissedCall, "+15559876543")]
    [InlineData("Missed call from (555) 987-6543", SmsType.MissedCall, "(555) 987-6543")]
    public void ParseSubject_ExtractsTypeAndNumber(string subject, SmsType expectedType, string expectedNumber)
    {
        var result = GmailSmsService.ParseGvSubject(subject);
        Assert.NotNull(result);
        Assert.Equal(expectedType, result.Value.type);
        Assert.Equal(expectedNumber, result.Value.number);
    }

    [Theory]
    [InlineData("Re: Your order confirmation")]
    [InlineData("")]
    [InlineData("Google Voice notification")]
    public void ParseSubject_ReturnsNull_ForNonGvSubjects(string subject)
    {
        var result = GmailSmsService.ParseGvSubject(subject);
        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Commit failing tests**

```bash
git add src/RotaryPhoneController.GVTrunk.Tests/GmailSmsServiceTests.cs
git commit -m "test: add GmailSmsService parsing tests (failing)"
```

---

### Task 11: Implement GmailSmsService

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Services/GmailSmsService.cs`

- [ ] **Step 1: Implement with static parsing method for testability**

```csharp
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Services;

public class GmailSmsService : ISmsProvider
{
    private readonly TrunkConfig _config;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private GmailService? _gmail;
    private readonly HashSet<string> _processedIds = new();

    public event Action<SmsNotification>? OnSmsReceived;
    public event Action<SmsNotification>? OnMissedCallReceived;

    public GmailSmsService(IOptions<TrunkConfig> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public static (SmsType type, string number)? ParseGvSubject(string subject)
    {
        var smsMatch = Regex.Match(subject, @"New text message from (.+)$");
        if (smsMatch.Success)
            return (SmsType.Sms, smsMatch.Groups[1].Value.Trim());

        var missedMatch = Regex.Match(subject, @"Missed call from (.+)$");
        if (missedMatch.Success)
            return (SmsType.MissedCall, missedMatch.Groups[1].Value.Trim());

        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.GmailCredentialsPath))
        {
            _logger.Warning("Gmail credentials path not configured — SMS service disabled");
            return;
        }

        try
        {
            var credential = await AuthorizeAsync(ct);
            _gmail = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "RotaryPhoneController-GVTrunk"
            });

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = PollLoopAsync(_cts.Token);
            _logger.Information("GmailSmsService started — polling every {Interval}s", _config.GmailPollIntervalSeconds);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start Gmail SMS service");
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _logger.Information("GmailSmsService stopped");
        return Task.CompletedTask;
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        using var stream = new FileStream(_config.GmailCredentialsPath, FileMode.Open, FileAccess.Read);
        var credPath = Path.GetDirectoryName(_config.GmailCredentialsPath) ?? ".";
        var tokenPath = Path.Combine(credPath, "token");

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            new[] { GmailService.Scope.GmailModify },
            "user",
            ct,
            new FileDataStore(tokenPath, true));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Gmail poll error");
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.GmailPollIntervalSeconds), ct);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (_gmail == null) return;

        var request = _gmail.Users.Messages.List("me");
        request.Q = "from:voice-noreply@google.com is:unread";
        request.MaxResults = 10;

        var response = await request.ExecuteAsync(ct);
        if (response.Messages == null) return;

        foreach (var msgRef in response.Messages)
        {
            if (!_processedIds.Add(msgRef.Id)) continue; // skip already-processed

            var msg = await _gmail.Users.Messages.Get("me", msgRef.Id).ExecuteAsync(ct);
            var subject = msg.Payload?.Headers?.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "";
            var snippet = msg.Snippet ?? "";

            var parsed = ParseGvSubject(subject);
            if (parsed == null) continue;

            var notification = new SmsNotification(
                parsed.Value.number,
                parsed.Value.type == SmsType.Sms ? snippet : null,
                DateTime.UtcNow,
                parsed.Value.type);

            if (parsed.Value.type == SmsType.Sms)
                OnSmsReceived?.Invoke(notification);
            else
                OnMissedCallReceived?.Invoke(notification);

            // Mark as read
            var modReq = new ModifyMessageRequest { RemoveLabelIds = new[] { "UNREAD" } };
            await _gmail.Users.Messages.Modify(modReq, "me", msgRef.Id).ExecuteAsync(ct);

            // Cap set size to prevent unbounded growth
            if (_processedIds.Count > 500)
                _processedIds.Clear();

            _logger.Information("Processed GV notification: {Type} from {Number}", parsed.Value.type, parsed.Value.number);
        }
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test src/RotaryPhoneController.GVTrunk.Tests/ --filter GmailSmsServiceTests --verbosity quiet`
Expected: All 6 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Services/GmailSmsService.cs
git commit -m "feat: implement GmailSmsService — polls Gmail for GV SMS/missed-call notifications"
```

---

## Chunk 5: API + SignalR + DI Extensions + Host Integration

### Task 12: Create REST API controller

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Api/GVTrunkController.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Api;

[ApiController]
[Route("api/gvtrunk")]
public class GVTrunkController : ControllerBase
{
    private readonly ITrunkAdapter _trunk;
    private readonly ICallLogService _callLog;
    private readonly ISmsProvider _sms;
    private readonly RotaryPhoneController.Core.PhoneManagerService _phoneManager;

    public GVTrunkController(
        ITrunkAdapter trunk,
        ICallLogService callLog,
        ISmsProvider sms,
        RotaryPhoneController.Core.PhoneManagerService phoneManager)
    {
        _trunk = trunk;
        _callLog = callLog;
        _sms = sms;
        _phoneManager = phoneManager;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var phone = _phoneManager.GetAllPhones().FirstOrDefault();
        var callManager = phone.CallManager;
        var callState = callManager?.CurrentState ?? Core.CallState.Idle;
        // NOTE: Requires adding `public DateTime? CallStartedAtUtc { get; }` to CallManager.
        // Set it when entering InCall, cleared on hangup. Returns 0 when no active call.
        return Ok(new
        {
            isRegistered = _trunk.IsRegistered,
            callState = callState.ToString(),
            activeCallDurationSeconds = callState == Core.CallState.InCall && callManager?.CallStartedAtUtc != null
                ? (int)(DateTime.UtcNow - callManager.CallStartedAtUtc.Value).TotalSeconds
                : 0
        });
    }

    [HttpGet("calls")]
    public async Task<IActionResult> GetCalls()
    {
        var calls = await _callLog.GetRecentAsync(50);
        return Ok(calls);
    }

    [HttpGet("sms")]
    public IActionResult GetSms()
    {
        return Ok(GVTrunkSmsCache.GetRecent(20));
    }

    [HttpPost("dial")]
    public async Task<IActionResult> Dial([FromBody] DialRequest request)
    {
        if (!_trunk.IsRegistered)
            return Conflict(new { error = "Trunk not registered" });
        if (string.IsNullOrWhiteSpace(request.Number))
            return BadRequest(new { error = "Number required" });

        var sessionId = await _trunk.PlaceOutboundCallAsync(request.Number);
        return Ok(new { sessionId });
    }

    [HttpPost("reregister")]
    public async Task<IActionResult> Reregister()
    {
        await _trunk.RegisterAsync();
        return Ok(new { status = "Registration initiated" });
    }

    public record DialRequest(string Number);
}

/// <summary>
/// Thread-safe in-memory cache for recent SMS notifications (shared between controller and hub).
/// </summary>
public static class GVTrunkSmsCache
{
    private static readonly List<SmsNotification> _notifications = new();
    private static readonly object _lock = new();

    public static void Add(SmsNotification notification)
    {
        lock (_lock)
        {
            _notifications.Add(notification);
            if (_notifications.Count > 100)
                _notifications.RemoveRange(0, _notifications.Count - 100);
        }
    }

    public static List<SmsNotification> GetRecent(int count)
    {
        lock (_lock)
        {
            return _notifications.TakeLast(count).ToList();
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Api/GVTrunkController.cs
git commit -m "feat: add GVTrunkController REST API"
```

---

### Task 13: Create SignalR hub

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Api/GVTrunkHub.cs`

- [ ] **Step 1: Implement**

```csharp
using Microsoft.AspNetCore.SignalR;

namespace RotaryPhoneController.GVTrunk.Api;

public class GVTrunkHub : Hub
{
    // Server → client push only in Phase 1.
    // Events are broadcast via IHubContext<GVTrunkHub> from the extension wiring.
}
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Api/GVTrunkHub.cs
git commit -m "feat: add GVTrunkHub SignalR hub"
```

---

### Task 14: Create DI extension methods

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Extensions/GVTrunkServiceExtensions.cs`

- [ ] **Step 1: Implement AddGVTrunk and MapGVTrunk**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RotaryPhoneController.GVTrunk.Adapters;
using RotaryPhoneController.GVTrunk.Api;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using RotaryPhoneController.GVTrunk.Services;

namespace RotaryPhoneController.GVTrunk.Extensions;

public static class GVTrunkServiceExtensions
{
    public static IServiceCollection AddGVTrunk(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TrunkConfig>(configuration.GetSection("GVTrunk"));

        services.AddSingleton<GVTrunkAdapter>();
        services.AddSingleton<ITrunkAdapter>(sp => sp.GetRequiredService<GVTrunkAdapter>());

        // CallLogService needs async init (SQLite schema creation).
        // Register as singleton, initialize via hosted service to avoid sync-over-async.
        services.AddSingleton<CallLogService>(sp =>
        {
            var config = configuration.GetSection("GVTrunk").Get<TrunkConfig>() ?? new TrunkConfig();
            return new CallLogService(config.CallLogDbPath);
        });
        services.AddSingleton<ICallLogService>(sp => sp.GetRequiredService<CallLogService>());
        services.AddHostedService<CallLogInitializer>();

        services.AddSingleton<ISmsProvider, GmailSmsService>();
        services.AddHostedService<TrunkRegistrationService>();
        services.AddHostedService<GVTrunkEventBridge>();

        return services;
    }

    public static IEndpointRouteBuilder MapGVTrunk(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<GVTrunkHub>("/hubs/gvtrunk");
        endpoints.MapControllers();
        return endpoints;
    }
}

/// <summary>
/// Initializes CallLogService (SQLite schema) on startup without sync-over-async.
/// </summary>
public class CallLogInitializer : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly CallLogService _callLog;

    public CallLogInitializer(CallLogService callLog) => _callLog = callLog;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        await _callLog.InitializeAsync();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Bridges service events to SignalR hub broadcasts and SMS cache.
/// Observes ITrunkAdapter, ISmsProvider, and PhoneManagerService state changes.
/// </summary>
public class GVTrunkEventBridge : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ITrunkAdapter _trunk;
    private readonly ISmsProvider _sms;
    private readonly IHubContext<GVTrunkHub> _hub;
    private readonly RotaryPhoneController.Core.PhoneManagerService _phoneManager;

    public GVTrunkEventBridge(
        ITrunkAdapter trunk,
        ISmsProvider sms,
        IHubContext<GVTrunkHub> hub,
        RotaryPhoneController.Core.PhoneManagerService phoneManager)
    {
        _trunk = trunk;
        _sms = sms;
        _hub = hub;
        _phoneManager = phoneManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _trunk.OnRegistrationChanged += async (registered) =>
            await _hub.Clients.All.SendAsync("RegistrationChanged", new { isRegistered = registered });

        _sms.OnSmsReceived += async (notification) =>
        {
            GVTrunkSmsCache.Add(notification);
            await _hub.Clients.All.SendAsync("SmsReceived", notification);
        };

        _sms.OnMissedCallReceived += async (notification) =>
        {
            GVTrunkSmsCache.Add(notification);
            await _hub.Clients.All.SendAsync("MissedCallReceived", notification);
        };

        // Observe CallManager state changes and broadcast to SignalR clients.
        // Note: CallManager.StateChanged fires with no args; read CurrentState after.
        foreach (var (phoneId, callManager) in _phoneManager.GetAllPhones())
        {
            callManager.StateChanged += async () =>
                await _hub.Clients.All.SendAsync("CallStateChanged", new
                {
                    phoneId,
                    callState = callManager.CurrentState.ToString()
                });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Extensions/
git commit -m "feat: add GVTrunk DI extensions — AddGVTrunk() / MapGVTrunk()"
```

---

### Task 15: Integrate into RotaryPhone Server

**Files:**
- Modify: `src/RotaryPhoneController.Server/Program.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Add config section to appsettings.json**

Add after the `"RotaryPhone"` section:

```json
"GVTrunk": {
  "SipServer": "sip.voip.ms",
  "SipPort": 5060,
  "SipUsername": "",
  "SipPassword": "",
  "LocalSipPort": 5061,
  "LocalIp": "0.0.0.0",
  "GoogleVoiceForwardingNumber": "",
  "OutboundCallerId": "",
  "RegisterIntervalSeconds": 60,
  "GmailPollIntervalSeconds": 30,
  "GmailCredentialsPath": "",
  "CallLogDbPath": "data/gvtrunk-calllog.db"
}
```

- [ ] **Step 2: Add to Program.cs**

Add after existing service registrations (before `var app = builder.Build()`):

```csharp
// Google Voice Trunk (optional — only active if GVTrunk config has credentials)
builder.Services.AddGVTrunk(builder.Configuration);
```

Add after `app.MapControllers()`:

```csharp
app.MapGVTrunk();
```

Add the using at top:
```csharp
using RotaryPhoneController.GVTrunk.Extensions;
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build && dotnet test --verbosity quiet`
Expected: Build succeeded, all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Server/Program.cs src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat: wire GVTrunk into RotaryPhone server — AddGVTrunk + MapGVTrunk"
```

---

## Chunk 6: React Dashboard

### Task 16: Create useGVTrunk hook

**Files:**
- Create: `src/RotaryPhoneController.Client/src/hooks/useGVTrunk.ts`

- [ ] **Step 1: Implement**

```typescript
import { useState, useEffect, useCallback } from 'react';
import { HubConnectionBuilder, HubConnection } from '@microsoft/signalr';
import axios from 'axios';

interface TrunkStatus {
  isRegistered: boolean;
  callState: string;
  activeCallDurationSeconds: number;
}

interface CallLogEntry {
  id: number;
  startedAt: string;
  endedAt: string | null;
  direction: string;
  remoteNumber: string;
  status: string;
  durationSeconds: number | null;
}

interface SmsNotification {
  fromNumber: string;
  body: string | null;
  receivedAt: string;
  type: 'Sms' | 'MissedCall';
}

export function useGVTrunk() {
  const [status, setStatus] = useState<TrunkStatus>({ isRegistered: false, callState: 'Unknown', activeCallDurationSeconds: 0 });
  const [calls, setCalls] = useState<CallLogEntry[]>([]);
  const [smsMessages, setSmsMessages] = useState<SmsNotification[]>([]);
  const [connection, setConnection] = useState<HubConnection | null>(null);

  useEffect(() => {
    // Fetch initial data
    axios.get('/api/gvtrunk/status').then(r => setStatus(r.data)).catch(() => {});
    axios.get('/api/gvtrunk/calls').then(r => setCalls(r.data)).catch(() => {});
    axios.get('/api/gvtrunk/sms').then(r => setSmsMessages(r.data)).catch(() => {});

    // SignalR connection
    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/gvtrunk')
      .withAutomaticReconnect()
      .build();

    hub.on('RegistrationChanged', (data: { isRegistered: boolean }) => {
      setStatus(data);
    });

    hub.on('SmsReceived', (notification: SmsNotification) => {
      setSmsMessages(prev => [...prev.slice(-19), notification]);
    });

    hub.on('MissedCallReceived', (notification: SmsNotification) => {
      setSmsMessages(prev => [...prev.slice(-19), notification]);
    });

    hub.on('CallStateChanged', (data: { phoneId: string; callState: string }) => {
      setStatus(prev => ({ ...prev, callState: data.callState }));
    });

    hub.start().catch(err => console.error('GVTrunk hub error:', err));
    setConnection(hub);

    return () => { hub.stop(); };
  }, []);

  const dial = useCallback(async (number: string) => {
    await axios.post('/api/gvtrunk/dial', { number });
  }, []);

  const forceReregister = useCallback(async () => {
    await axios.post('/api/gvtrunk/reregister');
  }, []);

  const refreshCalls = useCallback(async () => {
    const r = await axios.get('/api/gvtrunk/calls');
    setCalls(r.data);
  }, []);

  return { status, calls, smsMessages, dial, forceReregister, refreshCalls };
}
```

- [ ] **Step 2: Commit**

```bash
git add src/RotaryPhoneController.Client/src/hooks/useGVTrunk.ts
git commit -m "feat: add useGVTrunk React hook — SignalR + REST integration"
```

---

### Task 17: Create GVTrunk dashboard page

**Files:**
- Create: `src/RotaryPhoneController.Client/src/pages/GVTrunk.tsx`
- Modify: `src/RotaryPhoneController.Client/src/App.tsx`
- Modify: `src/RotaryPhoneController.Client/src/components/Layout.tsx`

- [ ] **Step 1: Create dashboard page**

```tsx
import { useState } from 'react';
import { useGVTrunk } from '../hooks/useGVTrunk';

export default function GVTrunk() {
  const { status, calls, smsMessages, dial, forceReregister, refreshCalls } = useGVTrunk();
  const [dialNumber, setDialNumber] = useState('');

  return (
    <div style={{ padding: '1rem' }}>
      <h2>Google Voice Trunk</h2>

      {/* Status Panel */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Trunk Status</h3>
        <p>
          Registration:{' '}
          <span style={{ color: status.isRegistered ? 'green' : 'red', fontWeight: 'bold' }}>
            {status.isRegistered ? 'Registered' : 'Unregistered'}
          </span>
        </p>
        <button onClick={forceReregister}>Force Re-Register</button>
      </div>

      {/* Dial Panel */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Outbound Dial</h3>
        <input
          type="tel"
          placeholder="+1XXXXXXXXXX"
          value={dialNumber}
          onChange={e => setDialNumber(e.target.value)}
          style={{ marginRight: 8, padding: '0.25rem' }}
        />
        <button
          onClick={() => { dial(dialNumber); setDialNumber(''); }}
          disabled={!status.isRegistered || !dialNumber}
        >
          Dial via GV Trunk
        </button>
      </div>

      {/* Call History */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Call History <button onClick={refreshCalls} style={{ fontSize: '0.8em' }}>Refresh</button></h3>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left' }}>Time</th>
              <th style={{ textAlign: 'left' }}>Direction</th>
              <th style={{ textAlign: 'left' }}>Number</th>
              <th style={{ textAlign: 'left' }}>Status</th>
              <th style={{ textAlign: 'left' }}>Duration</th>
            </tr>
          </thead>
          <tbody>
            {calls.map(c => (
              <tr key={c.id}>
                <td>{new Date(c.startedAt).toLocaleString()}</td>
                <td>{c.direction}</td>
                <td>{c.remoteNumber}</td>
                <td>{c.status}</td>
                <td>{c.durationSeconds != null ? `${c.durationSeconds}s` : '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* SMS Notifications */}
      <div style={{ padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>SMS / Missed Calls</h3>
        {smsMessages.length === 0 ? (
          <p style={{ color: '#888' }}>No notifications yet</p>
        ) : (
          smsMessages.map((s, i) => (
            <div key={i} style={{ marginBottom: '0.5rem', padding: '0.5rem', background: '#f5f5f5', borderRadius: 4 }}>
              <strong>{s.type === 'Sms' ? 'SMS' : 'Missed Call'}</strong> from {s.fromNumber}
              <span style={{ color: '#888', marginLeft: 8 }}>{new Date(s.receivedAt).toLocaleTimeString()}</span>
              {s.body && <p style={{ margin: '0.25rem 0 0' }}>{s.body}</p>}
            </div>
          ))
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Add route to App.tsx**

Add import and route:

```tsx
import GVTrunk from './pages/GVTrunk';
// In Routes:
<Route path="/gvtrunk" element={<GVTrunk />} />
```

- [ ] **Step 3: Add nav link to Layout.tsx**

Add a navigation link to the GV Trunk page.

- [ ] **Step 4: Build React client**

Run: `cd src/RotaryPhoneController.Client && npm run build`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Client/
git commit -m "feat: add GVTrunk React dashboard — status, dial, call history, SMS panels"
```

---

## Chunk 7: Blazor Components

### Task 18: Create Blazor dashboard components

**Files:**
- Create: `src/RotaryPhoneController.GVTrunk/Components/GVTrunkDashboard.razor`
- Create: `src/RotaryPhoneController.GVTrunk/Components/TrunkStatusPanel.razor`
- Create: `src/RotaryPhoneController.GVTrunk/Components/CallHistoryTable.razor`
- Create: `src/RotaryPhoneController.GVTrunk/Components/SmsNotificationsPanel.razor`
- Create: `src/RotaryPhoneController.GVTrunk/Components/OutboundDialPanel.razor`

- [ ] **Step 0: Create `_Imports.razor`**

Create `src/RotaryPhoneController.GVTrunk/Components/_Imports.razor` so Blazor components can resolve each other without full namespaces:

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using RotaryPhoneController.GVTrunk.Components
@using RotaryPhoneController.GVTrunk.Interfaces
@using RotaryPhoneController.GVTrunk.Models
```

- [ ] **Step 1: Create GVTrunkDashboard.razor**

Top-level component that composes the four panels:

```razor
<div class="gvtrunk-dashboard">
    <h2>Google Voice Trunk</h2>
    <TrunkStatusPanel />
    <OutboundDialPanel />
    <CallHistoryTable />
    <SmsNotificationsPanel />
</div>
```

- [ ] **Step 2: Create TrunkStatusPanel.razor**

Registration badge, re-register button. Injects `ITrunkAdapter`, subscribes to `OnRegistrationChanged`.

- [ ] **Step 3: Create CallHistoryTable.razor**

Table of recent calls. Injects `ICallLogService`, refreshes every 10s via timer.

- [ ] **Step 4: Create SmsNotificationsPanel.razor**

Notification feed. Injects `ISmsProvider`, subscribes to `OnSmsReceived` / `OnMissedCallReceived`.

- [ ] **Step 5: Create OutboundDialPanel.razor**

E.164 input + dial button. Injects `ITrunkAdapter`, calls `PlaceOutboundCallAsync`.

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.GVTrunk/Components/
git commit -m "feat: add GVTrunk Blazor components — dashboard, status, calls, SMS, dial"
```

---

## Chunk 8: Final Integration + All Tests

### Task 19: Run full test suite and verify build

- [ ] **Step 1: Run all tests**

Run: `dotnet test --verbosity quiet`
Expected: All tests pass across both test projects.

- [ ] **Step 2: Build for Linux deployment**

Run: `dotnet publish src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj --configuration Release --runtime linux-x64 -f net10.0 --self-contained --output publish/linux-x64 -v quiet`
Expected: Build succeeded.

- [ ] **Step 3: Verify React client builds**

Run: `cd src/RotaryPhoneController.Client && npm run build`
Expected: Build succeeded.

- [ ] **Step 4: Final commit**

```bash
git add src/RotaryPhoneController.GVTrunk/ src/RotaryPhoneController.GVTrunk.Tests/
git add src/RotaryPhoneController.Server/Program.cs src/RotaryPhoneController.Server/appsettings.json
git add src/RotaryPhoneController.Client/
git commit -m "feat: Google Voice Trunk integration complete — SIP trunk, Gmail SMS, dashboard"
```

---

## RTest Integration (Separate Session)

These changes go into the RTest repo (`D:\prj\RTest\RTest`), not this repo:

1. Add project reference to `RotaryPhoneController.GVTrunk.csproj`
2. Add `builder.Services.AddGVTrunk(builder.Configuration)` + `app.MapGVTrunk()` to `Program.cs`
3. Add `"GVTrunk"` config section to `appsettings.json`
4. Create `Pages/GVTrunk.razor` mounting `<GVTrunkDashboard />`
5. Add nav link in `Shared/NavMenu.razor`

Pass these instructions to the RTest Claude session when ready.
