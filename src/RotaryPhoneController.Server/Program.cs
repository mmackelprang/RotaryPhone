using RotaryPhoneController.Server.Hubs;
using RotaryPhoneController.Server.Services;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Contacts;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Adapters;
using RotaryPhoneController.Server.Adapters;
using RotaryPhoneController.GVTrunk.Extensions;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVBridge.Extensions;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.Sip;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Serilog;

// CLI command: gv-login — extract GV cookies via Chrome CDP, then exit
if (args.Contains("gv-login"))
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger("GvLogin");

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        // ⚠ ADDED: the box's authoritative settings live in appsettings.Production.json — it is the file
        // the deploy deliberately does NOT overwrite. Reading only appsettings.json meant gv-login could
        // resolve a different CDP port (and different cookie paths) than the running service uses, which
        // is the same class of defect as the hardcoded 9222 it is being fixed alongside.
        .AddJsonFile("appsettings.Production.json", optional: true)
        .Build();
    var gvConfig = config.GetSection("GVBridge");

    var cookiePath = gvConfig["CookieFilePath"] ?? "data/gv-cookies.enc";
    var keyPath = gvConfig["CookieKeyFilePath"] ?? "data/gv-key.bin";
    // Default 9224 — GVBridgeConfig.ChromeCdpPort's own default, and the port the bridge listens on.
    var cdpPort = int.TryParse(gvConfig["ChromeCdpPort"], out var configuredPort) ? configuredPort : 9224;

    var result = await RotaryPhoneController.GVBridge.Auth.CookieRetriever.RetrieveAndSaveAsync(
        cookiePath, keyPath, cdpPort,
        msg => logger.LogInformation("{Message}", msg));

    if (result)
        logger.LogInformation("Cookie extraction successful. Start the server normally.");
    else
        logger.LogError("Cookie extraction failed. Ensure Chrome/Chromium is running and you are logged into voice.google.com.");

    // Returning a value anywhere in top-level statements makes the implicit Main return int,
    // so this early exit must be explicit too.
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog from appsettings
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

// Use Serilog
builder.Host.UseSerilog();

// Register Serilog.ILogger in DI (GVTrunk/GVBridge services inject it directly)
builder.Services.AddSingleton<Serilog.ILogger>(Log.Logger);

// Bind configuration
var appConfig = new AppConfiguration();
builder.Configuration.GetSection("RotaryPhone").Bind(appConfig);

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

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR(options =>
    options.AddFilter<RotaryPhoneController.Server.Hubs.HubAuthFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS policies for development, Radio.Web, and GV Bridge extension
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClients", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",   // Vite dev
                "http://127.0.0.1:5173",
                "http://localhost:5002",   // Radio.Web local
                "http://radio:5002",       // Radio.Web on Ubuntu
                "http://192.168.86.55:5173",
                "https://voice.google.com") // GV Bridge extension content script
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
    // A second policy, "GVBridge", used to be registered here with AllowAnyOrigin() — a wildcard —
    // described as "permissive policy for GV Bridge HTTP event endpoint (extension content scripts)".
    // Removed 2026-09-09 with the rest of the /api/gvbridge/event carve-outs. It was never applied:
    // no UseCors("GVBridge"), no [EnableCors("GVBridge")], anywhere. Dead since the relay it named was
    // deleted in March 2026, but sitting under an inviting name — the next person needing CORS on a GV
    // endpoint could have attached it and silently granted wildcard-origin access to a live route.
    // AllowClients above already lists https://voice.google.com, so the legitimate case is covered.
});

// Register configuration as singleton
builder.Services.AddSingleton(appConfig);

// Register call history service if enabled
if (appConfig.EnableCallHistory)
{
    builder.Services.AddSingleton<ICallHistoryService>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<SqliteCallHistoryService>>();
        var dbPath = Path.Combine(AppContext.BaseDirectory, "data/call-history.db");
        return new SqliteCallHistoryService(logger, dbPath, appConfig.MaxCallHistoryEntries);
    });
}

// Register contact service if enabled
if (appConfig.EnableContacts)
{
    builder.Services.AddSingleton<IContactService>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<ContactService>>();
        var storagePath = Path.Combine(AppContext.BaseDirectory, appConfig.ContactsStoragePath);
        return new ContactService(logger, storagePath);
    });
}

// Register HT801 configuration service
builder.Services.AddSingleton<IHT801ConfigService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<HT801ConfigService>>();
    var storagePath = Path.Combine(AppContext.BaseDirectory, "data/ht801-config.json");
    return new HT801ConfigService(logger, appConfig, storagePath);
});

