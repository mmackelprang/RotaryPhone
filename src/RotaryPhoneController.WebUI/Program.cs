using RotaryPhoneController.WebUI.Components;
using RotaryPhoneController.WebUI.Hubs;
using RotaryPhoneController.WebUI.Services;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Contacts;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Configuration;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Debug()
    .WriteTo.File(
        path: "logs/rotary-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

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
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Register existing Blazor components (temporary)
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
    return new PhoneManagerService(logger, callHistoryService);
});

// Register SignalR Notifier Service (Hosted Service)
builder.Services.AddHostedService<SignalRNotifierService>();

// Register audio components (based on configuration)
builder.Services.AddSingleton<IBluetoothHfpAdapter>(sp =>
{
    if (appConfig.UseActualBluetoothHfp)
    {
        var logger = sp.GetRequiredService<ILogger<BlueZHfpAdapter>>();
        var adapter = new BlueZHfpAdapter(logger, appConfig.BluetoothDeviceName);
        // Initialize the adapter asynchronously
        _ = adapter.InitializeAsync();
        return adapter;
    }
    else
    {
        var logger = sp.GetRequiredService<ILogger<MockBluetoothHfpAdapter>>();
        return new MockBluetoothHfpAdapter(logger);
    }
});

builder.Services.AddSingleton<IRtpAudioBridge>(sp =>
{
    if (appConfig.UseActualRtpAudioBridge)
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
    var serilogLogger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .CreateLogger();
    var adapter = new SIPSorceryAdapter(serilogLogger, appConfig.SipListenAddress, appConfig.SipPort);
    adapter.StartListening();
    return adapter;
});

// Register CallManager for the first phone (backward compatibility)
builder.Services.AddSingleton<CallManager>(sp =>
{
    var phoneManager = sp.GetRequiredService<PhoneManagerService>();
    var sipAdapter = sp.GetRequiredService<ISipAdapter>();
    var bluetoothAdapter = sp.GetRequiredService<IBluetoothHfpAdapter>();
    var rtpBridge = sp.GetRequiredService<IRtpAudioBridge>();
    var logger = sp.GetRequiredService<ILogger<CallManager>>();
    
    // Register all configured phones
    foreach (var phoneConfig in appConfig.Phones)
    {
        phoneManager.RegisterPhone(
            phoneConfig.Id,
            sipAdapter,
            bluetoothAdapter,
            rtpBridge,
            logger,
            phoneConfig,
            appConfig.RtpBasePort);
    }
    
    // Return the first phone's CallManager
    if (appConfig.Phones.Count == 0)
    {
        throw new InvalidOperationException("No phones configured in appsettings.json");
    }
    
    var firstPhone = phoneManager.GetPhone(appConfig.Phones[0].Id);
    if (firstPhone == null)
    {
        throw new InvalidOperationException($"Failed to create CallManager for phone: {appConfig.Phones[0].Id}");
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

app.UseAntiforgery();

app.UseStaticFiles();

app.MapStaticAssets();

// Map Controllers
app.MapControllers();

// Map SignalR Hub
app.MapHub<RotaryHub>("/hub");

// Map Blazor Components (Legacy)
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Fallback to React SPA
app.MapFallbackToFile("index.html");

app.Run();

// Ensure Serilog is properly disposed
Log.CloseAndFlush();