using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Tests;

/// <summary>
/// Regression tests for the 2026-07 "UI says Ringing but the bell never rings" bug.
///
/// Root cause: AppConfiguration.Phones was pre-seeded with one element carrying a hardcoded HT801
/// address, and .NET's ConfigurationBinder APPENDS to a non-null List&lt;T&gt; instead of replacing
/// it. A single-phone config therefore produced TWO phones, and first-wins registration kept the
/// hardcoded one — so every INVITE went to a stale address that no config edit could change.
///
/// See docs/plans/ht801-address-resolution-and-config-binder-fix.md.
/// </summary>
public class ConfigurationBindingTests
{
    private const string ConfiguredIp = "192.0.2.240";

    private const string SinglePhoneJson = """
    {
      "RotaryPhone": {
        "SipPort": 5060,
        "RtpBasePort": 49000,
        "Phones": [
          {
            "Id": "default",
            "Name": "Rotary Phone",
            "HT801IpAddress": "192.0.2.240",
            "HT801Extension": "1000"
          }
        ]
      }
    }
    """;

    private static AppConfiguration BindSinglePhone()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SinglePhoneJson));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var appConfig = new AppConfiguration();
        configuration.GetSection("RotaryPhone").Bind(appConfig);
        return appConfig;
    }

    [Fact]
    public void Bind_SinglePhoneConfig_ProducesExactlyOnePhoneWithConfiguredAddress()
    {
        var appConfig = BindSinglePhone();

        Assert.Single(appConfig.Phones);
        Assert.Equal(ConfiguredIp, appConfig.Phones[0].HT801IpAddress);
    }

    [Fact]
    public void AppConfiguration_PhonesList_StartsEmpty()
    {
        // A non-empty default is APPENDED to by the binder, shadowing real configuration.
        Assert.Empty(new AppConfiguration().Phones);
    }

    [Fact]
    public void PhoneManagerService_RingsTheConfiguredAddress_NotACompiledDefault()
    {
        var appConfig = BindSinglePhone();
        var sipAdapter = new Mock<ISipAdapter>();
        // No registrar binding learned: resolution passes the configured address through, so this
        // test still asserts exactly what it always did — that the CONFIGURED address is rung,
        // not a value compiled into source.
        sipAdapter
            .Setup(x => x.ResolveHt801Address(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string configured, bool _) => configured);

        var manager = new PhoneManagerService(
            Mock.Of<ILogger<PhoneManagerService>>(),
            appConfig,
            sipAdapter.Object,
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<IRtpAudioBridge>(),
            Mock.Of<ILogger<CallManager>>());

        Assert.Equal(1, manager.PhoneCount);

        manager.GetPhone("default")!.SimulateIncomingCall();

        sipAdapter.Verify(x => x.SendInviteToHT801("1000", ConfiguredIp), Times.Once);
    }

    [Fact]
    public void PhoneManagerService_ThrowsOnDuplicatePhoneId_RatherThanDiscardingSilently()
    {
        var appConfig = new AppConfiguration();
        appConfig.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.22" });
        appConfig.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = ConfiguredIp });

        var ex = Assert.Throws<InvalidOperationException>(() => new PhoneManagerService(
            Mock.Of<ILogger<PhoneManagerService>>(),
            appConfig,
            Mock.Of<ISipAdapter>(),
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<IRtpAudioBridge>(),
            Mock.Of<ILogger<CallManager>>()));

        Assert.Contains("Duplicate phone Id", ex.Message);
    }
}
