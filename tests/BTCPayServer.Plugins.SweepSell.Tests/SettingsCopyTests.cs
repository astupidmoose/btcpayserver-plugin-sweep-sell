using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.SweepSell.Tests;

public class SettingsCopyTests
{
    static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly string[] BtcAndLtc = ["BTC", "LTC"];
    static readonly SecretProtector Protector = new(new EphemeralDataProtectionProvider());

    // Invented values: a fixed cold address, and an extended public key standing in for one.
    const string FixedCold = "bcrt1qexamplecoldaddress";
    const string ColdXpub = "tpubExampleColdKey";
    static bool IsXpub(string code, string destination) => destination == ColdXpub;

    static SweepSellSettings Source() => new()
    {
        ExchangeId = "kraken",
        ProtectedApiKey = "protected-source-key",
        ProtectedApiSecret = "protected-source-secret",
        Convert = new ConvertSettings { Enabled = true, DryRun = false, Assets = ["DOGE", "LTC"], TargetAsset = "BTC", IntervalMinutes = 30, MaxSlippagePercent = 3, DailyCap = 0.5m },
        Hedge = new HedgeSettings
        {
            Enabled = true, DryRun = false, Trigger = HedgeTrigger.Processing, Markets = new() { ["GBP"] = "CAD" }, DefaultMarket = "CAD",
            MaxSlippagePercent = 1.5m, DailyCap = 0.2m, EnabledSince = Now.AddDays(-30)
        },
        Sweep = new SweepSettings
        {
            Enabled = true, DryRun = false, IntervalMinutes = 30, FeeTargetBlocks = 3,
            Rules =
            [
                new SweepRule { CryptoCode = "BTC", Enabled = true, MinimumBalance = 0.01m, ColdPercent = 20, ColdDestination = FixedCold, DailyCap = 1 },
                new SweepRule { CryptoCode = "LTC", Enabled = true, MinimumBalance = 1, ColdPercent = 10, ColdDestination = ColdXpub, ColdStartIndex = 5 },
                new SweepRule { CryptoCode = "DOGE", Enabled = true, MinimumBalance = 100 }
            ]
        }
    };

    static SweepSellSettings Target() => new()
    {
        Paused = true,
        ExchangeId = "kraken",
        ProtectedApiKey = "protected-target-key",
        ProtectedApiSecret = "protected-target-secret"
    };

    [Fact]
    public void EveryCopiedFeatureLandsInDryRun()
    {
        var (copy, _) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        Assert.True(copy.Convert is { Enabled: true, DryRun: true });
        Assert.True(copy.Hedge is { Enabled: true, DryRun: true });
        Assert.True(copy.Sweep is { Enabled: true, DryRun: true });
    }

    [Fact]
    public void TheOptionsAreCopied()
    {
        var (copy, _) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        Assert.Equal(["DOGE", "LTC"], copy.Convert.Assets);
        Assert.Equal((30, 3m, 0.5m), (copy.Convert.IntervalMinutes, copy.Convert.MaxSlippagePercent, copy.Convert.DailyCap));
        Assert.Equal(HedgeTrigger.Processing, copy.Hedge.Trigger);
        Assert.Equal("CAD", copy.Hedge.Markets["GBP"]);
        Assert.Equal((30, 3), (copy.Sweep.IntervalMinutes, copy.Sweep.FeeTargetBlocks));
        var btc = Assert.Single(copy.Sweep.Rules, r => r.CryptoCode == "BTC");
        Assert.Equal((0.01m, 20m, FixedCold, 1m), (btc.MinimumBalance, btc.ColdPercent, btc.ColdDestination, btc.DailyCap));
    }

    [Fact]
    public void TheTargetKeepsItsPauseAndKeyUnlessTheKeyIsCopied()
    {
        var (kept, _) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        Assert.True(kept.Paused);
        Assert.Equal("protected-target-key", kept.ProtectedApiKey);

        var (withKey, _) = SettingsCopy.CopyTo(Source(), Target(), true, BtcAndLtc, IsXpub, Now);
        Assert.True(withKey.Paused);
        Assert.Equal(("kraken", "protected-source-key", "protected-source-secret"), (withKey.ExchangeId, withKey.ProtectedApiKey, withKey.ProtectedApiSecret));
    }

