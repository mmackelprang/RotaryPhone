# HT801 address resolution + config binder fix — plan

**Branches:** `fix/ht801-invite-target` (PR1), `feat/ht801-registrar-binding` (PR2)
**Origin:** 2026-07-28 live bug — inbound calls show **"Ringing"** in the Radio.Web UI but the physical
rotary phone bell never rings. The SIP INVITE that rings the bell is sent to a stale address
`192.168.86.22`; the HT801's actual address is `192.168.86.240` (MAC `ec:74:d7:88:45:05`, now
DHCP-pinned). **Editing config does not fix this** — a hardcoded default wins over configuration.

---

## 1. Investigation verification

The investigation was re-verified against current `main` (`28a9f8a`) before writing this plan. Every
claim holds. Line numbers drifted in three places; corrected below.

| Claim | Status | Current location |
|---|---|---|
| `Phones` pre-seeded with one element | **Confirmed** | `AppConfiguration.cs:124-127` (as cited) |
| Seeded element carries hardcoded `192.168.86.22` | **Confirmed** | `AppConfiguration.cs:21` (as cited) |
| `Bind(appConfig)` appends rather than replaces | **Confirmed — empirically, see §2** | `Program.cs:73-74` (as cited) |
| `Program.cs` handles the empty-list case | **Confirmed** | `Program.cs:77-81` — but it *re-seeds* `new RotaryPhoneConfig()`, which reintroduces the hardcoded IP. Fix A alone is therefore **not sufficient**; see Task 1.2 |
| First-wins registration silently discards the real entry | **Confirmed** | `PhoneManagerService.cs:77-81` (**cited as 109-113 — drifted**). Log text `"Phone {PhoneId} is already registered"` matches the observed journal line exactly |
| `HT801ConfigService` is last-wins → divergence trap | **Confirmed** | `HT801ConfigService.cs:34-46`, dictionary assignment at `:36` (**cited as 34-38 — drifted**) |
| `/api/phone/system-status` reports the *configured* address, not the INVITE target | **Confirmed** | `PhoneController.cs:116-119` reads `_ht801Service.GetConfig(...)` (last-wins → `.240`) while the INVITE uses the first-wins entry (`.22`). **Not a valid verification signal** |
| UI sets `Ringing` unconditionally before the INVITE | **Confirmed** | `CallManager.cs:363` sets state; INVITE at `CallManager.cs:389` (both exact) |
| Bell path | **Confirmed** | `CallManager.cs:389` → `SIPSorceryAdapter.cs:429-508`, UDP send at `:487` |
| REGISTER Contact is echoed but never stored | **Confirmed** | `SIPSorceryAdapter.cs:370-399` (cited as 370-390) |

### Findings the investigation did not cover

1. **There is a second INVITE call site.** `CallManager.cs:325` (`SimulateIncomingCall`) also calls
   `SendInviteToHT801` with `_phoneConfig.HT801IpAddress`, and `DiagnosticsController.cs:152` calls it
   with `ht801Config.IpAddress` (the *last-wins* value — so the diagnostics "ring test" would have
   rung the **correct** address while real calls rang the wrong one). This is why address resolution
   in PR2 goes **inside `SIPSorceryAdapter.SendInviteToHT801`** — a single chokepoint covering all
   three call sites — rather than at each caller.
2. **`Bind` is not idempotent** (see §2, CASE 3). Even after Fix A, calling `.Bind()` twice on the same
   instance re-introduces duplicates. This is the argument for Fix B being *fail-fast* rather than
   *last-wins*: an empty seed list alone does not make the class of bug impossible.
3. **`appsettings.Production.json` has no `GVBridge` section at all.** So `GVBridge:HT801Ip` resolves
   only from `appsettings.json` — the file the deploy script overwrites (`Deploy-ToLinux.ps1:89`
   excludes only `appsettings.Production.json` from the rsync; `:127-129` back it up and restore it).
   Any hand-edit of the audio-leg IP on the box is destroyed by the next deploy. Consolidation (§E)
   removes this key entirely, which eliminates the hazard rather than papering over it.
4. **`GVBridgeConfig.HT801Ip` is only a fallback.** `GVAudioBridgeService.cs:79` uses
   `remoteRtpAddress ?? _config.HT801Ip` — the SDP-negotiated address normally wins. So the stale
   value there is a latent trap, not an active second bug. It still gets consolidated.
5. **The HT801's registration AOR may not equal the extension we ring.** `SIPSorceryAdapter.cs:327`
   shows the device sends INVITEs as user `rotaryphone`, while we ring extension `1000`. The binding
   lookup must therefore fall back to "the only binding we have" when the AOR doesn't match, or PR2's
   self-healing silently never engages. Task 2.5 covers this.

### The one place the investigation is wrong

**Scope H's fixture inventory is partly incorrect.** `SipAdapterTests.cs:173,184,186` and
`SipDiagnosticServiceTests.cs:21,42,54` contain **`192.168.86.250`**, not `192.168.86.22` — a *third*
historical HT801 address (it also survives in the stale `bin/Release/net10.0/linux-arm64/appsettings.json`
from the Pi deployment era). A fourth literal, `192.168.86.50`, appears in the same file as the
*local/server* address. Corrected inventory in Task 2.11.

This does not weaken the root cause — it strengthens the argument for scope F: the device has held at
least three addresses over the project's life, and every one of them was at some point compiled into
source.

**Nothing else contradicts the investigation.** Confidence is raised from ~95% to effectively certain
by §2.

---

## 2. Empirical confirmation of the binder behaviour

The investigator flagged that the `ConfigurationBinder` append behaviour was reasoned, not executed.
It was executed against the **same SDK the project builds with** (`dotnet 10.0.301`,
`Microsoft.Extensions.Configuration.Binder, Version=10.0.0.0`), replicating `AppConfiguration`'s exact
shape and the two-file `appsettings.json` + `appsettings.Production.json` layering:

