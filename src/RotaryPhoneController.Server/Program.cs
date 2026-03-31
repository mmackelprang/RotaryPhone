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
using RotaryPhoneController.Core.Diagnostics;
using Microsoft.Extensions.Options;
using Serilog;

// CLI command: gv-login — extract GV cookies via Chrome CDP, then exit
if (args.Contains("gv-login"))
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
    var logger = loggerFactory.CreateLogger("GvLogin");

    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .Build();
    var gvConfig = config.GetSection("GVBridge");

    var cookiePath = gvConfig["CookieFilePath"] ?? "data/gv-cookies.enc";
    var keyPath = gvConfig["CookieKeyFilePath"] ?? "data/gv-key.bin";

    var result = await RotaryPhoneController.GVBridge.Auth.CookieRetriever.RetrieveAndSaveAsync(
        cookiePath, keyPath,
        msg => logger.LogInformation("{Message}", msg));

    if (result)
        logger.LogInformation("Cookie extraction successful. Start the server normally.");
    else
        logger.LogError("Cookie extraction failed. Ensure Chrome/Chromium is running and you are logged into voice.google.com.");

    return;
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

// Validate configuration
if (appConfig.Phones.Count == 0)
{
    Log.Warning("No phones configured, using default configuration");
    appConfig.Phones.Add(new RotaryPhoneConfig());
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();
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
    // Permissive policy for GV Bridge HTTP event endpoint (extension content scripts)
    options.AddPolicy("GVBridge", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register configuration as singleton
builder.Services.AddSingleton(appConfig);

// Register call history service if enabled
if (appConfig.EnableCallHistory)
{
    builder.Services.AddSingleton<ICallHistoryService>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<CallHistoryService>>();
        return new CallHistoryService(logger, appConfig.MaxCallHistoryEntries);
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
        adapterRegistry);
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
    
    var adapter = new SIPSorceryAdapter(serilogLogger, config);
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

// Handle CORS for GV Bridge event endpoint BEFORE the general CORS middleware
// (content script on voice.google.com POSTs call events to this endpoint)
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.Contains("gvbridge/event", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        context.Response.Headers["X-GVBridge-CORS"] = "handled";
        if (context.Request.Method == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            await context.Response.CompleteAsync();
            return;
        }
    }
    await next();
});

// Enable CORS
app.UseCors("AllowClients");

// Static Files - Defaults to wwwroot
app.UseStaticFiles();

// Map Controllers
app.MapControllers();

// Map SignalR Hub
app.MapHub<RotaryHub>("/hub");
app.MapGVTrunk();
app.MapGVBridge();

// Fallback to React SPA in wwwroot/index.html
app.MapFallbackToFile("index.html");

app.Run();