    [Fact]
    public void ACopiedCollectionDoesNotShareTheSourcesLists()
    {
        var source = Source();
        var (copy, _) = SettingsCopy.CopyTo(source, Target(), false, BtcAndLtc, IsXpub, Now);
        copy.Convert.Assets.Add("XMR");
        copy.Hedge.Markets["EUR"] = "CAD";
        Assert.DoesNotContain("XMR", source.Convert.Assets);
        Assert.False(source.Hedge.Markets.ContainsKey("EUR"));
    }

    [Fact]
    public void AnExtendedKeyColdWalletIsNotCopied()
    {
        var (copy, notes) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        var ltc = Assert.Single(copy.Sweep.Rules, r => r.CryptoCode == "LTC");
        Assert.True(ltc.Enabled);
        Assert.Equal((0m, null, 0), (ltc.ColdPercent, ltc.ColdDestination, ltc.ColdStartIndex));
        Assert.Contains(notes, n => n.Contains("LTC's cold wallet is an extended public key"));
    }

    [Fact]
    public void ACoinWithoutAWalletInTheTargetIsLeftOut()
    {
        var (copy, notes) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        Assert.DoesNotContain(copy.Sweep.Rules, r => r.CryptoCode == "DOGE");
        Assert.Contains(notes, n => n.Contains("DOGE isn't swept there"));
    }

    [Fact]
    public void SweepingStaysOffWhenNoSweptCoinHasAWalletThere()
    {
        var (copy, notes) = SettingsCopy.CopyTo(Source(), Target(), false, ["XMR"], IsXpub, Now);
        Assert.False(copy.Sweep.Enabled);
        Assert.Contains(notes, n => n.Contains("Sweeping is left off"));
    }

    [Fact]
    public void HedgingThereCoversInvoicesFromTheCopyOn()
    {
        var (fresh, _) = SettingsCopy.CopyTo(Source(), Target(), false, BtcAndLtc, IsXpub, Now);
        Assert.Equal(Now, fresh.Hedge.EnabledSince);

        var hedging = Target();
        hedging.Hedge = new HedgeSettings { Enabled = true, EnabledSince = Now.AddDays(-2) };
        var (kept, _) = SettingsCopy.CopyTo(Source(), hedging, false, BtcAndLtc, IsXpub, Now);
        Assert.Equal(Now.AddDays(-2), kept.Hedge.EnabledSince);
    }

    [Fact]
    public void FeaturesCantBeCopiedToAStoreWithoutAKey()
    {
        var source = Source();
        source.ProtectedApiKey = Protector.Protect("source-key");
        source.ProtectedApiSecret = Protector.Protect("source-secret");
        var keyless = new SweepSellSettings();
        var (copy, _) = SettingsCopy.CopyTo(source, keyless, false, BtcAndLtc, IsXpub, Now);
        Assert.Contains("no exchange key", SettingsCopy.Problem(copy, Protector));

        var (withKey, _) = SettingsCopy.CopyTo(source, keyless, true, BtcAndLtc, IsXpub, Now);
        Assert.Null(SettingsCopy.Problem(withKey, Protector));
    }

    [Fact]
    public void AKeyThatCantBeDecryptedCountsAsNoKey()
    {
        // Saved, but encrypted with keys this server no longer has.
        var unreadable = new SweepSellSettings { ExchangeId = "kraken", ProtectedApiKey = "lost-key", ProtectedApiSecret = "lost-secret" };
        var (copy, _) = SettingsCopy.CopyTo(Source(), unreadable, false, BtcAndLtc, IsXpub, Now);
        Assert.True(copy.HasCredentials);
        Assert.Contains("no exchange key", SettingsCopy.Problem(copy, Protector));
    }
}