```
SDK binder: Microsoft.Extensions.Configuration.Binder, Version=10.0.0.0, ...
[CASE1] before Bind: Phones.Count=1
[CASE1] after  Bind: Phones.Count=2
  [CASE1] Phones[0] Id=default Ip=192.168.86.22      <- seeded hardcoded default
  [CASE1] Phones[1] Id=default Ip=192.168.86.240     <- the real config
  [CASE1] WARN Phone default is already registered   <- fires exactly ONCE, matching the journal
[CASE1] CallManager would INVITE -> 192.168.86.22    <- the bug, reproduced

[CASE2] after Bind: Phones.Count=1                   <- with an empty seed list
  [CASE2] Phones[0] Id=default Ip=192.168.86.240     <- Fix A works

[CASE3] after 2nd Bind on same instance: Phones.Count=2   <- Bind is NOT idempotent
```

CASE 1 reproduces the production symptom exactly, including the single-occurrence warning. CASE 2
proves Fix A is sufficient for the immediate bell restoration. CASE 3 is the new finding in §1.2.

---

## 3. Goal

1. **Restore the bell today** with a small, independently reviewable, independently verifiable change.
2. Make the failure class **impossible to reintroduce silently**: no hardcoded site IPs in source, no
   silent discard of configuration, fail-fast on anything ambiguous.
3. Make the HT801 address **self-healing** by learning it from the device's own SIP REGISTER, so a
   future DHCP change fixes itself within one registration interval.
4. Leave behind a **single documented place** to change the address, and a documented way to verify it.

---

## 4. Design decisions

### D1 — Two PRs, not one *(justification, since the prompt asked for a judgement)*

**PR1** = scope A + B + D + the highest-value regression test. Roughly 6 lines of behaviour change
plus tests. **PR2** = scope C + E + F + G + H.

Rationale: PR1's entire value is *speed to a ringing bell*. Scope C (fail-fast validation) and E
(config-schema consolidation) change the shape of configuration — if either is subtly wrong the
service refuses to start, which is a strictly worse outage than today's silent misroute, and it would
block the bell fix behind a larger review. Scope F is a new subsystem (~250 lines). Bundling couples a
trivially-reviewable fix to a feature-sized diff. Split.

Accepted cost: PR1 edits the IP in `appsettings.json`; PR2 deletes one of those keys. Minor churn,
called out in Task 2.4.

### D2 — Duplicate phone Ids: **fail fast**, not last-wins

The prompt asked for a decision and stated a preference for failing loudly. Agreed, and §1.2
strengthens it: an empty seed list does not make duplicates impossible (any second `Bind`, or a
genuinely duplicated entry in `appsettings.Production.json`, still produces them). Last-wins would
have masked *this* bug but would equally silently mask a config typo. Two guards:

- `AppConfigurationValidator.Validate` (new, in Core, unit-testable) rejects duplicate Ids at startup
  with an actionable message; `Program.cs` logs `Fatal` and exits non-zero.
- `PhoneManagerService` throws on duplicate Id rather than logging a warning and returning. It has
  exactly one caller (`InitializePhones`, `PhoneManagerService.cs:54`), so this is safe.

### D3 — Learned binding source: **the REGISTER's source address**, not the Contact header

RFC 3261 says to use the Contact URI. In practice a Grandstream ATA can advertise a stale or NAT-confused
host there, and the source address of the packet is *provably* reachable — it just delivered a datagram.
Decision: bind to `remoteEndPoint.Address`, record `ContactURI.Host` for diagnostics, and log a warning
when they disagree. On a flat LAN with no NAT they are identical, so this costs nothing and is strictly
more robust. (Rejected alternative: prefer Contact and fall back to source — same result on the happy
path, worse on the failure path we actually care about.)

### D4 — Learned binding beats configuration, always (when fresh)

`SendInviteToHT801`'s `targetIP` parameter becomes the **cold-start fallback**, used only in the window
between service start and the first REGISTER, or when the binding has gone stale. When configured and
learned disagree, the learned address is used *and* a warning is logged naming the config key to fix.
Freshness = `expires` (as requested by the device, default 3600s) + 5 min grace, because the HT801
re-registers at ~50% of expiry and one missed refresh must not invalidate the binding.

### D5 — Bindings are in-memory only, never persisted

A binding learned before a restart may be stale. The device re-registers within ~50 minutes of any
restart regardless, and the configured fallback covers that window. Persisting would trade a
self-correcting cache for a second stale-state store — the exact problem being fixed.

### D6 — Single source of truth = `RotaryPhone:Phones[].HT801IpAddress`

`GVBridge:HT801Ip` is **deleted** from the schema (both the model default and the JSON key). The GV
audio bridge resolves its fallback address through the same Core resolver. Since that key lived only in
`appsettings.json` — the file the deploy overwrites — deleting it removes the deploy-overwrite hazard
outright.

`appsettings.json` keeps a populated (correct) `HT801IpAddress` as the dev/template default rather than
an empty string. *Rejected alternative:* ship `""` in `appsettings.json` so a lost
`appsettings.Production.json` fails loudly. It would break plain local `dotnet run` (no
`appsettings.Development.json` exists), and PR2's fail-fast validation plus the learned binding already
cover the failure mode.

### D7 — Test fixtures move to TEST-NET-1, not to the new real IP

Scope H fixtures become `192.0.2.x` (RFC 5737 documentation range) rather than `192.168.86.240`, so a
future grep for the production address never again returns test noise, and no fixture can ever be
mistaken for real config.

### D8 — UI honesty about INVITE failure: in scope, last, and in-repo only

`CallManager.cs:363` sets `Ringing` before the INVITE at `:389`, so the UI is a false positive whenever
the INVITE fails. Task 2.9 makes `SendInviteToHT801` return a `bool` and emits a distinct
`BellInviteFailed` SignalR event plus an `ERROR` log. It deliberately does **not** change the
`Ringing` state (the *call* really is ringing on the cell/GV leg — only the bell failed) and does not
require any Radio.Web change to land; consuming the new event is a follow-up handoff.

---

## PR1 — Restore the bell *(branch `fix/ht801-invite-target`)*

Small, self-contained, independently verifiable. Deploy this first.

### Task 1.1 — Stop pre-seeding the phone list

`src/RotaryPhoneController.Core/Configuration/AppConfiguration.cs` — replace lines 121-127:

```csharp
    /// <summary>
    /// List of configured rotary phones. Bound from "RotaryPhone:Phones".
    /// MUST start empty. .NET's ConfigurationBinder APPENDS to a non-null List&lt;T&gt; rather than
    /// replacing it or binding into existing elements, so any pre-seeded element survives binding
    /// and — because registration was first-wins — shadowed the real configuration entirely.
    /// See docs/plans/ht801-address-resolution-and-config-binder-fix.md.
    /// </summary>
    public List<RotaryPhoneConfig> Phones { get; set; } = new();
```

