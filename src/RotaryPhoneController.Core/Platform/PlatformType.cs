namespace RotaryPhoneController.Core.Platform;

/// <summary>
/// Represents the supported platform types for the application
/// </summary>
public enum PlatformType
{
    /// <summary>
    /// Windows operating system (NUC device)
    /// </summary>
    Windows,

    /// <summary>
    /// Linux operating system (Raspberry Pi)
    /// </summary>
    Linux,

    /// <summary>
    /// Unknown or unsupported platform
    /// </summary>
    Unknown
}
