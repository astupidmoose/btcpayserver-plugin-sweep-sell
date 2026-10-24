using BTCPayServer.Plugins.SweepSell.Controllers;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;
using BTCPayServer.Plugins.SweepSell.Payout;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class PayoutSettingsTests
{
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());
    static readonly WithdrawalRegistry Registry = new([new KrakenWithdrawals(new KrakenConnector(new HttpClient()))]);

    static PayoutSettings Saved() => new()
    {
        ExchangeId = "kraken",
        ProtectedApiKey = Protector.Protect("old-payout-key"),
        ProtectedApiSecret = Protector.Protect("old-payout-secret"),
        Asset = "USD",
        Destination = "test-bank"
    };

    static PayoutSettingsViewModel Valid() => new()
    {
        Enabled = true, ExchangeId = "kraken", Asset = "usd", Destination = " test-bank ", Minimum = 50m, DailyCap = 1000m, MaxFee = 1m,
        IntervalMinutes = 1440
    };

    static List<string> Errors(PayoutSettingsViewModel vm) => UISweepSellPayoutController.Validate(vm, Registry).Select(e => e.Field).ToList();

    [Fact]
    public void SavedPayoutSettingsNeverContainThePlainKey()
    {
        var vm = Valid();
        vm.ApiKey = "my-payout-key";
        vm.ApiSecret = "my-payout-secret";
        var settings = UISweepSellPayoutController.Apply(new PayoutSettings(), vm, Protector);
        var json = JsonConvert.SerializeObject(settings);
        Assert.DoesNotContain("my-payout-key", json);
        Assert.DoesNotContain("my-payout-secret", json);
        Assert.Equal("my-payout-key", Protector.Credentials(settings)!.ApiKey);
        Assert.Equal(("USD", "test-bank"), (settings.Asset, settings.Destination));
    }

    [Fact]
    public void KeyIsKeptRemovedOrForgottenWithTheExchange()
    {
        Assert.Equal("old-payout-key", Protector.Credentials(UISweepSellPayoutController.Apply(Saved(), Valid(), Protector))!.ApiKey);
        var clear = Valid();
        clear.ClearCredentials = true;
        clear.ApiKey = "autofilled";
        clear.ApiSecret = "autofilled";
        Assert.False(UISweepSellPayoutController.Apply(Saved(), clear, Protector).HasCredentials);
        var other = Valid();
        other.ExchangeId = null;
        Assert.False(UISweepSellPayoutController.Apply(Saved(), other, Protector).HasCredentials);
    }

    [Fact]
    public void NewSettingsStartInDryRun()
    {
        Assert.True(new PayoutSettings().DryRun);
        Assert.True(new PayoutSettingsViewModel().DryRun);
    }

    [Fact]
    public void EnabledPayoutNeedsADestinationCapAndMinimum()
    {
        Assert.Empty(Errors(Valid()));
        var vm = new PayoutSettingsViewModel { Enabled = true, IntervalMinutes = 1440 };
        var errors = Errors(vm);
        Assert.Contains(nameof(vm.Enabled), errors); // no exchange
        Assert.Contains(nameof(vm.Asset), errors);
        Assert.Contains(nameof(vm.Destination), errors);
        Assert.Contains(nameof(vm.DailyCap), errors);
        Assert.Contains(nameof(vm.Minimum), errors);
        // Turned off, an incomplete form can still be saved.
        Assert.Empty(Errors(new PayoutSettingsViewModel { IntervalMinutes = 1440 }));
    }

    [Theory]
    [InlineData("Asset", "U$D")]
    [InlineData("MaxFee", "-1")]
    [InlineData("KeepBalance", "-1")]
    [InlineData("IntervalMinutes", "5")]
    [InlineData("ExchangeId", "unknown")]
    public void InvalidFieldsAreRefused(string field, string value)
    {
        var vm = Valid();
        switch (field)
        {
            case "Asset": vm.Asset = value; break;
            case "MaxFee": vm.MaxFee = decimal.Parse(value); break;
            case "KeepBalance": vm.KeepBalance = decimal.Parse(value); break;
            case "IntervalMinutes": vm.IntervalMinutes = int.Parse(value); break;
            case "ExchangeId": vm.ExchangeId = value; break;
        }
        Assert.Contains(field, Errors(vm));
    }

    [Fact]
    public void PayoutKeyMustNotBeTheTradingKey()
    {
        var trading = new SweepSellSettings { ExchangeId = "kraken", ProtectedApiKey = Protector.Protect("same-key"), ProtectedApiSecret = Protector.Protect("s") };
        var payout = new PayoutSettings { ExchangeId = "kraken", ProtectedApiKey = Protector.Protect("same-key"), ProtectedApiSecret = Protector.Protect("t") };
        Assert.NotNull(UISweepSellPayoutController.SameKeyConflict(payout, trading, Protector));
        Assert.Null(UISweepSellPayoutController.SameKeyConflict(Saved(), trading, Protector));
        Assert.Null(UISweepSellPayoutController.SameKeyConflict(new PayoutSettings(), trading, Protector)); // no payout key
    }

    [Fact]
    public void PendingWithdrawalsKeepTheirKeyAndAsset()
    {
        var saved = Saved();
        var fingerprint = Protector.Credentials(saved)!.Fingerprint();
        List<PendingWithdrawal> pending = [new() { ExchangeId = "kraken", KeyFingerprint = fingerprint, Asset = "USD" }];
        Assert.Null(UISweepSellPayoutController.PendingConflict(saved, saved, pending, Protector));
        Assert.Null(UISweepSellPayoutController.PendingConflict(saved, new PayoutSettings { ExchangeId = "kraken", Asset = "USD" }, pending, Protector)); // key removed

        var newKey = UISweepSellPayoutController.Apply(saved, new PayoutSettingsViewModel { ExchangeId = "kraken", Asset = "USD", ApiKey = "new", ApiSecret = "new" }, Protector);
        Assert.NotNull(UISweepSellPayoutController.PendingConflict(saved, newKey, pending, Protector));
        var newAsset = UISweepSellPayoutController.Apply(saved, new PayoutSettingsViewModel { ExchangeId = "kraken", Asset = "EUR" }, Protector);
        Assert.NotNull(UISweepSellPayoutController.PendingConflict(saved, newAsset, pending, Protector));
        Assert.Null(UISweepSellPayoutController.PendingConflict(saved, newKey, [], Protector));
    }
}