### Task 1.2 — Fail fast instead of re-seeding a default phone

New file `src/RotaryPhoneController.Core/Configuration/AppConfigurationValidator.cs`:

```csharp
namespace RotaryPhoneController.Core.Configuration;

/// <summary>
/// Thrown when the bound <see cref="AppConfiguration"/> is unusable. Startup treats this as fatal:
/// a missing or duplicated phone entry produces a service that reports "Ringing" while the bell
/// never rings — a far worse failure mode than refusing to start.
/// </summary>
public class ConfigurationValidationException(string message) : Exception(message);

public static class AppConfigurationValidator
{
    /// <summary>
    /// Validates bound configuration, throwing on the first problem with an actionable message.
    /// </summary>
    public static void Validate(AppConfiguration config)
    {
        if (config.Phones.Count == 0)
        {
            throw new ConfigurationValidationException(
                "No phones configured. Add at least one entry under \"RotaryPhone:Phones\" " +
                "(Id, Name, HT801IpAddress, HT801Extension) in appsettings.Production.json.");
        }

        var duplicateIds = config.Phones
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new ConfigurationValidationException(
                $"Duplicate phone Id(s) in \"RotaryPhone:Phones\": {string.Join(", ", duplicateIds)}. " +
                "Each phone must have a unique Id. Duplicates were previously discarded silently, " +
                "which routed calls to a stale HT801 address.");
        }
    }
}
```

`src/RotaryPhoneController.Server/Program.cs` — replace lines 76-81:

```csharp
// Validate configuration — fail fast and loudly. There is no safe default here: the previous
// behaviour (warn, then append a `new RotaryPhoneConfig()`) reintroduced a hardcoded HT801 address
// and produced a service that looked healthy while the bell never rang.
try
{
    AppConfigurationValidator.Validate(appConfig);
}
catch (ConfigurationValidationException ex)
{
    Log.Fatal("Invalid RotaryPhone configuration: {Message}", ex.Message);
    Log.CloseAndFlush();
    return 1;
}
```

Returning a value from top-level statements makes the implicit `Main` return `int`, which makes the
bare `return;` on the `gv-login` path a compile error. Change `Program.cs:44` in the same commit:

```csharp
    return 0;
```

### Task 1.3 — Fail fast on duplicate registration

`src/RotaryPhoneController.Core/PhoneManagerService.cs` — replace `InitializePhones` (lines 50-63):

```csharp
    private void InitializePhones()
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phoneConfig in _config.Phones)
        {
            // Fail loudly rather than discarding. The previous behaviour (warn + return) silently
            // kept the FIRST entry — which the configuration binder had appended a hardcoded
            // default ahead of — and rang a stale address.
            if (!seenIds.Add(phoneConfig.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate phone Id '{phoneConfig.Id}' in RotaryPhone:Phones. " +
                    "Each configured phone must have a unique Id.");
            }

            RegisterPhone(
                phoneConfig.Id,
                _sipAdapter,
                _bluetoothAdapter,
                _rtpBridge,
                _callManagerLogger,
                phoneConfig,
                _config.RtpBasePort);
        }
    }
```

And replace the first-wins guard (lines 77-81):

```csharp
        if (_phoneManagers.ContainsKey(phoneId))
        {
            throw new InvalidOperationException(
                $"Phone '{phoneId}' is already registered. Re-registering would silently discard " +
                "the new configuration — check RotaryPhone:Phones for duplicate Ids.");
        }
```

`RegisterPhone` has exactly one caller (`PhoneManagerService.cs:54`), so no other path is affected.

### Task 1.4 — Correct the repo config

The box is already correct; the repo is not, so the next deploy would re-break it.

- `src/RotaryPhoneController.Server/appsettings.json:45` — `"HT801IpAddress": "192.168.86.240"`
- `src/RotaryPhoneController.Server/appsettings.json:68` — `"HT801Ip": "192.168.86.240"`
  *(this key is deleted in Task 2.4; corrected here so PR1 is deployable on its own)*
- `src/RotaryPhoneController.Server/appsettings.Production.json:43` — `"HT801IpAddress": "192.168.86.240"`

**Deploy note:** `Deploy-ToLinux.ps1:89,127-129` preserves the box's `appsettings.Production.json`, so
the box keeps whatever it already has for `Phones` — the repo edit is for the *next fresh* deploy and
for anyone reading the file. `appsettings.json` **is** overwritten, so its correction takes effect on
this deploy.

### Task 1.5 — The regression test that would have caught this

New file `src/RotaryPhoneController.Tests/ConfigurationBindingTests.cs`. This is the highest-value test
in the plan: it exercises the real `AppConfiguration`, the real binder, and the real
`PhoneManagerService` together, and it fails on today's `main`.

`RotaryPhoneController.Tests` references `RotaryPhoneController.Server` (a Web SDK project), so the
ASP.NET Core shared framework flows transitively — `ConfigurationBuilder` / `AddJsonStream` / `Bind`
need no new package. If restore complains, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
to the test csproj, matching `RotaryPhoneController.Server.Tests`.

