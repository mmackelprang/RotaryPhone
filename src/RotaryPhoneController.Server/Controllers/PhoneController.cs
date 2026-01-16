using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Platform;
using RotaryPhoneController.Core.HT801;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PhoneController : ControllerBase
{
    private readonly PhoneManagerService _phoneManager;
    private readonly ILogger<PhoneController> _logger;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly ISipAdapter _sipAdapter;
    private readonly AppConfiguration _config;
    private readonly IHT801ConfigService _ht801Service;

    public PhoneController(
        PhoneManagerService phoneManager,
        ILogger<PhoneController> logger,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        AppConfiguration config,
        IHT801ConfigService ht801Service)
    {
        _phoneManager = phoneManager;
        _logger = logger;
        _bluetoothAdapter = bluetoothAdapter;
        _sipAdapter = sipAdapter;
        _config = config;
        _ht801Service = ht801Service;
    }

    [HttpGet("status")]
    public IActionResult GetStatus([FromQuery] string? phoneId = null)
    {
        if (string.IsNullOrEmpty(phoneId))
        {
            // Default to first phone or return all?
            // For now, let's return a list of all phones status
            var statuses = _phoneManager.GetAllPhones().Select(p => new
            {
                Id = p.PhoneId,
                State = p.CallManager.CurrentState.ToString(),
                DialedNumber = p.CallManager.DialedNumber
            });
            return Ok(statuses);
        }

        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound($"Phone {phoneId} not found");

        return Ok(new 
        { 
            Id = phoneId, 
            State = manager.CurrentState.ToString(),
            DialedNumber = manager.DialedNumber
        });
    }

    [HttpPost("simulate/incoming")]
    public IActionResult SimulateIncoming([FromQuery] string phoneId = "default")
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.SimulateIncomingCall();
        return Ok("Incoming call simulated");
    }

    [HttpPost("simulate/hook")]
    public IActionResult SimulateHook([FromQuery] string phoneId = "default", [FromQuery] bool offHook = true)
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.HandleHookChange(offHook);
        return Ok($"Hook state set to {(offHook ? "OFF-HOOK" : "ON-HOOK")}");
    }
    
    [HttpPost("simulate/dial")]
    public IActionResult SimulateDial([FromQuery] string phoneId = "default", [FromQuery] string digits = "")
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.HandleDigitsReceived(digits);
        return Ok($"Digits '{digits}' received");
    }

    /// <summary>
    /// Gets the current system status including platform, Bluetooth, and SIP information
    /// </summary>
    [HttpGet("system-status")]
    public async Task<IActionResult> GetSystemStatus()
    {
        var status = new SystemStatus
        {
            Platform = PlatformDetector.CurrentPlatform.ToString(),
            IsRaspberryPi = PlatformDetector.IsRaspberryPi,
            BluetoothEnabled = _config.UseActualBluetoothHfp,
            BluetoothConnected = _bluetoothAdapter.IsConnected,
            BluetoothDeviceAddress = _bluetoothAdapter.ConnectedDeviceAddress,
            SipListening = _sipAdapter.IsListening,
            SipListenAddress = _config.SipListenAddress,
            SipPort = _config.SipPort
        };

        // Check HT801 status
        // We'll use the default phone's config for now
        var defaultPhoneId = _config.Phones.FirstOrDefault()?.Id ?? "default";
        var ht801Config = _ht801Service.GetConfig(defaultPhoneId);
        
        status.Ht801IpAddress = ht801Config.IpAddress;
        
        // Only check reachability if we have a valid IP
        if (!string.IsNullOrEmpty(ht801Config.IpAddress) && ht801Config.IpAddress != "0.0.0.0")
        {
            var result = await _ht801Service.TestConnectionAsync(ht801Config.IpAddress);
            status.Ht801Reachable = result.Success;
        }

        _logger.LogDebug("System status requested: Platform={Platform}, Bluetooth={BluetoothConnected}, SIP={SipListening}, HT801={Ht801Reachable}",
            status.Platform, status.BluetoothConnected, status.SipListening, status.Ht801Reachable);

        return Ok(status);
    }
}
