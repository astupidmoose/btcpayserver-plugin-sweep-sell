using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Controllers;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;
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
    public void RemoveWinsOverAutofilledKeyFields()
    {
        var settings = UISweepSellController.Apply(Saved(),
            new SweepSellSettingsViewModel { ExchangeId = "kraken", ClearCredentials = true, ApiKey = "autofilled", ApiSecret = "autofilled" }, Protector);
        Assert.False(settings.HasCredentials);
    }

    [Fact]
    public void SwitchingExchangeForgetsTheKey()
    {
        var settings = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel { ExchangeId = "other" }, Protector);
        Assert.False(settings.HasCredentials);
    }

    static readonly ExchangeRegistry Exchanges = new([new KrakenConnector(new HttpClient())]);

    [Fact]
    public void RemoveWorksWhenOnlyOneKeyFieldWasAutofilled()
    {
        Assert.Empty(UISweepSellController.Validate(
            new SweepSellSettingsViewModel { ExchangeId = "kraken", ClearCredentials = true, ApiKey = "autofilled" }, Exchanges));
        Assert.Single(UISweepSellController.Validate(new SweepSellSettingsViewModel { ExchangeId = "kraken", ApiKey = "only-one" }, Exchanges));
    }

    [Fact]
    public void KeyNeedsAnExchange()
    {
        var errors = UISweepSellController.Validate(new SweepSellSettingsViewModel { ApiKey = "my-key", ApiSecret = "my-secret" }, Exchanges);
        Assert.Equal(nameof(SweepSellSettingsViewModel.ExchangeId), Assert.Single(errors).Field);
    }

    [Fact]
    public void ConvertSettingsAreParsedAndSaved()
    {
        var settings = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel
        {
            ExchangeId = "kraken", ConvertEnabled = true, ConvertDryRun = false, ConvertAssets = "doge, LTC  doge;xmr", ConvertTarget = " btc ",
            ConvertIntervalMinutes = 30
        }, Protector);
        Assert.True(settings.Convert.Enabled);
        Assert.False(settings.Convert.DryRun);
        Assert.Equal(["DOGE", "LTC", "XMR"], settings.Convert.Assets);
        Assert.Equal("BTC", settings.Convert.TargetAsset);
        Assert.Equal(30, settings.Convert.IntervalMinutes);
        Assert.Equal("old-key", Protector.Credentials(settings)!.ApiKey); // the key is kept
    }

    [Theory]
    [InlineData("DOGE", "BTC", 15, true, null)]
    [InlineData("DO GE!", "BTC", 15, false, nameof(SweepSellSettingsViewModel.ConvertAssets))]
    [InlineData("DOGE, BTC", "BTC", 15, false, nameof(SweepSellSettingsViewModel.ConvertAssets))]
    [InlineData("DOGE", "", 15, false, nameof(SweepSellSettingsViewModel.ConvertTarget))]
    [InlineData("DOGE", "BTC", 1, false, nameof(SweepSellSettingsViewModel.ConvertIntervalMinutes))]
    [InlineData("", "BTC", 15, true, nameof(SweepSellSettingsViewModel.ConvertAssets))] // enabled with nothing to convert
    public void ConvertSettingsAreValidated(string assets, string target, int interval, bool enabled, string? errorField)
    {
        var errors = UISweepSellController.Validate(new SweepSellSettingsViewModel
        {
            ExchangeId = "kraken", ConvertEnabled = enabled, ConvertAssets = assets, ConvertTarget = target, ConvertIntervalMinutes = interval
        }, Exchanges).ToList();
        if (errorField is null)
            Assert.Empty(errors);
        else
            Assert.Contains(errors, e => e.Field == errorField);
    }

    static PendingOrder PendingWith(string exchangeId, string apiKey) => new()
    {
        ClientOrderId = Guid.NewGuid(),
        ExchangeId = exchangeId,
        KeyFingerprint = new ExchangeCredentials(apiKey, "unused").Fingerprint(),
        Asset = "DOGE",
        TargetAsset = "BTC",
        Amount = 300m
    };

    [Theory]
    [InlineData(false, "kraken", "new-key", false)] // nothing pending: anything goes
    [InlineData(true, "kraken", null, false)] // settings saved with the same key
    [InlineData(true, "kraken", "old-key", false)] // the original key typed again, e.g. after removing it
    [InlineData(true, "kraken", "new-key", true)] // another key can't look the order up
    [InlineData(true, "other", "old-key", true)] // nor can the same key name on another exchange
    public void PendingOrdersAllowOnlyTheirOwnKey(bool pending, string exchangeId, string? apiKey, bool blocked)
    {
        var updated = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel
        {
            ExchangeId = exchangeId, ApiKey = apiKey, ApiSecret = apiKey is null ? null : "a-secret"
        }, Protector);
        PendingOrder[] orders = pending ? [PendingWith("kraken", "old-key")] : [];
        Assert.Equal(blocked, UISweepSellController.PendingConflict(updated, orders, Protector) is not null);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SavesWithoutAKeyAreNeverBlockedByPendingOrders(bool clear, bool switchExchange)
    {
        // Removing the key, or switching exchange (which forgets it), leaves nothing to look orders up with.
        var updated = UISweepSellController.Apply(Saved(), new SweepSellSettingsViewModel
        {
            ExchangeId = switchExchange ? "other" : "kraken", ClearCredentials = clear
        }, Protector);
        Assert.False(UISweepSellController.KeyChanges(Saved(), updated));
        Assert.Null(UISweepSellController.PendingConflict(updated, [PendingWith("kraken", "old-key")], Protector));
    }

    [Theory]
    [InlineData(null, false)] // pausing or other settings
    [InlineData("old-key", true)] // retyped: checked again, and allowed
    [InlineData("new-key", true)]
    public void KeyChangesAreDetected(string? apiKey, bool changes)
    {
        var saved = Saved();
        var updated = UISweepSellController.Apply(saved, new SweepSellSettingsViewModel
        {
            ExchangeId = "kraken", Paused = true, ApiKey = apiKey, ApiSecret = apiKey is null ? null : "a-secret"
        }, Protector);
        Assert.Equal(changes, UISweepSellController.KeyChanges(saved, updated));
    }

    [Theory]
    [InlineData("2", "0", true)]
    [InlineData("0.05", "0", false)] // too tight to ever fill
    [InlineData("25", "0", false)] // too loose
    [InlineData("2", "-1", false)]
    public void PriceFloorAndCapAreValidated(string slippage, string cap, bool valid)
    {
        var errors = UISweepSellController.Validate(new SweepSellSettingsViewModel
        {
            ExchangeId = "kraken", ConvertAssets = "DOGE", ConvertTarget = "BTC",
            ConvertMaxSlippagePercent = decimal.Parse(slippage, System.Globalization.CultureInfo.InvariantCulture),
            ConvertDailyCap = decimal.Parse(cap, System.Globalization.CultureInfo.InvariantCulture)
        }, Exchanges);
        Assert.Equal(valid, !errors.Any());
    }

    [Fact]
    public void ConvertNeedsAnExchange()
    {
        var errors = UISweepSellController.Validate(new SweepSellSettingsViewModel { ConvertEnabled = true, ConvertAssets = "DOGE" }, Exchanges);
        Assert.Contains(errors, e => e.Field == nameof(SweepSellSettingsViewModel.ConvertEnabled));
    }

    [Fact]
    public void KeyWithoutAnExchangeIsNotSaved()
    {
        var settings = UISweepSellController.Apply(new SweepSellSettings(),
            new SweepSellSettingsViewModel { ApiKey = "my-key", ApiSecret = "my-secret" }, Protector);
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