```csharp
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

/// <summary>
/// Regression tests for the 2026-07 "UI says Ringing but the bell never rings" bug.
///
/// Root cause: AppConfiguration.Phones was pre-seeded with one element carrying a hardcoded HT801
/// address, and .NET's ConfigurationBinder APPENDS to a non-null List&lt;T&gt; instead of replacing
/// it. A single-phone config therefore produced TWO phones, and first-wins registration kept the
/// hardcoded one — so every INVITE went to a stale address that no config edit could change.
/// </summary>
public class ConfigurationBindingTests
{
    private const string ConfiguredIp = "192.0.2.240";

    private const string SinglePhoneJson = """
    {
      "RotaryPhone": {
        "SipPort": 5060,
        "RtpBasePort": 49000,
        "Phones": [
          {
            "Id": "default",
            "Name": "Rotary Phone",
            "HT801IpAddress": "192.0.2.240",
            "HT801Extension": "1000"
          }
        ]
      }
    }
    """;

    private static AppConfiguration BindSinglePhone()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SinglePhoneJson));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var appConfig = new AppConfiguration();
        configuration.GetSection("RotaryPhone").Bind(appConfig);
        return appConfig;
    }

    [Fact]
    public void Bind_SinglePhoneConfig_ProducesExactlyOnePhoneWithConfiguredAddress()
    {
        var appConfig = BindSinglePhone();

        Assert.Single(appConfig.Phones);
        Assert.Equal(ConfiguredIp, appConfig.Phones[0].HT801IpAddress);
    }

    [Fact]
    public void AppConfiguration_PhonesList_StartsEmpty()
    {
        // A non-empty default is APPENDED to by the binder, shadowing real configuration.
        Assert.Empty(new AppConfiguration().Phones);
    }

    [Fact]
    public void PhoneManagerService_RingsTheConfiguredAddress_NotACompiledDefault()
    {
        var appConfig = BindSinglePhone();
        var sipAdapter = new Mock<ISipAdapter>();

        var manager = new PhoneManagerService(
            Mock.Of<ILogger<PhoneManagerService>>(),
            appConfig,
            sipAdapter.Object,
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<IRtpAudioBridge>(),
            Mock.Of<ILogger<CallManager>>());

        Assert.Equal(1, manager.PhoneCount);

        manager.GetPhone("default")!.SimulateIncomingCall();

        sipAdapter.Verify(x => x.SendInviteToHT801("1000", ConfiguredIp), Times.Once);
    }

    [Fact]
    public void PhoneManagerService_ThrowsOnDuplicatePhoneId_RatherThanDiscardingSilently()
    {
        var appConfig = new AppConfiguration();
        appConfig.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.22" });
        appConfig.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = ConfiguredIp });

        var ex = Assert.Throws<InvalidOperationException>(() => new PhoneManagerService(
            Mock.Of<ILogger<PhoneManagerService>>(),
            appConfig,
            Mock.Of<ISipAdapter>(),
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<IRtpAudioBridge>(),
            Mock.Of<ILogger<CallManager>>()));

        Assert.Contains("Duplicate phone Id", ex.Message);
    }
}
```

New file `src/RotaryPhoneController.Tests/AppConfigurationValidatorTests.cs`:

```csharp
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

public class AppConfigurationValidatorTests
{
    [Fact]
    public void Validate_NoPhones_Throws()
    {
        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(new AppConfiguration()));

        Assert.Contains("RotaryPhone:Phones", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateIds_Throws()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.10" });
        config.Phones.Add(new RotaryPhoneConfig { Id = "DEFAULT", HT801IpAddress = "192.0.2.11" });

        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(config));

        Assert.Contains("Duplicate phone Id", ex.Message);
    }

    [Fact]
    public void Validate_SingleWellFormedPhone_DoesNotThrow()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.240" });

        AppConfigurationValidator.Validate(config);
    }
}
```

### Task 1.6 — PR1 verification on the live box

See §Test plan. **Do not use `/api/phone/system-status`.**

---

## PR2 — Durable fix *(branch `feat/ht801-registrar-binding`)*

### Task 2.1 — Strip hardcoded site IPs from source defaults

- `src/RotaryPhoneController.Core/Configuration/AppConfiguration.cs:21`:

```csharp
    /// <summary>
    /// IP address of the HT801 ATA device. No default — a site-specific address compiled into
    /// source is unfixable by configuration and silently outlives the hardware it described.
    /// Supplied by "RotaryPhone:Phones[].HT801IpAddress"; validated at startup.
    /// </summary>
    public string HT801IpAddress { get; set; } = "";
```

- `src/RotaryPhoneController.Core/HT801/HT801Config.cs:11` → `public string IpAddress { get; set; } = "";`
  (`PhoneController.cs:122` already guards on empty before probing reachability.)
- `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:8` → **delete the property** (Task 2.4).

### Task 2.2 — Extend startup validation to cover addresses

Append to `AppConfigurationValidator.Validate`:

```csharp
        foreach (var phone in config.Phones)
        {
            if (string.IsNullOrWhiteSpace(phone.HT801IpAddress))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has no HT801IpAddress. Set " +
                    $"\"RotaryPhone:Phones[].HT801IpAddress\" in appsettings.Production.json " +
                    "(see docs/HT801-ADDRESS.md).");
            }

            if (!System.Net.IPAddress.TryParse(phone.HT801IpAddress, out _))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has an unparseable HT801IpAddress " +
                    $"'{phone.HT801IpAddress}'. Expected a literal IPv4 address, e.g. 192.168.86.240.");
            }

            if (string.IsNullOrWhiteSpace(phone.HT801Extension))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has no HT801Extension (the SIP extension to ring, e.g. 1000).");
            }
        }
```

Add matching tests to `AppConfigurationValidatorTests` (empty address, garbage address, empty extension).

### Task 2.3 — Registrar binding model + store

New file `src/RotaryPhoneController.Core/Sip/RegistrarBinding.cs`:

```csharp
namespace RotaryPhoneController.Core.Sip;

/// <summary>
/// A learned SIP registrar binding: where a registered endpoint (the HT801) can actually be reached.
/// Learned from the source address of its REGISTER, which the device repeats roughly every 50
/// minutes — so a DHCP move self-heals within one registration interval.
/// </summary>
/// <param name="AddressOfRecord">URI user part the device registered as (e.g. "rotaryphone", "1000").</param>
/// <param name="Address">Address to send INVITEs to — the IP the REGISTER actually arrived from.</param>
/// <param name="Port">Source SIP port of the REGISTER (normally 5060).</param>
/// <param name="ContactHost">Host advertised in the device's Contact header. Diagnostics only — see plan D3.</param>
/// <param name="LearnedAtUtc">When this binding was last refreshed.</param>
/// <param name="ExpiresSeconds">Expiry the device requested in its REGISTER.</param>
public sealed record RegistrarBinding(
    string AddressOfRecord,
    string Address,
    int Port,
    string? ContactHost,
    DateTime LearnedAtUtc,
    int ExpiresSeconds)
{
    /// <summary>
    /// Grace added to the requested expiry before a binding is considered stale. The HT801
    /// re-registers at ~50% of expiry, so a single missed refresh must not invalidate the binding.
    /// </summary>
    public static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(5);

    public bool IsFresh(DateTime utcNow) =>
        utcNow - LearnedAtUtc <= TimeSpan.FromSeconds(ExpiresSeconds) + StaleGrace;
}
```

