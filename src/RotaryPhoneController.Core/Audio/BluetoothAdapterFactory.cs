using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Platform;

namespace RotaryPhoneController.Core.Audio;

/// <summary>
/// Factory for creating the appropriate Bluetooth HFP adapter based on the current platform.
/// </summary>
public static class BluetoothAdapterFactory
{
  /// <summary>
  /// Creates the appropriate IBluetoothHfpAdapter based on configuration and detected platform.
  /// </summary>
  public static IBluetoothHfpAdapter Create(
    AppConfiguration config,
    ILoggerFactory loggerFactory)
  {
    var platformLogger = loggerFactory.CreateLogger("BluetoothAdapterFactory");

    if (!config.UseActualBluetoothHfp)
    {
      var mockLogger = loggerFactory.CreateLogger<MockBluetoothHfpAdapter>();
      platformLogger.LogInformation(
        "Using MockBluetoothHfpAdapter (UseActualBluetoothHfp=false). Platform: {Platform}",
        PlatformDetector.PlatformDescription);
      return new MockBluetoothHfpAdapter(mockLogger);
    }

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
      PlatformType.Linux => CreateLinuxAdapter(config, loggerFactory, null),
#endif
      _ => throw new PlatformNotSupportedException(
        $"Bluetooth HFP is not supported on platform: {platform}. " +
        $"Detected platform: {PlatformDetector.PlatformDescription}. " +
        $"Set UseActualBluetoothHfp=false in configuration to use mock adapter.")
    };
  }

#if !WINDOWS
  /// <summary>
  /// Creates the adapter with BluetoothMgmtMonitor support (Linux only).
  /// </summary>
  public static IBluetoothHfpAdapter Create(
    AppConfiguration config,
    ILoggerFactory loggerFactory,
    BluetoothMgmtMonitor? mgmtMonitor)
  {
    var platformLogger = loggerFactory.CreateLogger("BluetoothAdapterFactory");

    if (!config.UseActualBluetoothHfp)
    {
      var mockLogger = loggerFactory.CreateLogger<MockBluetoothHfpAdapter>();
      platformLogger.LogInformation(
        "Using MockBluetoothHfpAdapter (UseActualBluetoothHfp=false). Platform: {Platform}",
        PlatformDetector.PlatformDescription);
      return new MockBluetoothHfpAdapter(mockLogger);
    }

    var platform = DeterminePlatform(config.ForcePlatform);

    platformLogger.LogInformation(
      "Creating Bluetooth adapter for platform: {Platform} (ForcePlatform={ForcePlatform})",
      platform,
      config.ForcePlatform ?? "auto-detect");

    return platform switch
    {
      PlatformType.Linux => CreateLinuxAdapter(config, loggerFactory, mgmtMonitor),
      _ => throw new PlatformNotSupportedException(
        $"Bluetooth HFP is not supported on platform: {platform}. " +
        $"Detected platform: {PlatformDetector.PlatformDescription}. " +
        $"Set UseActualBluetoothHfp=false in configuration to use mock adapter.")
    };
  }
#endif

  private static PlatformType DeterminePlatform(string? forcePlatform)
  {
    if (string.IsNullOrWhiteSpace(forcePlatform))
      return PlatformDetector.CurrentPlatform;

    if (Enum.TryParse<PlatformType>(forcePlatform, ignoreCase: true, out var platform))
      return platform;

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
    _ = InitializeAdapterAsync(adapter, logger);
    return adapter;
  }
#endif

#if !WINDOWS
  private static IBluetoothHfpAdapter CreateLinuxAdapter(
    AppConfiguration config,
    ILoggerFactory loggerFactory,
    BluetoothMgmtMonitor? mgmtMonitor)
  {
    var logger = loggerFactory.CreateLogger<BlueZHfpAdapter>();

    var platformInfo = PlatformDetector.IsRaspberryPi
      ? "Linux (Raspberry Pi)"
      : "Linux";

    logger.LogInformation("Creating BlueZHfpAdapter for {Platform} platform", platformInfo);

    var adapter = new BlueZHfpAdapter(logger, config, mgmtMonitor);
    _ = InitializeAdapterAsync(adapter, logger);
    return adapter;
  }
#endif

  private static async Task InitializeAdapterAsync<T>(T adapter, ILogger logger)
    where T : class
  {
    try
    {
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