// Call adapter registry — runtime mode switching between BT/SIP/GV
builder.Services.AddSingleton<BluetoothCallAdapter>(sp =>
{
    var hfpAdapter = sp.GetRequiredService<IBluetoothHfpAdapter>();
    var logger = sp.GetRequiredService<ILogger<BluetoothCallAdapter>>();
    var deviceManager = sp.GetRequiredService<IBluetoothDeviceManager>();
    return new BluetoothCallAdapter(hfpAdapter, logger, deviceManager);
});
builder.Services.AddSingleton<SipTrunkCallAdapter>(sp =>
{
    var trunk = sp.GetRequiredService<ITrunkAdapter>();
    var logger = sp.GetRequiredService<ILogger<SipTrunkCallAdapter>>();
    return new SipTrunkCallAdapter(trunk, logger);
});
builder.Services.AddSingleton<ICallAdapterRegistry>(sp =>
{
    var registry = new CallAdapterRegistry(sp.GetRequiredService<ILogger<CallAdapterRegistry>>());
    registry.Register(sp.GetRequiredService<BluetoothCallAdapter>());
    registry.Register(sp.GetRequiredService<SipTrunkCallAdapter>());
    // Register GV API adapter (direct HTTP API, no CDP)
    var gvAdapter = sp.GetRequiredService<GVApiAdapter>();
    registry.Register(gvAdapter);
    // Set default adapter mode from config (GVBridge.DefaultMode or fallback to BluetoothHfp)
    var gvConfig = sp.GetRequiredService<IOptions<GVBridgeConfig>>().Value;
    var defaultMode = Enum.TryParse<CallAdapterMode>(gvConfig.DefaultMode, true, out var mode)
        ? mode : CallAdapterMode.BluetoothHfp;
    registry.SwitchModeAsync(defaultMode).GetAwaiter().GetResult();
    sp.GetRequiredService<ILogger<CallAdapterRegistry>>()
        .LogInformation("Default call adapter mode: {Mode}", defaultMode);
    return registry;
});

// HT801 reachability. Singleton, and it MUST be — SignalRNotifierService writes the background
// probe result into it and PhoneController.GetSystemStatus reads it, which is the whole point:
// REST and SignalR report one probe rather than two that disagreed. A scoped or transient
// registration would hand the controller its own permanently-empty cache, and the endpoint would
// report "Unknown" forever while the hub reported the truth.
builder.Services.AddSingleton<IHt801ReachabilityCache, Ht801ReachabilityCache>();

// Bell-failure state. Singleton and the SINGLE convergence point for "the bell did not ring":
// the immediate socket-level failure (CallManager) and the delayed INVITE outcome
// (SipDiagnosticService: timeout / 4xx) both feed it, and exactly one hub event is emitted from it.
//
// Backed by a file under data/, which the deploy script excludes — so a dismissed note survives the
// nightly restart of the consuming kiosk, a crash, and a deploy. That durability was stated to
// RadioConsole in writing before it was true; see BellFailureTracker for the reversal of plan D5.
builder.Services.AddSingleton<IBellFailureTracker>(sp =>
{
    var storePath = Path.Combine(AppContext.BaseDirectory, "data/bell-failure-state.json");
    var store = new JsonBellFailureStore(storePath, sp.GetRequiredService<ILogger<JsonBellFailureStore>>());
    return new BellFailureTracker(store, sp.GetRequiredService<ILogger<BellFailureTracker>>());
});

// Register phone manager service
builder.Services.AddSingleton<PhoneManagerService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<PhoneManagerService>>();
    var callHistoryService = appConfig.EnableCallHistory
        ? sp.GetRequiredService<ICallHistoryService>()
        : null;

    var sipAdapter = sp.GetRequiredService<ISipAdapter>();
    var bluetoothAdapter = sp.GetRequiredService<IBluetoothHfpAdapter>();
    var rtpBridge = sp.GetRequiredService<IRtpAudioBridge>();
    var callManagerLogger = sp.GetRequiredService<ILogger<CallManager>>();
    var config = sp.GetRequiredService<AppConfiguration>();
    var deviceManager = sp.GetRequiredService<IBluetoothDeviceManager>();
    var adapterRegistry = sp.GetRequiredService<ICallAdapterRegistry>();

    return new PhoneManagerService(
        logger,
        config,
        sipAdapter,
        bluetoothAdapter,
        rtpBridge,
        callManagerLogger,
        callHistoryService,
        deviceManager,
        adapterRegistry,
        // Named, not positional: PhoneManagerService's tail is optional parameters, and a silent
        // mis-bind there is exactly the bug class this change set exists to eliminate.
        bellFailureTracker: sp.GetRequiredService<IBellFailureTracker>());
});

// Register SignalR Notifier Service (Hosted Service)
builder.Services.AddHostedService<SignalRNotifierService>();