New file `src/RotaryPhoneController.Core/Sip/RegistrarBindingStore.cs`:

```csharp
using System.Collections.Concurrent;

namespace RotaryPhoneController.Core.Sip;

public interface IRegistrarBindingStore
{
    void Record(RegistrarBinding binding);
    void Remove(string addressOfRecord);
    RegistrarBinding? Get(string addressOfRecord);

    /// <summary>
    /// The sole binding, when exactly one endpoint is registered. Single-ATA deployments ring an
    /// extension ("1000") that need not match the AOR the device registered under ("rotaryphone"),
    /// so an exact-match-only lookup would never engage. Returns null when ambiguous.
    /// </summary>
    RegistrarBinding? GetSingle();

    IReadOnlyCollection<RegistrarBinding> All();
}

/// <summary>
/// In-memory registrar binding table. Deliberately not persisted: a binding learned before a restart
/// may be stale, the device re-registers within ~50 minutes of any restart, and the configured
/// address covers that window. Persisting would trade a self-correcting cache for a second stale
/// address store — the exact problem this fixes.
/// </summary>
public sealed class RegistrarBindingStore : IRegistrarBindingStore
{
    private readonly ConcurrentDictionary<string, RegistrarBinding> _bindings =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(RegistrarBinding binding) => _bindings[binding.AddressOfRecord] = binding;

    public void Remove(string addressOfRecord) => _bindings.TryRemove(addressOfRecord, out _);

    public RegistrarBinding? Get(string addressOfRecord) =>
        _bindings.TryGetValue(addressOfRecord, out var binding) ? binding : null;

    public RegistrarBinding? GetSingle() =>
        _bindings.Count == 1 ? _bindings.Values.First() : null;

    public IReadOnlyCollection<RegistrarBinding> All() => _bindings.Values.ToList();
}
```

### Task 2.4 — Consolidate to one address key

- Delete `HT801Ip` from `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:8`.
- Delete `"HT801Ip": ...` from `src/RotaryPhoneController.Server/appsettings.json:68`.
- `src/RotaryPhoneController.GVBridge/Services/GVAudioBridgeService.cs:79` — resolve the fallback from
  Core instead of GV's own copy. Inject `AppConfiguration` and `IRegistrarBindingStore`
  (GVBridge already references Core; both are singletons), then:

```csharp
        // Fallback address when the SDP didn't carry one: prefer the learned registrar binding,
        // then the single configured phone. There is exactly ONE HT801 address key in this system —
        // RotaryPhone:Phones[].HT801IpAddress. See docs/HT801-ADDRESS.md.
        var effectiveRemoteIp = remoteRtpAddress
            ?? _bindingStore.GetSingle()?.Address
            ?? _appConfig.Phones.FirstOrDefault()?.HT801IpAddress
            ?? throw new InvalidOperationException(
                   "No HT801 address available for the GV audio bridge (no SDP address, no learned " +
                   "registrar binding, no configured phone).");
```

- Update `docs/SETUP-GVBridge.md:158` to drop the `HT801Ip` key and point at `docs/HT801-ADDRESS.md`.
- Leave the historical plan `docs/superpowers/plans/2026-03-30-sip-wss-dtls-srtp-integration.md:767`
  untouched (it is a record of what was built at the time, not live config).

### Task 2.5 — Learn the binding from REGISTER

`src/RotaryPhoneController.Core/SIPSorceryAdapter.cs` — add the field and constructor parameter
(optional, so existing tests and the second constructor keep compiling):

```csharp
    private readonly IRegistrarBindingStore? _bindingStore;
```

```csharp
    public SIPSorceryAdapter(ILogger logger, AppConfiguration config,
        IRegistrarBindingStore? bindingStore = null)
    {
        _logger = logger;
        _localIPAddress = config.SipListenAddress;
        _localPort = config.SipPort;
        _bindingStore = bindingStore;
    }

    public SIPSorceryAdapter(ILogger logger, string localIPAddress = "0.0.0.0", int localPort = 5060,
        IRegistrarBindingStore? bindingStore = null)
    {
        _logger = logger;
        _localIPAddress = localIPAddress;
        _localPort = localPort;
        _bindingStore = bindingStore;
    }
```

Insert into `HandleRegister`, after the existing `_logger.Debug("Processing REGISTER ...")` call at
`SIPSorceryAdapter.cs:379-380` and before the response is built:

```csharp
            // Learn where this device actually lives. RFC 3261 nominates the Contact URI, but a
            // misconfigured ATA can advertise a stale host there, whereas the source address of the
            // REGISTER just provably delivered a datagram. Prefer the source; keep Contact for
            // diagnostics; warn when they disagree. See plan decision D3.
            var addressOfRecord = sipRequest.Header.To?.ToURI?.User
                                  ?? sipRequest.Header.From?.FromURI?.User;
            var contactHost = contact?.ContactURI?.Host;

            if (!string.IsNullOrEmpty(addressOfRecord) && _bindingStore != null)
            {
                if (sipRequest.Header.Expires == 0)
                {
                    // Explicit de-registration — drop the binding so we don't ring a device that
                    // has told us it is going away.
                    _bindingStore.Remove(addressOfRecord);
                    _logger.Information("Registrar binding removed for {Aor} (Expires: 0)", addressOfRecord);
                }
                else
                {
                    var binding = new RegistrarBinding(
                        addressOfRecord,
                        remoteEndPoint.Address.ToString(),
                        remoteEndPoint.Port,
                        contactHost,
                        DateTime.UtcNow,
                        expires);

                    _bindingStore.Record(binding);

                    _logger.Information(
                        "Learned registrar binding: {Aor} -> {Address}:{Port} (contact={ContactHost}, expires={Expires}s)",
                        binding.AddressOfRecord, binding.Address, binding.Port,
                        contactHost ?? "(none)", expires);

                    if (contactHost != null &&
                        !contactHost.Equals(binding.Address, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warning(
                            "REGISTER Contact host {ContactHost} differs from source address {Source} — " +
                            "using the source address for INVITEs", contactHost, binding.Address);
                    }
                }
            }
```

