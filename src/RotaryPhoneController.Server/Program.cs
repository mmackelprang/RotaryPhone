using RotaryPhoneController.Server.Hubs;
using RotaryPhoneController.Server.Services;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Contacts;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Configuration;
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

// Add CORS policy for Vite development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowViteDev", policy =>
    {
        policy.WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173",
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

    return new PhoneManagerService(
        logger, 
        config,
        sipAdapter,
        bluetoothAdapter,
        rtpBridge,
        callManagerLogger,
        callHistoryService);
});

// Register SignalR Notifier Service (Hosted Service)
builder.Services.AddHostedService<SignalRNotifierService>();

// Register Bluetooth HFP adapter (platform-aware factory pattern)
builder.Services.AddSingleton<IBluetoothHfpAdapter>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    return BluetoothAdapterFactory.Create(config, loggerFactory);
});

builder.Services.AddSingleton<IRtpAudioBridge>(sp =>
{
    var config = sp.GetRequiredService<AppConfiguration>();
    if (config.UseActualRtpAudioBridge)
    {
        var logger = sp.GetRequiredService<ILogger<RtpAudioBridge>>();
        return new RtpAudioBridge(logger);
    }
    else
    {
        var logger = sp.GetRequiredService<ILogger<MockRtpAudioBridge>>();
        return new MockRtpAudioBridge(logger);
    }
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
else
{
    // Enable Swagger in Development
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Enable CORS
app.UseCors("AllowViteDev");

// Static Files - Defaults to wwwroot
app.UseStaticFiles();

// Map Controllers
app.MapControllers();

// Map SignalR Hub
app.MapHub<RotaryHub>("/hub");

// Fallback to React SPA in wwwroot/index.html
app.MapFallbackToFile("index.html");

app.Run();