// Register BlueZ mgmt monitor (singleton + hosted service for disconnect reason detection)
#if !WINDOWS
builder.Services.AddSingleton<BluetoothMgmtMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BluetoothMgmtMonitor>());
#endif

// Register Bluetooth HFP adapter (platform-aware factory pattern)
// When BlueZBtManager is active, use mock to avoid duplicate HFP profile registration
builder.Services.AddSingleton<IBluetoothHfpAdapter>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

#if !WINDOWS
    if (config.UseActualBluetoothHfp)
    {
        // BlueZBtManager handles HFP — use mock for legacy interface to avoid UUID conflict
        var mockLogger = loggerFactory.CreateLogger<MockBluetoothHfpAdapter>();
        return new MockBluetoothHfpAdapter(mockLogger);
    }
#endif

#if !WINDOWS
    var mgmtMonitor = sp.GetService<BluetoothMgmtMonitor>();
    return BluetoothAdapterFactory.Create(config, loggerFactory, mgmtMonitor);
#else
    return BluetoothAdapterFactory.Create(config, loggerFactory);
#endif
});

// Register IBluetoothDeviceManager (multi-device BT — runs alongside legacy adapter during transition)
builder.Services.AddSingleton<IBluetoothDeviceManager>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    if (!config.UseActualBluetoothHfp)
        return new MockBluetoothDeviceManager(loggerFactory.CreateLogger<MockBluetoothDeviceManager>());

#if !WINDOWS
    return new BlueZBtManager(loggerFactory.CreateLogger<BlueZBtManager>(), config);
#else
    return new MockBluetoothDeviceManager(loggerFactory.CreateLogger<MockBluetoothDeviceManager>());
#endif
});

builder.Services.AddSingleton<IRtpAudioBridge>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
#if WINDOWS
    if (config.UseActualRtpAudioBridge)
    {
        var logger = sp.GetRequiredService<ILogger<RtpAudioBridge>>();
        return new RtpAudioBridge(logger);
    }
#endif
#if !WINDOWS
    if (config.UseActualRtpAudioBridge)
    {
        var logger = sp.GetRequiredService<ILogger<ScoRtpBridge>>();
        return new ScoRtpBridge(logger, config.ScoUdpBasePort, config.ScoUdpBasePort + 1);
    }
#endif
    var mockLogger = sp.GetRequiredService<ILogger<MockRtpAudioBridge>>();
    return new MockRtpAudioBridge(mockLogger);
});

// Registrar bindings learned from the HT801's own REGISTER. Singleton: shared by the SIP adapter
// (writer + reader), the GV audio bridge (reader), and the diagnostics endpoint (reader).
builder.Services.AddSingleton<IRegistrarBindingStore, RegistrarBindingStore>();

// Register Core services as singletons
builder.Services.AddSingleton<ISipAdapter>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SIPSorceryAdapter>>();
    var config = sp.GetRequiredService<AppConfiguration>();

    // TODO: migrate SIPSorceryAdapter to use ILogger<T>. For now, bridge Serilog from configured logger.
    var serilogLogger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .CreateLogger();

    var adapter = new SIPSorceryAdapter(
        serilogLogger, config, sp.GetRequiredService<IRegistrarBindingStore>());
    adapter.StartListening();
    return adapter;
});

// Register CallManager for the first phone (backward compatibility)
builder.Services.AddSingleton<CallManager>(sp =>
{
    var phoneManager = sp.GetRequiredService<PhoneManagerService>();
    var config = sp.GetRequiredService<AppConfiguration>();
    
    // Return the first phone's CallManager
    if (config.Phones.Count == 0)
    {
        throw new InvalidOperationException("No phones configured in appsettings.json");
    }
    
    var firstPhone = phoneManager.GetPhone(config.Phones[0].Id);
    if (firstPhone == null)
    {
        throw new InvalidOperationException($"Failed to create CallManager for phone: {config.Phones[0].Id}");
    }
    
    return firstPhone;
});

// Register SIP diagnostics service (singleton + hosted for periodic INVITE timeout checks)
builder.Services.AddSingleton<SipDiagnosticService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SipDiagnosticService>());

builder.Services.AddGVTrunk(builder.Configuration);
builder.Services.AddGVBridge(builder.Configuration);

// Inter-service auth gate (ADR §6.5). Register the validator from the bound GVBridgeConfig (after
// AddGVBridge so IOptions<GVBridgeConfig> is available). Default-off when InterServiceAuthKey is empty.
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
        RotaryPhoneController.GVBridge.Models.GVBridgeConfig>>().Value;
    return new RotaryPhoneController.Server.Auth.InterServiceAuthValidator(cfg.InterServiceAuthKey);
});
builder.Services.AddSingleton<RotaryPhoneController.Server.Hubs.HubAuthFilter>();