Note: the existing line 377 coerces `Expires <= 0` to 3600 for the *response*. That behaviour is left
alone; the block above reads `sipRequest.Header.Expires` directly so a genuine de-registration is
still honoured for binding purposes.

### Task 2.6 — Prefer the learned binding when sending the INVITE

`src/RotaryPhoneController.Core/SIPSorceryAdapter.cs` — new method:

```csharp
    /// <summary>
    /// Chooses the address an INVITE is actually sent to: a fresh learned registrar binding when one
    /// exists, otherwise the configured address. The log line this emits is the ONLY trustworthy
    /// answer to "which address will the bell be rung at" — /api/phone/system-status reports the
    /// configured value and can disagree.
    /// </summary>
    internal string ResolveTargetAddress(string extensionToRing, string configuredIP)
    {
        var binding = _bindingStore?.Get(extensionToRing) ?? _bindingStore?.GetSingle();

        if (binding == null)
        {
            _logger.Warning(
                "No registrar binding learned yet — falling back to configured HT801 address " +
                "{ConfiguredIP}. The bell will not ring if that address is stale.", configuredIP);
            return configuredIP;
        }

        if (!binding.IsFresh(DateTime.UtcNow))
        {
            _logger.Warning(
                "Registrar binding for {Aor} is stale (learned {LearnedAt:u}, expiry {Expires}s) — " +
                "falling back to configured address {ConfiguredIP}",
                binding.AddressOfRecord, binding.LearnedAtUtc, binding.ExpiresSeconds, configuredIP);
            return configuredIP;
        }

        if (!binding.Address.Equals(configuredIP, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning(
                "Configured HT801 address {ConfiguredIP} does not match the learned address " +
                "{LearnedIP} — using the learned address. Update " +
                "RotaryPhone:Phones[].HT801IpAddress (see docs/HT801-ADDRESS.md).",
                configuredIP, binding.Address);
        }

        return binding.Address;
    }
```

And at the top of `SendInviteToHT801` (`SIPSorceryAdapter.cs:429`), immediately inside the `try`,
before the existing `_logger.Information("Sending INVITE ...")`:

```csharp
            // Single chokepoint: every caller (CallManager x2, DiagnosticsController) routes through
            // here, so resolution lives here rather than at each call site. `targetIP` becomes the
            // cold-start fallback for the window before the first REGISTER arrives.
            targetIP = ResolveTargetAddress(extensionToRing, targetIP);
```

`ResolveTargetAddress` is `internal`; `RotaryPhoneController.Core.csproj` already declares
`InternalsVisibleTo("RotaryPhoneController.Tests")`, so it is directly testable.

### Task 2.7 — DI registration

`src/RotaryPhoneController.Server/Program.cs`, before the `ISipAdapter` registration at line 279:

```csharp
// Registrar bindings learned from the HT801's own REGISTER. Singleton: shared by the SIP adapter
// (writer + reader), the GV audio bridge (reader), and the diagnostics endpoint (reader).
builder.Services.AddSingleton<IRegistrarBindingStore, RegistrarBindingStore>();
```

and inside the `ISipAdapter` factory (line ~290):

```csharp
    var adapter = new SIPSorceryAdapter(
        serilogLogger, config, sp.GetRequiredService<IRegistrarBindingStore>());
```

### Task 2.8 — A diagnostics endpoint that tells the truth

`src/RotaryPhoneController.Server/Controllers/DiagnosticsController.cs` — inject
`IRegistrarBindingStore _bindingStore` and add:

```csharp
    /// <summary>
    /// Learned registrar bindings — i.e. where INVITEs will ACTUALLY be sent.
    /// Use this, NOT /api/phone/system-status, to verify HT801 addressing: system-status reports the
    /// configured address, which is a different value and can disagree.
    /// </summary>
    [HttpGet("sip-registrations")]
    public IActionResult GetSipRegistrations()
    {
        var now = DateTime.UtcNow;

        return Ok(_bindingStore.All().Select(b => new
        {
            addressOfRecord = b.AddressOfRecord,
            address = b.Address,
            port = b.Port,
            contactHost = b.ContactHost,
            learnedAtUtc = b.LearnedAtUtc,
            expiresSeconds = b.ExpiresSeconds,
            isFresh = b.IsFresh(now)
        }));
    }
```

### Task 2.9 — Make the UI stop lying about the bell *(last; independently revertable)*

`CallManager.cs:363` sets `Ringing` unconditionally before the INVITE at `:389`, so a failed INVITE
still shows "Ringing" in Radio.Web. Minimal in-repo honesty:

- `ISipAdapter.SendInviteToHT801` returns `bool` (socket-level send success).
  `SIPSorceryAdapter` returns `false` on the `sendResult != SocketError.Success` branch
  (`SIPSorceryAdapter.cs:491-496`), on the `_sipTransport == null` guard (`:435-439`), and from the
  `catch` (`:504-507`); `true` otherwise. `GVTrunkAdapter.cs:201` returns `false` (it only delegates).
- `CallManager` (both call sites, `:325` and `:389`) captures the result. On `false`, log
  `LogError("Bell INVITE failed — call is ringing on the network leg but the rotary phone will not ring")`
  and set a new `public bool BellInviteFailed { get; private set; }` before invoking `StateChanged`.
- `SignalRNotifierService.OnStateChanged` (`:90-102`) emits an additional
  `_hubContext.Clients.All.SendAsync("BellInviteFailed", phoneId)` when the flag is set.

Radio.Web ignores unknown hub events, so no cross-repo change is required to land this. Consuming the
event in `PhoneStatusHero.razor` is a follow-up handoff — note it in `docs/handoffs/`.

### Task 2.10 — Registrar binding tests *(required)*

New file `src/RotaryPhoneController.Tests/RegistrarBindingTests.cs`:

