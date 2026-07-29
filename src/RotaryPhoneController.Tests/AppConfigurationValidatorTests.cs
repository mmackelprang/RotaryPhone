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

    [Fact]
    public void Validate_EmptyHt801Address_Throws()
    {
        // The compiled-in default is now "" (PR2 Task 2.1), so a phone entry that omits the key
        // must be rejected at startup rather than silently ringing nothing.
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "" });

        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(config));

        Assert.Contains("has no HT801IpAddress", ex.Message);
        Assert.Contains("HT801-ADDRESS.md", ex.Message);
    }

    [Fact]
    public void Validate_UnparseableHt801Address_Throws()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "not-an-ip" });

        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(config));

        Assert.Contains("unparseable HT801IpAddress", ex.Message);
    }

    [Fact]
    public void Validate_EmptyHt801Extension_Throws()
    {
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig
        {
            Id = "default",
            HT801IpAddress = "192.0.2.240",
            HT801Extension = ""
        });

        var ex = Assert.Throws<ConfigurationValidationException>(
            () => AppConfigurationValidator.Validate(config));

        Assert.Contains("has no HT801Extension", ex.Message);
    }
}
