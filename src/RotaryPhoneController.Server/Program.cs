using RotaryPhoneController.Server.Hubs;
using RotaryPhoneController.Server.Services;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Contacts;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.GVTrunk.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog from appsettings
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

// Use Serilog
builder.Host.UseSerilog();

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

// Add CORS policy for development and Radio.Web integration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClients", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",   // Vite dev
                "http://127.0.0.1:5173",
                "http://localhost:5002",   // Radio.Web local
                "http://radio:5002",       // Radio.Web on Ubuntu
                "http://192.168.86.55:5173")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
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

    return new PhoneManagerService(
        logger,
        config,
        sipAdapter,
        bluetoothAdapter,
        rtpBridge,
        callManagerLogger,
        callHistoryService,
        deviceManager);
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

builder.Services.AddGVTrunk(builder.Configuration);

var app = builder.Build();

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

// Enable CORS
app.UseCors("AllowClients");

// Static Files - Defaults to wwwroot
app.UseStaticFiles();

// Map Controllers
app.MapControllers();

// Map SignalR Hub
app.MapHub<RotaryHub>("/hub");
app.MapGVTrunk();

// Fallback to React SPA in wwwroot/index.html
app.MapFallbackToFile("index.html");

app.Run();
