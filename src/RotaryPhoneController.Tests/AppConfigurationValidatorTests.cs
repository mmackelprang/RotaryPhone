using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

public class AppConfigurationValidatorTests
{
    [Fact]
    public void Validate_NoPhones_Throws()
    {
        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(new AppConfiguration()));

        Assert.Contains("RotaryPhone:Phones", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateIds_Throws()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.10" });
        config.Phones.Add(new RotaryPhoneConfig { Id = "DEFAULT", HT801IpAddress = "192.0.2.11" });

        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(config));

        Assert.Contains("Duplicate phone Id", ex.Message);
    }

    [Fact]
    public void Validate_SingleWellFormedPhone_DoesNotThrow()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.240" });

        AppConfigurationValidator.Validate(config);
    }
}
