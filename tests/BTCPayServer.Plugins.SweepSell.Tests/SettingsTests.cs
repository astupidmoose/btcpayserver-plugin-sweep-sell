using BTCPayServer.Plugins.SweepSell.Controllers;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class SettingsTests
{
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());

    static SweepSellSettings Saved() => new()
    {
        ExchangeId = "kraken",
        ProtectedApiKey = Protector.Protect("old-key"),
        ProtectedApiSecret = Protector.Protect("old-secret")
    };

    [Fact]
    public void SavedSettingsNeverContainThePlainKey()
    {
        var settings = UISweepSellController.Apply(new SweepSellSettings(),
            new SweepSellSettingsViewModel { ExchangeId = "kraken", ApiKey = "my-key", ApiSecret = "my-secret" }, Protector);
        var json = JsonConvert.SerializeObject(settings);
        Assert.DoesNotContain("my-key", json);
        Assert.DoesNotContain("my-secret", json);
        var credentials = Protector.Credentials(settings)!;
        Assert.Equal("my-key", credentials.ApiKey);
        Assert.Equal("my-secret", credentials.ApiSecret);
    }

    [Fact]
    public void EmptyFieldsKeepTheSavedKey()
    {
        var settings = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel { ExchangeId = "kraken", Paused = true }, Protector);
        Assert.True(settings.Paused);
        Assert.Equal("old-key", Protector.Credentials(settings)!.ApiKey);
    }

    [Fact]
    public void RemoveClearsTheKey()
    {
        var settings = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel { ExchangeId = "kraken", ClearCredentials = true }, Protector);
        Assert.False(settings.HasCredentials);
        Assert.Null(Protector.Credentials(settings));
    }

    [Fact]
    public void SwitchingExchangeForgetsTheKey()
    {
        var settings = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel { ExchangeId = "other" }, Protector);
        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void NewPairReplacesTheKey()
    {
        var settings = UISweepSellController.Apply(Saved(),
            new SweepSellSettingsViewModel { ExchangeId = "kraken", ApiKey = " new-key ", ApiSecret = "new-secret" }, Protector);
        Assert.Equal("new-key", Protector.Credentials(settings)!.ApiKey);
    }

    [Fact]
    public void KeyFromAnotherKeyRingReadsAsMissing()
    {
        var other = new SecretProtector(new EphemeralDataProtectionProvider());
        Assert.Null(other.Credentials(Saved()));
    }
}
