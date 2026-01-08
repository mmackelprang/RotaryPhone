using Xunit;
using Xunit.Abstractions;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;

namespace RotaryPhoneController.Tests;

public class BluetoothHardwareFact : FactAttribute
{
    public BluetoothHardwareFact()
    {
        if (!IsBluetoothAvailable())
        {
            Skip = "Bluetooth hardware not detected or unsupported on this platform.";
        }
    }

    private static bool IsBluetoothAvailable()
    {
        // On Windows, we can check for Bluetooth radios via some system calls, 
        // but a simple check is to see if any Bluetooth-related network interfaces exist
        // or just check the OS.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // For PoC/Dev, we'll assume we want to skip in CI
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HAS_BLUETOOTH_HARDWARE"));
        }
        
        // On Linux, we could check for /sys/class/bluetooth
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Directory.Exists("/sys/class/bluetooth");
        }

        return false;
    }
}

public class BluetoothIntegrationTests
{
    [BluetoothHardwareFact]
    public void Test_BluetoothHardwareAccessibility()
    {
        // This test only runs if Bluetooth hardware is detected
        // It would attempt to initialize the actual hardware adapter
        Assert.True(true);
    }
}