```csharp
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Sip;
using Serilog;

namespace RotaryPhoneController.Tests;

public class RegistrarBindingTests
{
    private const string ConfiguredIp = "192.0.2.22";   // stale, as shipped in config
    private const string LearnedIp = "192.0.2.240";     // where the device actually is

    private static RegistrarBinding Fresh(string aor, string address, int expires = 3600) =>
        new(aor, address, 5060, address, DateTime.UtcNow, expires);

    private static SIPSorceryAdapter AdapterWith(IRegistrarBindingStore store) =>
        new(Mock.Of<ILogger>(), "0.0.0.0", 5060, store);

    [Fact]
    public void Resolve_PrefersFreshLearnedBinding_OverConfiguredAddress()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("1000", LearnedIp));

        Assert.Equal(LearnedIp, AdapterWith(store).ResolveTargetAddress("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_FallsBackToConfigured_WhenNothingLearnedYet()
    {
        Assert.Equal(ConfiguredIp,
            AdapterWith(new RegistrarBindingStore()).ResolveTargetAddress("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_FallsBackToConfigured_WhenBindingIsStale()
    {
        var store = new RegistrarBindingStore();
        // Expiry 60s, learned 2h ago — well beyond expiry + StaleGrace.
        store.Record(new RegistrarBinding("1000", LearnedIp, 5060, LearnedIp,
            DateTime.UtcNow.AddHours(-2), 60));

        Assert.Equal(ConfiguredIp, AdapterWith(store).ResolveTargetAddress("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_UsesSingleBinding_WhenExtensionDoesNotMatchRegisteredAor()
    {
        // The HT801 registers as "rotaryphone" but is rung at extension "1000".
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));

        Assert.Equal(LearnedIp, AdapterWith(store).ResolveTargetAddress("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_DoesNotGuess_WhenMultipleBindingsAndNoAorMatch()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));
        store.Record(Fresh("kitchen", "192.0.2.241"));

        Assert.Equal(ConfiguredIp, AdapterWith(store).ResolveTargetAddress("1000", ConfiguredIp));
    }

    [Fact]
    public void Record_RefreshesExistingBinding_WhenDeviceMoves()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", "192.0.2.99"));
        store.Record(Fresh("rotaryphone", LearnedIp));

        Assert.Single(store.All());
        Assert.Equal(LearnedIp, store.Get("rotaryphone")!.Address);
    }

    [Fact]
    public void Remove_DropsBinding_OnDeRegistration()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));
        store.Remove("rotaryphone");

        Assert.Null(store.Get("rotaryphone"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void IsFresh_HonoursExpiryPlusGrace()
    {
        var now = DateTime.UtcNow;
        var binding = new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp,
            now.AddSeconds(-3600), 3600);

        Assert.True(binding.IsFresh(now));                      // exactly at expiry, within grace
        Assert.False(binding.IsFresh(now.AddMinutes(6)));       // beyond expiry + 5 min grace
    }
}
```

### Task 2.11 — Test fixtures off the production address range

**The scope-H list in the bug report is partly wrong — verified line by line.** Three different
literals are in play, and only some are the stale `.22`:

| File / lines | Literal | Note |
|---|---|---|
| `DiagnosticsControllerTests.cs:78,91,101,104` | `192.168.86.22` | As cited |
| `SipAdapterTests.cs:80,82,93,122,127,145,150` | `192.168.86.22` | As cited |
| `SipAdapterTests.cs:173,184,186` | **`192.168.86.250`** | **Cited as `.22` — wrong.** A *third* historical HT801 address (it also appears in the stale `bin/Release/.../linux-arm64/appsettings.json`, from the Pi era) |
| `SipDiagnosticServiceTests.cs:21,42,54` | **`192.168.86.250`** | **Cited as `.22` — wrong.** Same third address |
| `SipAdapterTests.cs:170,199,211,217,226,237` | `192.168.86.50` | Not in the bug report. This is the *local/server* address in those fixtures, not an HT801 address |

Replace all three literals with TEST-NET-1 (RFC 5737) so no fixture can be mistaken for real config
and a grep for a production address returns only real config:

- `192.168.86.22` → `192.0.2.22`
- `192.168.86.250` → `192.0.2.250`
- `192.168.86.50` → `192.0.2.50`

Purely cosmetic — every assertion compares against the same literal its own setup supplies.

The `.250` finding is worth a line in `docs/HT801-ADDRESS.md` (Task 2.12 §6): the device has had at
least three addresses (`.250` on the Pi, `.22`, now `.240`), which is precisely why the address must be
learned rather than compiled in.

### Task 2.12 — Documentation

New file `docs/HT801-ADDRESS.md`, linked from `README.md` and `docs/SETUP-AND-TESTING.md`. Required
sections:

1. **TL;DR** — current address `192.168.86.240`, MAC `ec:74:d7:88:45:05`, DHCP-pinned on the router.
2. **The one place to change it** — `RotaryPhone:Phones[].HT801IpAddress` in
   `appsettings.Production.json` on the box (`/opt/rotary-phone/appsettings.Production.json`) and the
   repo template at `src/RotaryPhoneController.Server/appsettings.Production.json`.
3. **Every location the address can appear**, with what each one is for:
   | Location | Role |
   |---|---|
   | `appsettings.Production.json` → `RotaryPhone:Phones[].HT801IpAddress` | **Authoritative.** Preserved across deploys |
   | `appsettings.json` → same key | Dev/template default. **Overwritten by every deploy** |
   | Learned registrar binding (runtime, in-memory) | **Wins at runtime** when fresh; self-heals DHCP moves |
   | `HT801Config.IpAddress` (runtime, `HT801ConfigService`) | Projection for the UI/diagnostics. Last-wins over `Phones`. **Reporting only** |
   | ~~`GVBridge:HT801Ip`~~ | **Removed** in PR2 — was a duplicate source of truth |
4. **Procedure to change the address** — edit `appsettings.Production.json` on the box, restart
   `rotary-phone.service`, verify per §5. Note that with PR2 the learned binding usually makes this
   unnecessary; the config value is the cold-start fallback.
5. **How to verify correctly** — the valid signals, and the invalid one:
   - **Valid:** `CallManager sending INVITE to 1000@<ip>`, `INVITE target endpoint: udp:<ip>:5060`,
     `Learned registrar binding: <aor> -> <ip>:5060`, and `GET /api/diagnostics/sip-registrations`.
   - **Valid (negative):** absence of `Phone default is already registered`.
   - **NOT VALID:** `/api/phone/system-status` → `ht801IpAddress`. It reports the *configured*
     projection (`PhoneController.cs:116-119` → `HT801ConfigService`, last-wins), not the INVITE
     target. During this bug it reported the correct `.240` for months while every INVITE went to
     `.22`. Do not use it to verify addressing.
6. **The historical bug** — one paragraph plus a link to this plan, so the next person who greps
   `192.168.86.22` lands on the explanation.

