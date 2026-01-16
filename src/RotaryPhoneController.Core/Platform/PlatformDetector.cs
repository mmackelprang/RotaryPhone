using System.Runtime.InteropServices;

namespace RotaryPhoneController.Core.Platform;

/// <summary>
/// Provides runtime platform detection for cross-platform support
/// </summary>
public static class PlatformDetector
{
    private static PlatformType? _cachedPlatform;
    private static bool? _cachedIsRaspberryPi;

    /// <summary>
    /// Gets the current platform type, auto-detected at runtime
    /// </summary>
    public static PlatformType CurrentPlatform
    {
        get
        {
            if (_cachedPlatform.HasValue)
                return _cachedPlatform.Value;

            _cachedPlatform = DetectPlatform();
            return _cachedPlatform.Value;
        }
    }

    /// <summary>
    /// Gets whether the current device is a Raspberry Pi
    /// </summary>
    public static bool IsRaspberryPi
    {
        get
        {
            if (_cachedIsRaspberryPi.HasValue)
                return _cachedIsRaspberryPi.Value;

            _cachedIsRaspberryPi = DetectRaspberryPi();
            return _cachedIsRaspberryPi.Value;
        }
    }

    /// <summary>
    /// Gets a human-readable description of the current platform
    /// </summary>
    public static string PlatformDescription
    {
        get
        {
            var platform = CurrentPlatform.ToString();
            if (IsRaspberryPi)
                platform += " (Raspberry Pi)";
            return platform;
        }
    }

    private static PlatformType DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return PlatformType.Windows;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return PlatformType.Linux;

        return PlatformType.Unknown;
    }

    private static bool DetectRaspberryPi()
    {
        if (CurrentPlatform != PlatformType.Linux)
            return false;

        try
        {
            const string cpuInfoPath = "/proc/cpuinfo";
            if (!File.Exists(cpuInfoPath))
                return false;

            var cpuInfo = File.ReadAllText(cpuInfoPath);
            return cpuInfo.Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resets the cached platform detection (useful for testing)
    /// </summary>
    internal static void ResetCache()
    {
        _cachedPlatform = null;
        _cachedIsRaspberryPi = null;
    }
}