// Bridge the GVBridge message-event seam (IGvMessageEventSource, registered by AddGVBridge) to
// RotaryHub so new inbound SMS/voicemail push to RadioConsole over the existing SignalR connection,
// mirroring the IncomingCall broadcast (ADR §6.3). Lives in the Server project because it needs
// IHubContext<RotaryHub>.
builder.Services.AddHostedService<RotaryPhoneController.Server.Services.GvMessagePushBridge>();

var app = builder.Build();

// Wire SIP diagnostic event: forward SIP messages from adapter to diagnostics service
var sipAdapter = app.Services.GetRequiredService<ISipAdapter>();
var sipDiagnostics = app.Services.GetRequiredService<SipDiagnosticService>();
if (sipAdapter is SIPSorceryAdapter sorceryAdapter)
{
    sorceryAdapter.OnSipMessageLogged += sipDiagnostics.HandleSipMessage;
}

// Initialize IBluetoothDeviceManager (starts bt_manager.py subprocess)
var deviceManager = app.Services.GetRequiredService<IBluetoothDeviceManager>();
await deviceManager.InitializeAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI();

// A bespoke CORS block for /api/gvbridge/event used to sit here, ahead of the general policy. It was
// removed on 2026-09-09 along with the matching auth-gate exemption: the browser-extension relay it
// served was deleted by design in March 2026 and no route for that path has existed since.
//
// Two reasons its removal is a security improvement, not just tidying:
//   1. It set Access-Control-Allow-Origin: * — a wildcard origin.
//   2. It matched on path.Contains("gvbridge/event") — a SUBSTRING test. The auth middleware was
//      hardened against exactly that in review MEDIUM-1 (anchoring to a segment boundary); this block
//      never got the same fix. So a sibling like /api/gvbridge/eventlog was correctly gated by auth
//      while still being handed wildcard CORS by this block.
//
// Unmatched /api/gvbridge/* paths now fall through to the /api/{**rest} 404 (see the fallback below).

// Enable CORS
app.UseCors("AllowClients");

// Inter-service auth gate (ADR §6.5): gate /api/gvbridge/* behind X-RotaryPhone-Auth when a key is
// configured. Runs AFTER UseCors (so preflight still works) and BEFORE the endpoints. Default-off:
// with no key it is a pass-through. No path is exempt — see the middleware's own doc comment for the
// /api/gvbridge/event carve-out that was removed on 2026-09-09 and why it must not come back.
app.UseMiddleware<RotaryPhoneController.Server.Middleware.GvBridgeAuthMiddleware>();

// Static Files - Defaults to wwwroot
app.UseStaticFiles();

// Map Controllers
app.MapControllers();

// Map SignalR Hub
app.MapHub<RotaryHub>("/hub");
app.MapGVTrunk();
app.MapGVBridge();

// An unmatched /api/* path must 404 as JSON, NOT fall through to the SPA shell below.
//
// Why this exists: a bare MapFallbackToFile served index.html — HTTP 200, text/html — to every
// unmatched /api/* request. A success code covering a failure. It cost a day of cross-repo
// debugging in Sep 2026 (docs/prompts/2026-09-09-radioconsole-ui11-was-never-ours.md), because a
// JSON caller probing a wrong path got 200 back and concluded the route existed.
//
// The body is the point. An empty 404 is what the caller could not act on; { error = ... } matches
// the shape already used across the GVBridge/GVTrunk controllers.
//
// ⚠ Do NOT replace this with UseStatusCodePagesWithReExecute("/not-found"). The .NET 10 template
// ships it and the docs steer you to it, but it re-executes into the Blazor/SPA pipeline and gives
// every /api/* 404 an HTML body — this same bug through the front door, with the tests still green.
//
// Precedence note: this wins over the SPA fallback by route-template specificity (the literal "api"
// segment beats the SPA's catch-all `{*path:nonfile}`), NOT by being registered first — both are
// registered at the same fallback order. Registering it first documents intent; it is not what
// makes it work. ApiFallbackRoutingTests pins the actual resulting behaviour in both directions.
app.MapFallback("/api/{**rest}", (HttpContext ctx) => Results.Json(
    new { error = $"No API route matches {ctx.Request.Method} {ctx.Request.Path}" },
    statusCode: StatusCodes.Status404NotFound));

// Fallback to React SPA in wwwroot/index.html
app.MapFallbackToFile("index.html");

app.Run();

// Normal shutdown. Explicit because the config-validation failure path above returns 1, which makes
// the implicit Main return int — every code path must then return a value.
return 0;
