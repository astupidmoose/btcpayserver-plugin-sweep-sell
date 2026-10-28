#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.SweepSell.Exchanges;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// Copies one store's sweep, hedge and convert settings to another store. Every copied feature lands in dry run, the
/// target keeps its pause switch and (unless asked) its own exchange key, and payout is never copied: only one store may
/// pay out each asset of an exchange account.
/// </summary>
public static class SettingsCopy
{
    /// <summary>
    /// The target's settings after the copy, with notes on what was left out. <paramref name="targetCoins"/> are the coins
    /// the target store has a wallet for; <paramref name="isExtendedKey"/> tells a cold wallet given as an extended public
    /// key (coin, destination) from a fixed address.
    /// </summary>
    public static (SweepSellSettings Settings, List<string> Notes) CopyTo(SweepSellSettings source, SweepSellSettings target, bool copyKey,
        IReadOnlyCollection<string> targetCoins, Func<string, string, bool> isExtendedKey, DateTimeOffset now)
    {
        var notes = new List<string>();
        var rules = new List<SweepRule>();
        foreach (var rule in source.Sweep.Rules)
        {
            var code = rule.CryptoCode.ToUpperInvariant();
            if (!targetCoins.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                if (rule.Enabled)
                    notes.Add($"{code} isn't swept there: that store has no {code} wallet.");
                continue;
            }
            var copy = new SweepRule
            {
                CryptoCode = code,
                Enabled = rule.Enabled,
                MinimumBalance = rule.MinimumBalance,
                ColdPercent = rule.ColdPercent,
                ColdDestination = rule.ColdDestination,
                ColdStartIndex = rule.ColdStartIndex,
                DailyCap = rule.DailyCap,
                IncludeUnconfirmed = rule.IncludeUnconfirmed
            };
            // Each store counts its own addresses from an extended public key, so two stores would give out the same ones.
            if (!string.IsNullOrEmpty(copy.ColdDestination) && isExtendedKey(code, copy.ColdDestination))
            {
                if (copy.ColdPercent > 0)
                    notes.Add($"{code}'s cold wallet is an extended public key, so it wasn't copied (both stores would use the same addresses). Set one in that store; until then all of {code} goes to the exchange.");
                copy.ColdPercent = 0;
                copy.ColdDestination = null;
                copy.ColdStartIndex = 0;
            }
            rules.Add(copy);
        }
        var sweepEnabled = source.Sweep.Enabled && rules.Any(r => r.Enabled);
        if (source.Sweep.Enabled && !sweepEnabled)
            notes.Add("Sweeping is left off there: none of the coins it sweeps has a wallet in that store.");

        DateTimeOffset? hedgeSince = !source.Hedge.Enabled ? null
            : target.Hedge.Enabled && target.Hedge.EnabledSince is { } since ? since
            : now;
        var settings = new SweepSellSettings
        {
            Paused = target.Paused,
            ExchangeId = copyKey ? source.ExchangeId : target.ExchangeId,
            ProtectedApiKey = copyKey ? source.ProtectedApiKey : target.ProtectedApiKey,
            ProtectedApiSecret = copyKey ? source.ProtectedApiSecret : target.ProtectedApiSecret,
            Convert = new ConvertSettings
            {
                Enabled = source.Convert.Enabled,
                DryRun = true,
                Assets = source.Convert.Assets.ToList(),
                TargetAsset = source.Convert.TargetAsset,
                IntervalMinutes = source.Convert.IntervalMinutes,
                MaxSlippagePercent = source.Convert.MaxSlippagePercent,
                DailyCap = source.Convert.DailyCap
            },
            Sweep = new SweepSettings
            {
                Enabled = sweepEnabled,
                DryRun = true,
                IntervalMinutes = source.Sweep.IntervalMinutes,
                FeeTargetBlocks = source.Sweep.FeeTargetBlocks,
                Rules = rules
            },
            Hedge = new HedgeSettings
            {
                Enabled = source.Hedge.Enabled,
                DryRun = true,
                Trigger = source.Hedge.Trigger,
                Amount = source.Hedge.Amount,
                Markets = new Dictionary<string, string>(source.Hedge.Markets),
                DefaultMarket = source.Hedge.DefaultMarket,
                MaxSlippagePercent = source.Hedge.MaxSlippagePercent,
                DailyCap = source.Hedge.DailyCap,
                // Like turning it on there: invoices created before the copy are never hedged.
                EnabledSince = hedgeSince
            }
        };
        return (settings, notes);
    }

    /// <summary>
    /// Why the copied settings can't run in the target store, or null. A saved key that can't be decrypted (e.g. restored
    /// without this server's data-protection keys) counts as no key, as on the settings page, and so does a key for an
    /// exchange this server no longer supports.
    /// </summary>
    public static string? Problem(SweepSellSettings copied, SecretProtector protector, ExchangeRegistry exchanges)
    {
        var anyOn = copied.Convert.Enabled || copied.Sweep.Enabled || copied.Hedge.Enabled;
        if (anyOn && (exchanges.Find(copied.ExchangeId) is null || protector.Credentials(copied) is null))
            return "it has no exchange key. Tick \"Also copy the exchange key\", or save a key in that store first.";
        return null;
    }
}
