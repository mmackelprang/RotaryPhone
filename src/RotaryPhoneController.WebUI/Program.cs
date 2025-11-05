using RotaryPhoneController.WebUI.Components;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.Debug()
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

// Register phone manager service
builder.Services.AddSingleton<PhoneManagerService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<PhoneManagerService>>();
    var callHistoryService = appConfig.EnableCallHistory 
        ? sp.GetRequiredService<ICallHistoryService>() 
        : null;
    return new PhoneManagerService(logger, callHistoryService);
});

// Register audio components
builder.Services.AddSingleton<IBluetoothHfpAdapter>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MockBluetoothHfpAdapter>>();
    return new MockBluetoothHfpAdapter(logger);
});

builder.Services.AddSingleton<IRtpAudioBridge>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MockRtpAudioBridge>>();
    return new MockRtpAudioBridge(logger);
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
// The PhoneManagerService can manage multiple phones
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
            phoneConfig);
    }
    
    // Return the first phone's CallManager for backward compatibility with existing UI
    var firstPhone = phoneManager.GetPhone(appConfig.Phones[0].Id);
    if (firstPhone == null)
    {
        throw new InvalidOperationException("Failed to create CallManager for first phone");
    }
    
    return firstPhone;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Ensure Serilog is properly disposed
Log.CloseAndFlush();
