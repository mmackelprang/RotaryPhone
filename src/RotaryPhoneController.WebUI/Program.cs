using RotaryPhoneController.WebUI.Components;
using RotaryPhoneController.Core;
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

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register Core services as singletons
builder.Services.AddSingleton<ISipAdapter>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SIPSorceryAdapter>>();
    var serilogLogger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .CreateLogger();
    var adapter = new SIPSorceryAdapter(serilogLogger, "192.168.1.20", 5060);
    adapter.StartListening();
    return adapter;
});

builder.Services.AddSingleton<CallManager>(sp =>
{
    var sipAdapter = sp.GetRequiredService<ISipAdapter>();
    var logger = sp.GetRequiredService<ILogger<CallManager>>();
    var callManager = new CallManager(sipAdapter, logger);
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
