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

// Register CallManager for the first phone (for now, single phone support)
// Multi-phone support can be added by creating multiple CallManager instances
builder.Services.AddSingleton<CallManager>(sp =>
{
    var sipAdapter = sp.GetRequiredService<ISipAdapter>();
    var bluetoothAdapter = sp.GetRequiredService<IBluetoothHfpAdapter>();
    var rtpBridge = sp.GetRequiredService<IRtpAudioBridge>();
    var logger = sp.GetRequiredService<ILogger<CallManager>>();
    var callHistoryService = appConfig.EnableCallHistory 
        ? sp.GetRequiredService<ICallHistoryService>() 
        : null;
    
    var phoneConfig = appConfig.Phones[0]; // Use first phone for now
    var callManager = new CallManager(sipAdapter, bluetoothAdapter, rtpBridge, logger, phoneConfig, callHistoryService);
    callManager.Initialize();
    return callManager;
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