Also update `docs/KNOWN-ISSUES.md` with a resolved entry, and add a `docs/handoffs/` note for the
Radio.Web side of Task 2.9.

---

## 5. Test plan

### Unit / build (both PRs, on Windows dev box)

```bash
cd /d/prj/RotaryPhone
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal
```

Gate: 0 warnings, all green. `ConfigurationBindingTests` must **fail on `main`** before Task 1.1 and
pass after — confirm the red first, or the test is not testing what it claims.

### Live verification — PR1 (bell restoration)

The box is `mmack@radio` (Ubuntu x64, Intel N100), service `rotary-phone.service`. Use the SSH MCP,
not raw `ssh`. Deploy overwrites `appsettings.json` and **preserves** `appsettings.Production.json`.

1. **Capture the "before" state** so the fix is provable:
   ```bash
   sudo journalctl -u rotary-phone.service --since "-2h" | grep -c "already registered"
   ```
   Expect ≥ 1 (once per service start).
2. **Deploy:**
   ```powershell
   ./deploy/Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64
   ```
3. **Confirm the box's authoritative config** (preserved file, so verify rather than assume):
   ```bash
   grep -A6 '"Phones"' /opt/rotary-phone/appsettings.Production.json
   ```
   `HT801IpAddress` must be `192.168.86.240`. If it is not, edit it there — that is the one place.
4. **Restart and watch startup:**
   ```bash
   sudo systemctl restart rotary-phone.service
   sudo journalctl -u rotary-phone.service -f
   ```
   Expect `Registered phone: default (Rotary Phone)` and **no** `Phone default is already registered`.
   A `Fatal: Invalid RotaryPhone configuration` line means Task 1.2 caught a real config problem —
   read the message, it names the key.
5. **Place a real inbound call** to the GV number and watch the journal. The load-bearing line:
   ```
   CallManager sending INVITE to 1000@192.168.86.240 (SDP RTP port 49000)
   INVITE target endpoint: udp:192.168.86.240:5060
   INVITE sent successfully to HT801
   ```
   Any `1000@192.168.86.22` means the fix did not take.
6. **Confirm the bell physically rings.** This is the acceptance criterion — not a log line, not a UI
   state. Answer the call and confirm two-way audio, since PR1 also touches the audio-leg config key.
7. **Do NOT** verify via `/api/phone/system-status`. It reported the correct address throughout the
   entire outage.

### Live verification — PR2 (durable behaviour)

8. Redeploy and restart. Within ~50 minutes (or immediately, by power-cycling the HT801 to force a
   re-REGISTER) expect:
   ```
   Learned registrar binding: rotaryphone -> 192.168.86.240:5060 (contact=192.168.86.240, expires=3600s)
   ```
9. Confirm the truthful diagnostics endpoint:
   ```bash
   curl -s http://radio:5000/api/diagnostics/sip-registrations | jq
   ```
   Expect one entry, `address: "192.168.86.240"`, `isFresh: true`.
10. **Self-healing proof** — the point of the whole exercise. Set
    `RotaryPhone:Phones[0].HT801IpAddress` on the box to a deliberately wrong-but-valid address
    (e.g. `192.168.86.99`), restart, wait for the HT801 to re-register (power-cycle it to force it),
    then place a real inbound call. Expect:
    ```
    Configured HT801 address 192.168.86.99 does not match the learned address 192.168.86.240 —
      using the learned address. Update RotaryPhone:Phones[].HT801IpAddress
    CallManager sending INVITE to 1000@192.168.86.99 ...
    INVITE target endpoint: udp:192.168.86.240:5060
    ```
    and **the bell rings anyway**. Note the deliberate discrepancy between the `CallManager` log
    (configured) and `INVITE target endpoint` (resolved) — the latter is authoritative. Restore the
    correct configured value afterwards.
11. **Cold-start fallback proof:** restart the service and place a call *before* any REGISTER arrives.
    Expect `No registrar binding learned yet — falling back to configured HT801 address ...` and a
    ringing bell (because the configured value is now correct).

---

## 6. Docs impact

| File | Change |
|---|---|
| `docs/HT801-ADDRESS.md` | **New.** Every address location, change procedure, correct verification, the `system-status` warning |
| `docs/SETUP-GVBridge.md:158` | Drop the `HT801Ip` key; point at `HT801-ADDRESS.md` |
| `docs/SETUP-AND-TESTING.md` | Link `HT801-ADDRESS.md` from the setup flow |
| `docs/KNOWN-ISSUES.md` | Add the resolved bug with the root cause in one paragraph |
| `README.md` | Link `HT801-ADDRESS.md` |
| `docs/handoffs/` | New note: Radio.Web consumption of the `BellInviteFailed` hub event (Task 2.9) |
| `docs/architecture/decisions/` | ADR for D3/D4/D5 (learn-from-REGISTER, learned-beats-configured, no persistence) |

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Fail-fast validation refuses to start on a config that previously "worked" | Message names the exact key and file. PR1 is deployed while the operator is watching the journal (step 4) |
| `return 1;` in top-level statements breaks the `gv-login` path's bare `return;` | Task 1.2 changes it to `return 0;` in the same commit; caught by `dotnet build` regardless |
| Learned binding points somewhere wrong (e.g. a spoofed REGISTER on the LAN) | Single-ATA LAN, no NAT. Freshness bound + explicit warning on config/learned mismatch + `sip-registrations` endpoint make it observable. Configured value remains the fallback |
| `GetSingle()` guesses wrong if a second SIP device ever registers | Returns `null` when ambiguous (test `Resolve_DoesNotGuess_...`), falling back to configuration |
| Deleting `GVBridge:HT801Ip` breaks a GV call path | `GVAudioBridgeService.cs:79` already prefers the SDP-negotiated address; the config value was only the fallback. Covered by live step 6 (two-way audio) |
| PR1 edits `appsettings.json:68` and PR2 deletes that key | Deliberate — PR1 must be independently deployable. Called out in Task 2.4 |

## 8. Branch / PR convention

Per `~/.claude/CLAUDE.md` and repo practice: feature branch → PR to `main` → merge on green gates.
PR1 and PR2 are separate branches and separate PRs; PR2 branches from `main` after PR1 merges.
Both touch source, so neither may land directly on `main`.
