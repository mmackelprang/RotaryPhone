namespace RotaryPhoneController.Core.Configuration;

/// <summary>
/// Thrown when the bound <see cref="AppConfiguration"/> is unusable. Startup treats this as fatal:
/// a missing or duplicated phone entry produces a service that reports "Ringing" while the bell
/// never rings — a far worse failure mode than refusing to start.
/// </summary>
public class ConfigurationValidationException(string message) : Exception(message);

public static class AppConfigurationValidator
{
    /// <summary>
    /// Validates bound configuration, throwing on the first problem with an actionable message.
    /// </summary>
    public static void Validate(AppConfiguration config)
    {
        if (config.Phones.Count == 0)
        {
            throw new ConfigurationValidationException(
                "No phones configured. Add at least one entry under \"RotaryPhone:Phones\" " +
                "(Id, Name, HT801IpAddress, HT801Extension) in appsettings.Production.json.");
        }

        var duplicateIds = config.Phones
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new ConfigurationValidationException(
                $"Duplicate phone Id(s) in \"RotaryPhone:Phones\": {string.Join(", ", duplicateIds)}. " +
                "Each phone must have a unique Id. Duplicates were previously discarded silently, " +
                "which routed calls to a stale HT801 address.");
        }

        foreach (var phone in config.Phones)
        {
            if (string.IsNullOrWhiteSpace(phone.HT801IpAddress))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has no HT801IpAddress. Set " +
                    $"\"RotaryPhone:Phones[].HT801IpAddress\" in appsettings.Production.json " +
                    "(see docs/HT801-ADDRESS.md).");
            }

            if (!System.Net.IPAddress.TryParse(phone.HT801IpAddress, out _))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has an unparseable HT801IpAddress " +
                    $"'{phone.HT801IpAddress}'. Expected a literal IPv4 address, e.g. 192.168.86.240.");
            }

            if (string.IsNullOrWhiteSpace(phone.HT801Extension))
            {
                throw new ConfigurationValidationException(
                    $"Phone '{phone.Id}' has no HT801Extension (the SIP extension to ring, e.g. 1000).");
            }
        }
    }
}
