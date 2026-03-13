using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core.Audio;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/bluetooth")]
public class BluetoothController : ControllerBase
{
    private readonly IBluetoothDeviceManager _deviceManager;

    public BluetoothController(IBluetoothDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(new
        {
            paired = _deviceManager.PairedDevices,
            connected = _deviceManager.ConnectedDevices,
            adapterReady = _deviceManager.IsAdapterReady,
            adapterAddress = _deviceManager.AdapterAddress
        });
    }

    [HttpPost("discovery/start")]
    public async Task<IActionResult> StartDiscovery()
    {
        await _deviceManager.StartDiscoveryAsync();
        return Ok();
    }

    [HttpPost("discovery/stop")]
    public async Task<IActionResult> StopDiscovery()
    {
        await _deviceManager.StopDiscoveryAsync();
        return Ok();
    }

    [HttpPost("pair")]
    public async Task<IActionResult> PairDevice([FromBody] DeviceAddressRequest request)
    {
        var result = await _deviceManager.PairDeviceAsync(request.Address);
        return result ? Ok() : BadRequest("Pairing failed");
    }

    [HttpDelete("devices/{address}")]
    public async Task<IActionResult> RemoveDevice(string address)
    {
        var result = await _deviceManager.RemoveDeviceAsync(address);
        return result ? Ok() : BadRequest("Remove failed");
    }

    [HttpPost("pairing/confirm")]
    public async Task<IActionResult> ConfirmPairing([FromBody] PairingConfirmRequest request)
    {
        var result = await _deviceManager.ConfirmPairingAsync(request.Address, request.Accept);
        return result ? Ok() : BadRequest("Confirmation failed");
    }

    [HttpPut("adapter")]
    public async Task<IActionResult> SetAdapter([FromBody] AdapterConfigRequest request)
    {
        var result = await _deviceManager.SetAdapterAsync(request.Alias, request.Discoverable);
        return result ? Ok() : BadRequest("Adapter config failed");
    }

    [HttpPost("devices/{address}/connect")]
    public async Task<IActionResult> ConnectDevice(string address)
    {
        var result = await _deviceManager.ConnectDeviceAsync(address);
        return result ? Ok() : BadRequest("Connect failed");
    }

    [HttpPost("devices/{address}/disconnect")]
    public async Task<IActionResult> DisconnectDevice(string address)
    {
        var result = await _deviceManager.DisconnectDeviceAsync(address);
        return result ? Ok() : BadRequest("Disconnect failed");
    }
}

public record DeviceAddressRequest(string Address);
public record PairingConfirmRequest(string Address, bool Accept);
public record AdapterConfigRequest(string? Alias, bool? Discoverable);
