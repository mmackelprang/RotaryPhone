using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Platform;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Factory for creating the appropriate Bluetooth HFP adapter based on the current platform
/// </summary>
public static class BluetoothAdapterFactory
{
    /// <summary>
    /// Creates the appropriate IBluetoothHfpAdapter based on configuration and detected platform
    /// </summary>
    /// <param name="config">Application configuration</param>
    /// <param name="loggerFactory">Logger factory for creating adapter loggers</param>
    /// <returns>The appropriate Bluetooth adapter for the current platform</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the platform is not supported</exception>
    public static IBluetoothHfpAdapter Create(
        AppConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var platformLogger = loggerFactory.CreateLogger("BluetoothAdapterFactory");

        // If Bluetooth is disabled, use mock adapter
        if (!config.UseActualBluetoothHfp)
        {
            var mockLogger = loggerFactory.CreateLogger<MockBluetoothHfpAdapter>();
            platformLogger.LogInformation(
                "Using MockBluetoothHfpAdapter (UseActualBluetoothHfp=false). Platform: {Platform}",
                PlatformDetector.PlatformDescription);
            return new MockBluetoothHfpAdapter(mockLogger);
        }

        // Determine platform (allow override for testing)
        var platform = DeterminePlatform(config.ForcePlatform);

        platformLogger.LogInformation(
            "Creating Bluetooth adapter for platform: {Platform} (ForcePlatform={ForcePlatform})",
            platform,
            config.ForcePlatform ?? "auto-detect");

        return platform switch
        {
#if WINDOWS
            PlatformType.Windows => CreateWindowsAdapter(config, loggerFactory),
#endif
#if !WINDOWS
            PlatformType.Linux => CreateLinuxAdapter(config, loggerFactory),
#endif
            _ => throw new PlatformNotSupportedException(
                $"Bluetooth HFP is not supported on platform: {platform}. " +
                $"Detected platform: {PlatformDetector.PlatformDescription}. " +
                $"Set UseActualBluetoothHfp=false in configuration to use mock adapter.")
        };
    }

    private static PlatformType DeterminePlatform(string? forcePlatform)
    {
        if (string.IsNullOrWhiteSpace(forcePlatform))
        {
            return PlatformDetector.CurrentPlatform;
        }

        if (Enum.TryParse<PlatformType>(forcePlatform, ignoreCase: true, out var platform))
        {
            return platform;
        }

        throw new ArgumentException(
            $"Invalid ForcePlatform value: '{forcePlatform}'. " +
            $"Valid values are: {string.Join(", ", Enum.GetNames<PlatformType>())}");
    }

#if WINDOWS
    private static IBluetoothHfpAdapter CreateWindowsAdapter(
        AppConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<WindowsBluetoothHfpAdapter>();
        logger.LogInformation("Creating WindowsBluetoothHfpAdapter for Windows platform");

        var adapter = new WindowsBluetoothHfpAdapter(logger, config);

        // Initialize asynchronously (fire and forget, but log errors)
        _ = InitializeAdapterAsync(adapter, logger);

        return adapter;
    }
#endif

#if !WINDOWS
    private static IBluetoothHfpAdapter CreateLinuxAdapter(
        AppConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<BlueZHfpAdapter>();

        var platformInfo = PlatformDetector.IsRaspberryPi
            ? "Linux (Raspberry Pi)"
            : "Linux";

        logger.LogInformation("Creating BlueZHfpAdapter for {Platform} platform", platformInfo);

        var adapter = new BlueZHfpAdapter(logger, config);

        // Initialize asynchronously (fire and forget, but log errors)
        _ = InitializeAdapterAsync(adapter, logger);

        return adapter;
    }
#endif

    private static async Task InitializeAdapterAsync<T>(T adapter, ILogger logger)
        where T : class
    {
        try
        {
            // Use reflection to call InitializeAsync if it exists
            var initMethod = adapter.GetType().GetMethod("InitializeAsync");
            if (initMethod != null)
            {
                var task = initMethod.Invoke(adapter, null) as Task;
                if (task != null)
                {
                    await task;
                    logger.LogInformation("Bluetooth adapter initialized successfully");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Bluetooth adapter. Bluetooth features may not work correctly.");
        }
    }
}
