#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Hedging;
using BTCPayServer.Plugins.SweepSell.Payout;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Plugins.SweepSell.Sweeping;
using BTCPayServer.Plugins.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Controllers;

[Route("stores/{storeId}/sweep-sell")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISweepSellController(ISweepSellRepository repository, IPayoutRepository payouts, SecretProtector protector, ExchangeRegistry exchanges,
    ConvertService convert, SweepService sweep, IStoreWallets wallets, HedgeService hedge, IAuthorizationService authorization, TimeProvider clock,
    ILogger<UISweepSellController> logger)
    : Controller
{
    public const string SettingsView = SweepSellPlugin.ViewsDirectory + "/Settings.cshtml";

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId, CancellationToken cancellationToken)
    {
        return View(SettingsView, await ToViewModelAsync(storeId, await repository.GetAsync(storeId), null, cancellationToken));
    }

    public const string NoSpendPermission =
        "Sweeping spends from the store's wallets: changing it, or the exchange key it sends to, needs permission to sign and broadcast wallet transactions in this store.";

    /// <summary>
    /// Whether the user may spend from the store's wallets, as BTCPay asks before signing and broadcasting from them.
    /// Sweep settings and anything that decides where sweeps go need it, not just the store settings permission.
    /// </summary>
    async Task<bool> CanSpendAsync(string storeId) =>
        (await authorization.AuthorizeAsync(User, storeId, WalletPolicies.CanSignWalletTransactions)).Succeeded &&
        (await authorization.AuthorizeAsync(User, storeId, WalletPolicies.CanBroadcastWalletTransactions)).Succeeded;

    /// <summary>
    /// Whether a save changes what sweeps do: any sweep setting, the exchange or key while sweeping is (or becomes) on, since
    /// sweeps send to that exchange account, or lifting the pause while sweeping is on, which lets sweeps run again.
    /// Pausing is never a change: anyone who may change the settings may stop sweeps.
    /// </summary>
    public static bool ChangesSweeps(SweepSellSettings saved, SweepSellSettings updated) =>
        !SameSweeps(saved.Sweep, updated.Sweep) || (saved.Sweep.Enabled || updated.Sweep.Enabled) && KeyChanges(saved, updated) ||
        saved.Paused && !updated.Paused && updated.Sweep.Enabled;

    static bool SameSweeps(SweepSettings a, SweepSettings b)
    {
        // A coin without a rule and one with an untouched, disabled rule sweep the same: the page lists every wallet's coin.
        static Dictionary<string, SweepRule> Rules(SweepSettings s) => s.Rules
            .Where(r => r.Enabled || r.MinimumBalance != 0 || r.ColdPercent != 0 || !string.IsNullOrEmpty(r.ColdDestination) ||
                        r.ColdStartIndex != 0 || r.DailyCap != 0 || r.IncludeUnconfirmed)
            .GroupBy(r => r.CryptoCode.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var (ra, rb) = (Rules(a), Rules(b));
        return a.Enabled == b.Enabled && a.DryRun == b.DryRun && a.IntervalMinutes == b.IntervalMinutes && a.FeeTargetBlocks == b.FeeTargetBlocks &&
               ra.Count == rb.Count && ra.All(r => rb.TryGetValue(r.Key, out var o) && r.Value.Enabled == o.Enabled &&
                                                 r.Value.MinimumBalance == o.MinimumBalance && r.Value.ColdPercent == o.ColdPercent &&
                                                 r.Value.ColdDestination == o.ColdDestination && r.Value.ColdStartIndex == o.ColdStartIndex &&
                                                 r.Value.DailyCap == o.DailyCap && r.Value.IncludeUnconfirmed == o.IncludeUnconfirmed);
    }

    [HttpPost("")]
    public async Task<IActionResult> Settings(string storeId, SweepSellSettingsViewModel vm, CancellationToken cancellationToken)
    {
        foreach (var (field, error) in Validate(vm, exchanges).Concat(ValidateSweep(vm, wallets)).Concat(ValidateHedge(vm)))
            ModelState.AddModelError(field, error);
        if (!ModelState.IsValid)
            return View(SettingsView, await ToViewModelAsync(storeId, await repository.GetAsync(storeId), vm, cancellationToken));

        var canSpend = await CanSpendAsync(storeId);
        // Read, checked and saved while no order is being sent: a run checks the saved settings again under this lock before
        // each order, so once this returns no order goes out under the old settings (e.g. after pausing), and the pending
        // orders and settings seen here include every order and save made so far.
        var (saved, updated, conflict) = await convert.WithSettingsLockAsync(storeId, async () =>
        {
            var current = await repository.GetAsync(storeId);
            var next = Apply(current, vm, protector, clock.GetUtcNow());
            if (!canSpend && ChangesSweeps(current, next))
                return (current, next, NoSpendPermission);
            var pending = (await repository.GetConvertStateAsync(storeId)).Pending.Concat((await repository.GetHedgeStateAsync(storeId)).Pending).ToList();
            if (KeyChanges(current, next) && (PayoutKeyConflict(next, await payouts.GetAsync(storeId), protector) ?? PendingConflict(next, pending, protector)) is { } error)
                return (current, next, error);
            await repository.SaveAsync(storeId, next);
            return (current, next, (string?)null);
        }, cancellationToken);
        if (conflict is not null)
        {
            ModelState.AddModelError(conflict == NoSpendPermission ? nameof(vm.SweepEnabled) : nameof(vm.ExchangeId), conflict);
            return View(SettingsView, await ToViewModelAsync(storeId, saved, vm, cancellationToken));
        }

        // Not cancelled with the request: the settings are saved, so the record of the change must be too.
        await sweep.RecordDestinationChangesAsync(storeId, saved.Sweep, updated.Sweep, CancellationToken.None);

        // Never log the key itself; whether one is saved is enough to follow what happened.
        logger.LogInformation("Sweep & Sell settings of store {StoreId} saved: exchange {Exchange}, credentials {Credentials}, paused {Paused}, " +
                              "convert {Convert} ({ConvertMode}, {ConvertAssets} to {ConvertTarget} every {ConvertInterval} min), " +
                              "sweep {Sweep} ({SweepMode}, coins {SweepCoins}), hedge {Hedge} ({HedgeMode}, at {HedgeTrigger})",
            storeId, updated.ExchangeId ?? "none", updated.HasCredentials ? "saved" : "none", updated.Paused,
            updated.Convert.Enabled ? "on" : "off", updated.Convert.DryRun ? "dry run" : "live", string.Join(",", updated.Convert.Assets),
            updated.Convert.TargetAsset, updated.Convert.IntervalMinutes,
            updated.Sweep.Enabled ? "on" : "off", updated.Sweep.DryRun ? "dry run" : "live",
            string.Join(",", updated.Sweep.Rules.Where(r => r.Enabled).Select(r => r.CryptoCode)),
            updated.Hedge.Enabled ? "on" : "off", updated.Hedge.DryRun ? "dry run" : "live", updated.Hedge.Trigger);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Sweep & Sell settings saved."
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestCredentials(string storeId, CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(storeId);
        var exchange = exchanges.Find(settings.ExchangeId);
        var credentials = protector.Credentials(settings);
        StatusMessageModel message;
        if (exchange is null || credentials is null)
        {
            message = new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Save an exchange and its API key first."
            };
        }
        else
        {
            CredentialsCheck check;
            try
            {
                check = await exchange.TestCredentialsAsync(credentials, cancellationToken);
            }
            catch (Exception e) when (e is ExchangeException or System.Net.Http.HttpRequestException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
            {
                // Exchange and network errors carry no credentials.
                logger.LogWarning("Sweep & Sell key test for store {StoreId} on {Exchange} could not run: {Error}", storeId, exchange.Id, e.Message);
                check = new CredentialsCheck([new PermissionCheck("Connection", false, e.Message)]);
            }
            // Exchange error texts carry no credentials; log them so failures show up in the server logs too.
            foreach (var failed in check.Checks.Where(c => !c.Ok))
                logger.LogWarning("Sweep & Sell key test failed for store {StoreId} on {Exchange}: {Permission}: {Error}",
                    storeId, exchange.Id, failed.Permission, failed.Error);
            message = new StatusMessageModel
            {
                Severity = check.AllOk ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Error,
                Message = $"{exchange.Name}: " + string.Join(" ", check.Checks.Select(c => c.Ok ? $"{c.Permission}: OK." : $"{c.Permission}: failed ({c.Error})."))
            };
        }
        TempData.SetStatusMessageModel(message);
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("convert/run")]
    public async Task<IActionResult> RunConvert(string storeId, CancellationToken cancellationToken)
    {
        var result = await convert.RunAsync(storeId, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = result == ConvertRunResult.Ran ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = result switch
            {
                ConvertRunResult.Ran => "Convert ran. What it did is listed under Recent activity.",
                ConvertRunResult.Paused => "Sweep & Sell is paused for this store; nothing was converted.",
                ConvertRunResult.Disabled => "Turn on Convert at the exchange and save first.",
                _ => "A convert run is already in progress. Try again in a moment."
            }
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("convert/pending/{clientOrderId:guid}/remove")]
    public async Task<IActionResult> RemovePending(string storeId, Guid clientOrderId, CancellationToken cancellationToken)
    {
        var removed = await convert.RemovePendingAsync(storeId, clientOrderId, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = removed ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = removed ? "Pending order removed. Selling resumes on the next run." : "That order is no longer pending."
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("sweep/run")]
    public async Task<IActionResult> RunSweep(string storeId, CancellationToken cancellationToken)
    {
        if (!await CanSpendAsync(storeId))
            return Forbid();
        var result = await sweep.RunAsync(storeId, wait: false, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = result == SweepRunResult.Ran ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = result switch
            {
                SweepRunResult.Ran => "Sweep ran. What it did is listed under Sweep activity.",
                SweepRunResult.Paused => "Sweep & Sell is paused for this store; nothing was swept.",
                SweepRunResult.Disabled => "Turn on Sweep and save first.",
                _ => "A sweep run is already in progress. Try again in a moment."
            }
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("sweep/pending/{txid}/remove")]
    public async Task<IActionResult> RemovePendingSweep(string storeId, string txid, CancellationToken cancellationToken)
    {
        if (!await CanSpendAsync(storeId))
            return Forbid();
        var removed = await sweep.RemovePendingAsync(storeId, txid, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = removed ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = removed ? "Pending sweep removed. Sweeping that coin resumes on the next run." : "That sweep is no longer pending."
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("sweep/stopped/{cryptoCode}/resume")]
    public async Task<IActionResult> ResumeSweep(string storeId, string cryptoCode, CancellationToken cancellationToken)
    {
        if (!await CanSpendAsync(storeId))
            return Forbid();
        var resumed = await sweep.ResumeCoinAsync(storeId, cryptoCode, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = resumed ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = resumed ? $"Sweeping {cryptoCode.ToUpperInvariant()} resumes on the next run." : "That coin isn't stopped."
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("hedge/run")]
    public async Task<IActionResult> RunHedge(string storeId, CancellationToken cancellationToken)
    {
        var result = await hedge.RunAsync(storeId, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = result == HedgeRunResult.Ran ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = result switch
            {
                HedgeRunResult.Ran => "Hedge ran. What it did is listed under Hedge activity.",
                HedgeRunResult.Paused => "Sweep & Sell is paused for this store; nothing was hedged.",
                HedgeRunResult.Disabled => "Turn on Hedge on payment and save first.",
                _ => "A hedge run is already in progress. Try again in a moment."
            }
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("hedge/pending/{clientOrderId:guid}/remove")]
    public async Task<IActionResult> RemovePendingHedge(string storeId, Guid clientOrderId, CancellationToken cancellationToken)
    {
        var removed = await hedge.RemovePendingAsync(storeId, clientOrderId, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = removed ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = removed ? "Pending order removed and counted as sold. Hedging resumes on the next run." : "That order is no longer pending."
        });
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    /// <summary>
    /// Whether a save puts a different key (or exchange) in place, which pending orders must be checked against.
    /// Removing the key isn't a change here: with no key nothing runs, and removing it must never be blocked.
    /// </summary>
    public static bool KeyChanges(SweepSellSettings saved, SweepSellSettings updated) =>
        updated.HasCredentials && (updated.ExchangeId != saved.ExchangeId || updated.ProtectedApiKey != saved.ProtectedApiKey ||
                                   updated.ProtectedApiSecret != saved.ProtectedApiSecret);

    /// <summary>The payout key can withdraw, so it may never be saved as the trading key too. Null when the save is fine.</summary>
    public static string? PayoutKeyConflict(SweepSellSettings updated, PayoutSettings payout, SecretProtector protector) =>
        UISweepSellPayoutController.SameKey(payout, updated, protector)
            ? "This is the store's payout key, which can withdraw. Trading needs its own key, without Withdraw Funds."
            : null;

    /// <summary>
    /// A pending order can only be looked up with the exchange and key it was sent with, so a save may put only that key
    /// in place (for example the original key again after removing it). Null when the save is fine.
    /// </summary>
    public static string? PendingConflict(SweepSellSettings updated, IReadOnlyCollection<PendingOrder> pending, SecretProtector protector)
    {
        if (pending.Count == 0 || protector.Credentials(updated) is not { } credentials)
            return null;
        var fingerprint = credentials.Fingerprint();
        return pending.Any(p => p.ExchangeId != updated.ExchangeId || p.KeyFingerprint != fingerprint)
            ? "Orders sent with another exchange key are still pending. Save that key again so they can be checked, or check them in your exchange and remove them under Pending orders, before using a different exchange or key."
            : null;
    }

    static readonly Regex Ticker = new("^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant);

    /// <summary>Form errors, by field. Removing the key never depends on what was typed (or autofilled) in the key fields.</summary>
    public static IEnumerable<(string Field, string Error)> Validate(SweepSellSettingsViewModel vm, ExchangeRegistry exchanges)
    {
        if (!string.IsNullOrEmpty(vm.ExchangeId) && exchanges.Find(vm.ExchangeId) is null)
            yield return (nameof(vm.ExchangeId), "Pick an exchange from the list.");

        var assets = Tickers(vm.ConvertAssets);
        var target = Clean(vm.ConvertTarget)?.ToUpperInvariant();
        if (assets.FirstOrDefault(a => !Ticker.IsMatch(a)) is { } badAsset)
            yield return (nameof(vm.ConvertAssets), $"\"{badAsset}\" isn't a ticker. Use letters and digits, e.g. DOGE, LTC.");
        if (target is null || !Ticker.IsMatch(target))
            yield return (nameof(vm.ConvertTarget), "Enter the ticker to convert to, e.g. BTC.");
        else if (assets.Contains(target))
            yield return (nameof(vm.ConvertAssets), $"{target} is what you convert to; it can't also be converted.");
        if (vm.ConvertIntervalMinutes is < ConvertSettings.MinIntervalMinutes or > ConvertSettings.MaxIntervalMinutes)
            yield return (nameof(vm.ConvertIntervalMinutes),
                $"Check between every {ConvertSettings.MinIntervalMinutes} and {ConvertSettings.MaxIntervalMinutes} minutes.");
        if (vm.ConvertMaxSlippagePercent is < ConvertSettings.MinSlippageLimit or > ConvertSettings.MaxSlippageLimit)
            yield return (nameof(vm.ConvertMaxSlippagePercent),
                $"Allow between {ConvertSettings.MinSlippageLimit}% and {ConvertSettings.MaxSlippageLimit}% below the 24-hour average.");
        if (vm.ConvertDailyCap < 0)
            yield return (nameof(vm.ConvertDailyCap), "The daily cap can't be negative. Use 0 for no cap.");
        if (vm.ConvertEnabled && assets.Count == 0)
            yield return (nameof(vm.ConvertAssets), "List at least one coin to convert, or turn conversion off.");
        if (vm.ConvertEnabled && string.IsNullOrWhiteSpace(vm.ExchangeId))
            yield return (nameof(vm.ConvertEnabled), "Pick an exchange to convert on.");

        if (vm.ClearCredentials)
            yield break;
        var key = !string.IsNullOrWhiteSpace(vm.ApiKey);
        var secret = !string.IsNullOrWhiteSpace(vm.ApiSecret);
        if (key != secret)
            yield return (nameof(vm.ApiSecret), "Enter both the API key and its private key.");
        if ((key || secret) && string.IsNullOrWhiteSpace(vm.ExchangeId))
            yield return (nameof(vm.ExchangeId), "Pick the exchange this key belongs to.");
    }

    /// <summary>Hedge form errors, by field.</summary>
    public static IEnumerable<(string Field, string Error)> ValidateHedge(SweepSellSettingsViewModel vm)
    {
        var (markets, badMarket) = ParseMarkets(vm.HedgeMarkets);
        if (badMarket is not null)
            yield return (nameof(vm.HedgeMarkets), $"\"{badMarket}\" isn't a currency and market, e.g. GBP=CAD.");
        else if (markets.Any(m => m.Value == HedgeService.Asset))
            yield return (nameof(vm.HedgeMarkets), "BTC is what is sold; pick a market to sell it for, e.g. CAD.");
        var defaultMarket = Clean(vm.HedgeDefaultMarket)?.ToUpperInvariant();
        if (defaultMarket is not null && (!Ticker.IsMatch(defaultMarket) || defaultMarket == HedgeService.Asset))
            yield return (nameof(vm.HedgeDefaultMarket), "Enter the ticker to sell BTC for, e.g. USD, or leave it empty to use each invoice's currency.");
        if (vm.HedgeMaxSlippagePercent is < ConvertSettings.MinSlippageLimit or > ConvertSettings.MaxSlippageLimit)
            yield return (nameof(vm.HedgeMaxSlippagePercent),
                $"Allow between {ConvertSettings.MinSlippageLimit}% and {ConvertSettings.MaxSlippageLimit}% below the 24-hour average.");
        if (vm.HedgeDailyCap < 0)
            yield return (nameof(vm.HedgeDailyCap), "The daily cap can't be negative. Use 0 for no cap.");
        if (vm.HedgeEnabled && string.IsNullOrWhiteSpace(vm.ExchangeId))
            yield return (nameof(vm.HedgeEnabled), "Pick an exchange to hedge on.");
        // Convert sells all of an asset's balance, which would also sell the BTC kept on the exchange for hedges.
        if (vm.HedgeEnabled && vm.ConvertEnabled && Tickers(vm.ConvertAssets).Contains(HedgeService.Asset))
            yield return (nameof(vm.ConvertAssets), "BTC is hedged per invoice, so it can't also be in the coins to convert.");
    }

    /// <summary>"gbp=cad, EUR = CAD" → GBP: CAD, EUR: CAD; the first entry that doesn't parse, if any.</summary>
    static (Dictionary<string, string> Markets, string? Bad) ParseMarkets(string? value)
    {
        var markets = new Dictionary<string, string>();
        foreach (var entry in (value ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Ticker.IsMatch(parts[0].ToUpperInvariant()) || !Ticker.IsMatch(parts[1].ToUpperInvariant()))
                return (markets, entry);
            markets[parts[0].ToUpperInvariant()] = parts[1].ToUpperInvariant();
        }
        return (markets, null);
    }

    /// <summary>Sweep form errors, by field. Cold wallets are checked against the coin's network on this server.</summary>
    public static IEnumerable<(string Field, string Error)> ValidateSweep(SweepSellSettingsViewModel vm, IStoreWallets wallets)
    {
        if (vm.SweepIntervalMinutes is < SweepSettings.MinIntervalMinutes or > SweepSettings.MaxIntervalMinutes)
            yield return (nameof(vm.SweepIntervalMinutes),
                $"Check between every {SweepSettings.MinIntervalMinutes} and {SweepSettings.MaxIntervalMinutes} minutes.");
        if (vm.SweepFeeTargetBlocks is < SweepSettings.MinFeeTargetBlocks or > SweepSettings.MaxFeeTargetBlocks)
            yield return (nameof(vm.SweepFeeTargetBlocks),
                $"Aim for confirmation within {SweepSettings.MinFeeTargetBlocks} to {SweepSettings.MaxFeeTargetBlocks} blocks.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < vm.SweepRules.Count; i++)
        {
            var rule = vm.SweepRules[i];
            var field = $"{nameof(vm.SweepRules)}[{i}].";
            var code = Clean(rule.CryptoCode)?.ToUpperInvariant();
            if (code is null || !Ticker.IsMatch(code))
            {
                yield return (field + nameof(rule.CryptoCode), "A sweep rule has no valid coin.");
                continue;
            }
            if (!seen.Add(code))
                yield return (field + nameof(rule.CryptoCode), $"{code} has more than one sweep rule.");
            if (rule.MinimumBalance < 0)
                yield return (field + nameof(rule.MinimumBalance), $"{code}: the minimum balance can't be negative.");
            if (rule.DailyCap < 0)
                yield return (field + nameof(rule.DailyCap), $"{code}: the daily cap can't be negative. Use 0 for no cap.");
            if (rule.ColdPercent is < 0 or >= 100)
                yield return (field + nameof(rule.ColdPercent), $"{code}: the cold wallet share must be at least 0% and under 100%.");
            if (rule.ColdStartIndex < 0)
                yield return (field + nameof(rule.ColdStartIndex), $"{code}: the first address index can't be negative.");
            var cold = Clean(rule.ColdDestination);
            if (rule.ColdPercent > 0 && cold is null)
                yield return (field + nameof(rule.ColdDestination), $"{code}: enter the cold wallet address or extended public key, or set its share to 0%.");
            else if (cold is not null && CheckCold(wallets, code, cold) is { } error)
                yield return (field + nameof(rule.ColdDestination), $"{code}: {error}");
        }
        if (vm.SweepEnabled && string.IsNullOrWhiteSpace(vm.ExchangeId))
            yield return (nameof(vm.SweepEnabled), "Pick an exchange to sweep to.");
        if (vm.SweepEnabled && !vm.SweepRules.Any(r => r.Enabled))
            yield return (nameof(vm.SweepEnabled), "Turn on sweeping for at least one coin, or turn sweeping off.");
    }

    static string? CheckCold(IStoreWallets wallets, string code, string destination)
    {
        try
        {
            return wallets.CheckColdDestination(code, destination);
        }
        catch (SweepException e)
        {
            return e.Message; // e.g. a coin this server doesn't support
        }
    }

    /// <summary>
    /// The settings after a save. Keys are write-only on the page: an empty field keeps the saved key,
    /// "remove" clears both, and a new pair replaces both.
    /// </summary>
    public static SweepSellSettings Apply(SweepSellSettings saved, SweepSellSettingsViewModel vm, SecretProtector protector, DateTimeOffset? now = null)
    {
        var updated = new SweepSellSettings
        {
            Paused = vm.Paused,
            ExchangeId = string.IsNullOrWhiteSpace(vm.ExchangeId) ? null : vm.ExchangeId.Trim(),
            ProtectedApiKey = saved.ProtectedApiKey,
            ProtectedApiSecret = saved.ProtectedApiSecret,
            Convert = new ConvertSettings
            {
                Enabled = vm.ConvertEnabled,
                DryRun = vm.ConvertDryRun,
                Assets = Tickers(vm.ConvertAssets),
                TargetAsset = Clean(vm.ConvertTarget)?.ToUpperInvariant() ?? saved.Convert.TargetAsset,
                IntervalMinutes = vm.ConvertIntervalMinutes,
                MaxSlippagePercent = vm.ConvertMaxSlippagePercent,
                DailyCap = vm.ConvertDailyCap
            },
            Sweep = new SweepSettings
            {
                Enabled = vm.SweepEnabled,
                DryRun = vm.SweepDryRun,
                IntervalMinutes = vm.SweepIntervalMinutes,
                FeeTargetBlocks = vm.SweepFeeTargetBlocks,
                Rules = vm.SweepRules.Select(r => new SweepRule
                {
                    CryptoCode = Clean(r.CryptoCode)?.ToUpperInvariant() ?? "",
                    Enabled = r.Enabled,
                    MinimumBalance = r.MinimumBalance,
                    ColdPercent = r.ColdPercent,
                    ColdDestination = Clean(r.ColdDestination),
                    ColdStartIndex = r.ColdStartIndex,
                    DailyCap = r.DailyCap,
                    IncludeUnconfirmed = r.IncludeUnconfirmed
                }).ToList()
            },
            Hedge = new HedgeSettings
            {
                Enabled = vm.HedgeEnabled,
                DryRun = vm.HedgeDryRun,
                Trigger = vm.HedgeTrigger,
                Amount = vm.HedgeAmount,
                Markets = ParseMarkets(vm.HedgeMarkets).Markets,
                DefaultMarket = Clean(vm.HedgeDefaultMarket)?.ToUpperInvariant(),
                MaxSlippagePercent = vm.HedgeMaxSlippagePercent,
                DailyCap = vm.HedgeDailyCap,
                // Hedging covers invoices created from when it was turned on; turning it off and on again starts afresh.
                EnabledSince = !vm.HedgeEnabled ? null
                    : saved.Hedge.Enabled && saved.Hedge.EnabledSince is { } since ? since
                    : now ?? DateTimeOffset.UtcNow
            }
        };
        var key = Clean(vm.ApiKey);
        var secret = Clean(vm.ApiSecret);
        if (vm.ClearCredentials || updated.ExchangeId != saved.ExchangeId)
        {
            // A key belongs to one exchange: switching exchanges forgets it.
            updated.ProtectedApiKey = null;
            updated.ProtectedApiSecret = null;
        }
        // "Remove" wins over anything typed (or autofilled) in the key fields, and a key needs an exchange to belong to.
        if (!vm.ClearCredentials && updated.ExchangeId is not null && key is not null && secret is not null)
        {
            updated.ProtectedApiKey = protector.Protect(key);
            updated.ProtectedApiSecret = protector.Protect(secret);
        }
        return updated;
    }

    async Task<SweepSellSettingsViewModel> ToViewModelAsync(string storeId, SweepSellSettings settings, SweepSellSettingsViewModel? posted,
        CancellationToken cancellationToken)
    {
        var convertState = await repository.GetConvertStateAsync(storeId);
        var sweepState = await repository.GetSweepStateAsync(storeId);
        var hedgeState = await repository.GetHedgeStateAsync(storeId);
        // One row per coin the store has an on-chain wallet for, plus any saved rule whose wallet was removed since.
        var rules = posted?.SweepRules ?? (await wallets.GetCryptoCodesAsync(storeId, cancellationToken))
            .Select(code => settings.Sweep.Rules.FirstOrDefault(r => string.Equals(r.CryptoCode, code, StringComparison.OrdinalIgnoreCase))
                            ?? new SweepRule { CryptoCode = code })
            .Concat(settings.Sweep.Rules)
            .DistinctBy(r => r.CryptoCode.ToUpperInvariant())
            .Select(r => new SweepRuleViewModel
            {
                CryptoCode = r.CryptoCode,
                Enabled = r.Enabled,
                MinimumBalance = r.MinimumBalance,
                ColdPercent = r.ColdPercent,
                ColdDestination = r.ColdDestination,
                ColdStartIndex = r.ColdStartIndex,
                DailyCap = r.DailyCap,
                IncludeUnconfirmed = r.IncludeUnconfirmed
            })
            .ToList();
        foreach (var rule in rules)
        {
            var wallet = string.IsNullOrWhiteSpace(rule.CryptoCode) ? null : await wallets.GetWalletAsync(storeId, rule.CryptoCode, cancellationToken);
            rule.WalletStatus = wallet is null ? "No wallet" : wallet.IsHot ? "Hot wallet" : "Not a hot wallet: can't be swept";
        }
        return new SweepSellSettingsViewModel
        {
            Paused = posted?.Paused ?? settings.Paused,
            ExchangeId = posted?.ExchangeId ?? settings.ExchangeId,
            ConvertEnabled = posted?.ConvertEnabled ?? settings.Convert.Enabled,
            ConvertDryRun = posted?.ConvertDryRun ?? settings.Convert.DryRun,
            ConvertAssets = posted?.ConvertAssets ?? string.Join(", ", settings.Convert.Assets),
            ConvertTarget = posted?.ConvertTarget ?? settings.Convert.TargetAsset,
            ConvertIntervalMinutes = posted?.ConvertIntervalMinutes ?? settings.Convert.IntervalMinutes,
            ConvertMaxSlippagePercent = posted?.ConvertMaxSlippagePercent ?? settings.Convert.MaxSlippagePercent,
            ConvertDailyCap = posted?.ConvertDailyCap ?? settings.Convert.DailyCap,
            ConvertLastRun = convertState.LastRun,
            ConvertPending = convertState.Pending,
            ConvertActivity = convertState.Activity,
            SweepEnabled = posted?.SweepEnabled ?? settings.Sweep.Enabled,
            SweepDryRun = posted?.SweepDryRun ?? settings.Sweep.DryRun,
            SweepIntervalMinutes = posted?.SweepIntervalMinutes ?? settings.Sweep.IntervalMinutes,
            SweepFeeTargetBlocks = posted?.SweepFeeTargetBlocks ?? settings.Sweep.FeeTargetBlocks,
            SweepRules = rules,
            SweepLastRun = sweepState.LastRun,
            SweepPending = sweepState.Pending,
            SweepAwaiting = sweepState.Awaiting,
            SweepStopped = sweepState.Stopped,
            SweepActivity = sweepState.Activity,
            CanSpend = await CanSpendAsync(storeId),
            HedgeEnabled = posted?.HedgeEnabled ?? settings.Hedge.Enabled,
            HedgeDryRun = posted?.HedgeDryRun ?? settings.Hedge.DryRun,
            HedgeTrigger = posted?.HedgeTrigger ?? settings.Hedge.Trigger,
            HedgeAmount = posted?.HedgeAmount ?? settings.Hedge.Amount,
            HedgeMarkets = posted?.HedgeMarkets ?? string.Join(", ", settings.Hedge.Markets.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}")),
            HedgeDefaultMarket = posted?.HedgeDefaultMarket ?? settings.Hedge.DefaultMarket,
            HedgeMaxSlippagePercent = posted?.HedgeMaxSlippagePercent ?? settings.Hedge.MaxSlippagePercent,
            HedgeDailyCap = posted?.HedgeDailyCap ?? settings.Hedge.DailyCap,
            HedgeEnabledSince = settings.Hedge.EnabledSince,
            HedgeLastRun = hedgeState.LastRun,
            HedgePending = hedgeState.Pending,
            HedgeQueued = hedgeState.Invoices.Where(i => i.Value.Remaining > 0).OrderBy(i => i.Value.CreatedAt)
                .Select(i => new QueuedHedge(i.Key, i.Value.Remaining, i.Value.Amount, i.Value.Market, i.Value.DryRun)).ToList(),
            HedgeActivity = hedgeState.Activity,
            // Saved but undecryptable (e.g. restored on a server with other data-protection keys) counts as not saved.
            HasCredentials = protector.Credentials(settings) is not null,
            CredentialsUnreadable = settings.HasCredentials && protector.Credentials(settings) is null,
            Exchanges = exchanges.All.Select(e => new ExchangeOption(e.Id, e.Name, e.KeyPermissionsHelp)).ToList()
        };
    }

    /// <summary>"doge, LTC  xmr" → DOGE, LTC, XMR.</summary>
    static List<string> Tickers(string? value) =>
        (value ?? "").Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.ToUpperInvariant())
        .Distinct()
        .ToList();

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public record ExchangeOption(string Id, string Name, string KeyPermissionsHelp);

public record QueuedHedge(string InvoiceId, decimal Remaining, decimal Amount, string Market, bool DryRun);

public class SweepSellSettingsViewModel
{
    [Display(Name = "Pause everything")]
    public bool Paused { get; set; }

    [Display(Name = "Exchange")]
    public string? ExchangeId { get; set; }

    [Display(Name = "API key")]
    public string? ApiKey { get; set; }

    [Display(Name = "Private key")]
    public string? ApiSecret { get; set; }

    [Display(Name = "Remove the saved key")]
    public bool ClearCredentials { get; set; }

    [Display(Name = "Convert at the exchange")]
    public bool ConvertEnabled { get; set; }

    [Display(Name = "Dry run")]
    public bool ConvertDryRun { get; set; } = true;

    [Display(Name = "Coins to convert")]
    public string? ConvertAssets { get; set; }

    [Display(Name = "Convert to")]
    public string? ConvertTarget { get; set; } = "BTC";

    [Display(Name = "Check every (minutes)")]
    public int ConvertIntervalMinutes { get; set; } = 15;

    [Display(Name = "Lowest price, % below the 24-hour average")]
    public decimal ConvertMaxSlippagePercent { get; set; } = 2m;

    [Display(Name = "Daily cap (in the coin you convert to)")]
    public decimal ConvertDailyCap { get; set; }

    public DateTimeOffset? ConvertLastRun { get; set; }

    public List<PendingOrder> ConvertPending { get; set; } = [];

    public List<ActivityEntry> ConvertActivity { get; set; } = [];

    [Display(Name = "Sweep to the exchange")]
    public bool SweepEnabled { get; set; }

    [Display(Name = "Dry run")]
    public bool SweepDryRun { get; set; } = true;

    [Display(Name = "Also check every (minutes)")]
    public int SweepIntervalMinutes { get; set; } = 60;

    [Display(Name = "Network fee: confirm within (blocks)")]
    public int SweepFeeTargetBlocks { get; set; } = 6;

    public List<SweepRuleViewModel> SweepRules { get; set; } = [];

    public DateTimeOffset? SweepLastRun { get; set; }

    public List<PendingSweep> SweepPending { get; set; } = [];

    public List<AwaitingDeposit> SweepAwaiting { get; set; } = [];

    public Dictionary<string, StoppedSweep> SweepStopped { get; set; } = new();

    public List<ActivityEntry> SweepActivity { get; set; } = [];

    /// <summary>Whether the user may change sweeps (sign and broadcast wallet transactions in the store). Not posted.</summary>
    public bool CanSpend { get; set; }

    [Display(Name = "Hedge on payment")]
    public bool HedgeEnabled { get; set; }

    [Display(Name = "Dry run")]
    public bool HedgeDryRun { get; set; } = true;

    [Display(Name = "Hedge when the invoice is")]
    public HedgeTrigger HedgeTrigger { get; set; } = HedgeTrigger.Settled;

    [Display(Name = "What to hedge")]
    public HedgeAmount HedgeAmount { get; set; } = HedgeAmount.BtcPaid;

    [Display(Name = "Markets by invoice currency")]
    public string? HedgeMarkets { get; set; }

    [Display(Name = "Default market")]
    public string? HedgeDefaultMarket { get; set; }

    [Display(Name = "Lowest price, % below the 24-hour average")]
    public decimal HedgeMaxSlippagePercent { get; set; } = 2m;

    [Display(Name = "Daily cap (BTC)")]
    public decimal HedgeDailyCap { get; set; }

    public DateTimeOffset? HedgeEnabledSince { get; set; }

    public DateTimeOffset? HedgeLastRun { get; set; }

    public List<PendingOrder> HedgePending { get; set; } = [];

    public List<QueuedHedge> HedgeQueued { get; set; } = [];

    public List<ActivityEntry> HedgeActivity { get; set; } = [];

    public bool HasCredentials { get; set; }

    public bool CredentialsUnreadable { get; set; }

    public List<ExchangeOption> Exchanges { get; set; } = [];
}

public class SweepRuleViewModel
{
    public string? CryptoCode { get; set; }
    public bool Enabled { get; set; }
    public decimal MinimumBalance { get; set; }
    public decimal ColdPercent { get; set; }
    public string? ColdDestination { get; set; }
    public int ColdStartIndex { get; set; }
    public decimal DailyCap { get; set; }
    public bool IncludeUnconfirmed { get; set; }

    /// <summary>Shown next to the coin; not posted.</summary>
    public string? WalletStatus { get; set; }
}
