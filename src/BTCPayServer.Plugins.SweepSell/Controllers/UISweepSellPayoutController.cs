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
using BTCPayServer.Plugins.SweepSell.Payout;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Controllers;

/// <summary>
/// Fiat payout settings, on their own page and with their own withdraw-only key. Payouts only go to a destination
/// already saved in the exchange account, named here; the page can't add or change a destination at the exchange.
/// </summary>
[Route("stores/{storeId}/sweep-sell/payout")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISweepSellPayoutController(IPayoutRepository payouts, ISweepSellRepository repository, SecretProtector protector,
    WithdrawalRegistry withdrawals, PayoutService payout, ILogger<UISweepSellPayoutController> logger) : Controller
{
    public const string PayoutView = SweepSellPlugin.ViewsDirectory + "/Payout.cshtml";

    [HttpGet("")]
    public async Task<IActionResult> Payout(string storeId) =>
        View(PayoutView, ToViewModel(await payouts.GetAsync(storeId), await payouts.GetStateAsync(storeId), null));

    [HttpPost("")]
    public async Task<IActionResult> Payout(string storeId, PayoutSettingsViewModel vm, CancellationToken cancellationToken)
    {
        foreach (var (field, error) in Validate(vm, withdrawals))
            ModelState.AddModelError(field, error);
        if (!ModelState.IsValid)
            return View(PayoutView, ToViewModel(await payouts.GetAsync(storeId), await payouts.GetStateAsync(storeId), vm));

        // Under the store's settings lock: a run checks the saved settings again under it right before withdrawing, so once
        // this returns nothing goes out under the old settings, and the pending withdrawals seen here are all there are.
        var (saved, updated, conflict) = await WithSettingsLockAsync(storeId, async () =>
        {
            var current = await payouts.GetAsync(storeId);
            var next = Apply(current, vm, protector);
            var error = SameKeyConflict(next, await repository.GetAsync(storeId), protector) ??
                        PendingConflict(current, next, (await payouts.GetStateAsync(storeId)).Pending, protector);
            if (error is not null)
                return (current, next, error);
            await payouts.SaveAsync(storeId, next);
            return (current, next, (string?)null);
        }, cancellationToken);
        if (conflict is not null)
        {
            ModelState.AddModelError(nameof(vm.ExchangeId), conflict);
            return View(PayoutView, ToViewModel(saved, await payouts.GetStateAsync(storeId), vm));
        }
        await payout.RecordChangesAsync(storeId, saved, updated, cancellationToken);

        // Never log the key; whether one is saved is enough to follow what happened.
        logger.LogInformation("Sweep & Sell payout settings of store {StoreId} saved: {Enabled} ({Mode}), exchange {Exchange}, key {Key}, " +
                              "asset {Asset}, destination {Destination}, every {Interval} min",
            storeId, updated.Enabled ? "on" : "off", updated.DryRun ? "dry run" : "live", updated.ExchangeId ?? "none",
            updated.HasCredentials ? "saved" : "none", updated.Asset, updated.Destination, updated.IntervalMinutes);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Payout settings saved."
        });
        return RedirectToAction(nameof(Payout), new { storeId });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestCredentials(string storeId, CancellationToken cancellationToken)
    {
        var settings = await payouts.GetAsync(storeId);
        var connector = withdrawals.Find(settings.ExchangeId);
        var credentials = protector.Credentials(settings);
        StatusMessageModel message;
        if (connector is null || credentials is null || string.IsNullOrEmpty(settings.Asset))
        {
            message = new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Save an exchange, the payout key and the asset to withdraw first."
            };
        }
        else
        {
            var asset = settings.Asset;
            var checks = new List<PermissionCheck>();
            string destinations;
            try
            {
                checks.AddRange((await connector.TestCredentialsAsync(credentials, asset, cancellationToken)).Checks);
                var listed = await connector.GetDestinationsAsync(credentials, asset, cancellationToken);
                destinations = listed.Count == 0
                    ? $"{connector.Name} lists no saved {asset} withdrawal destinations for this key (some kinds, such as bank accounts, may not be listed; a dry run shows whether yours works)."
                    : $"Saved {asset} destinations: " + string.Join(", ", listed.Select(d => $"\"{d.Name}\" ({d.Method}{(d.Verified ? "" : ", not verified")})")) + ".";
            }
            catch (Exception e) when (e is ExchangeException or System.Net.Http.HttpRequestException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Sweep & Sell payout key test for store {StoreId} on {Exchange} could not run: {Error}", storeId, connector.Id, e.Message);
                checks.Add(new PermissionCheck("Connection", false, e.Message));
                destinations = "";
            }
            if (IsTradingKey(await repository.GetAsync(storeId), credentials))
                checks.Add(new PermissionCheck("Separate key", false, "This is the store's trading key. Payout needs its own key."));
            foreach (var failed in checks.Where(c => !c.Ok))
                logger.LogWarning("Sweep & Sell payout key test failed for store {StoreId} on {Exchange}: {Permission}: {Error}",
                    storeId, connector.Id, failed.Permission, failed.Error);
            var allOk = checks.All(c => c.Ok);
            message = new StatusMessageModel
            {
                Severity = allOk ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Error,
                Message = $"{connector.Name}: " + string.Join(" ", checks.Select(c => c.Ok ? $"{c.Permission}: OK." : $"{c.Permission}: failed ({c.Error}).")) +
                          (destinations.Length > 0 ? " " + destinations : "")
            };
        }
        TempData.SetStatusMessageModel(message);
        return RedirectToAction(nameof(Payout), new { storeId });
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunPayout(string storeId, CancellationToken cancellationToken)
    {
        var result = await payout.RunAsync(storeId, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = result == PayoutRunResult.Ran ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = result switch
            {
                PayoutRunResult.Ran => "Payout ran. What it did is listed under Payout activity.",
                PayoutRunResult.Paused => "Sweep & Sell is paused for this store; nothing was withdrawn.",
                PayoutRunResult.Disabled => "Turn on payout and save first.",
                _ => "A payout run is already in progress. Try again in a moment."
            }
        });
        return RedirectToAction(nameof(Payout), new { storeId });
    }

    [HttpPost("pending/{id:guid}/remove")]
    public async Task<IActionResult> RemovePending(string storeId, Guid id, CancellationToken cancellationToken)
    {
        var removed = await payout.RemovePendingAsync(storeId, id, cancellationToken);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = removed ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = removed ? "Pending withdrawal removed. Payout resumes on the next run." : "That withdrawal is no longer pending."
        });
        return RedirectToAction(nameof(Payout), new { storeId });
    }

    static async Task<T> WithSettingsLockAsync<T>(string storeId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = SettingsGate.For(storeId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    bool IsTradingKey(SweepSellSettings settings, ExchangeCredentials payoutKey) => payout.IsTradingKey(settings, payoutKey);

    /// <summary>The payout key must not be the trading key: one key must never be able to both trade and withdraw.</summary>
    public static string? SameKeyConflict(PayoutSettings updated, SweepSellSettings settings, SecretProtector protector) =>
        protector.Credentials(updated) is { } payoutKey && protector.Credentials(settings) is { } trading && trading.ApiKey == payoutKey.ApiKey
            ? "This is the store's trading key. Payout needs its own key, with only Query Funds and Withdraw Funds."
            : null;

    /// <summary>
    /// A pending withdrawal can only be found with the exchange, key and asset it was sent with, so while one is pending a
    /// save may not put others in place. Removing the key is always allowed. Null when the save is fine.
    /// </summary>
    public static string? PendingConflict(PayoutSettings saved, PayoutSettings updated, IReadOnlyCollection<PendingWithdrawal> pending,
        SecretProtector protector)
    {
        if (pending.Count == 0)
            return null;
        if (updated.HasCredentials && protector.Credentials(updated) is { } credentials)
        {
            var fingerprint = credentials.Fingerprint();
            if (pending.Any(p => p.ExchangeId != updated.ExchangeId || p.KeyFingerprint != fingerprint || p.Asset != updated.Asset))
                return "A withdrawal sent with another exchange, key or asset is still pending. Keep those so it can be found, or check it in your exchange and remove it under Pending withdrawals first.";
        }
        else if (saved.Asset != updated.Asset && pending.Any(p => p.Asset != updated.Asset))
        {
            return "A withdrawal of another asset is still pending. Keep the asset so it can be found, or check it in your exchange and remove it under Pending withdrawals first.";
        }
        return null;
    }

    static readonly Regex Ticker = new("^[A-Z0-9]{2,10}$", RegexOptions.CultureInvariant);

    /// <summary>Form errors, by field.</summary>
    public static IEnumerable<(string Field, string Error)> Validate(PayoutSettingsViewModel vm, WithdrawalRegistry withdrawals)
    {
        if (!string.IsNullOrEmpty(vm.ExchangeId) && withdrawals.Find(vm.ExchangeId) is null)
            yield return (nameof(vm.ExchangeId), "Pick an exchange from the list.");
        var asset = Clean(vm.Asset)?.ToUpperInvariant();
        if (asset is not null && !Ticker.IsMatch(asset))
            yield return (nameof(vm.Asset), $"\"{asset}\" isn't a ticker. Use letters and digits, e.g. USD.");
        var destination = Clean(vm.Destination);
        if (destination is { Length: > 200 })
            yield return (nameof(vm.Destination), "That name is too long.");
        if (vm.Minimum < 0)
            yield return (nameof(vm.Minimum), "The minimum can't be negative.");
        if (vm.KeepBalance < 0)
            yield return (nameof(vm.KeepBalance), "The amount to keep can't be negative.");
        if (vm.DailyCap < 0)
            yield return (nameof(vm.DailyCap), "The daily cap can't be negative.");
        if (vm.MaxFee < 0)
            yield return (nameof(vm.MaxFee), "The highest fee can't be negative. Use 0 to allow only free withdrawals.");
        if (vm.IntervalMinutes is < PayoutSettings.MinIntervalMinutes or > PayoutSettings.MaxIntervalMinutes)
            yield return (nameof(vm.IntervalMinutes),
                $"Check between every {PayoutSettings.MinIntervalMinutes} and {PayoutSettings.MaxIntervalMinutes} minutes.");
        if (vm.Enabled)
        {
            if (string.IsNullOrWhiteSpace(vm.ExchangeId))
                yield return (nameof(vm.Enabled), "Pick the exchange to pay out from.");
            if (asset is null)
                yield return (nameof(vm.Asset), "Enter the ticker to withdraw, e.g. USD.");
            if (destination is null)
                yield return (nameof(vm.Destination), "Enter the name of a withdrawal destination saved in your exchange.");
            if (vm.DailyCap <= 0)
                yield return (nameof(vm.DailyCap), "Payout needs a daily cap above 0.");
            if (vm.Minimum <= 0)
                yield return (nameof(vm.Minimum), "Payout needs a minimum above 0.");
        }
        if (vm.ClearCredentials)
            yield break;
        var key = !string.IsNullOrWhiteSpace(vm.ApiKey);
        var secret = !string.IsNullOrWhiteSpace(vm.ApiSecret);
        if (key != secret)
            yield return (nameof(vm.ApiSecret), "Enter both the API key and its private key.");
        if ((key || secret) && string.IsNullOrWhiteSpace(vm.ExchangeId))
            yield return (nameof(vm.ExchangeId), "Pick the exchange this key belongs to.");
    }

    /// <summary>
    /// The settings after a save. The key is write-only on the page: an empty field keeps the saved key, "remove" clears it,
    /// and a new pair replaces it. Changing the exchange forgets the key.
    /// </summary>
    public static PayoutSettings Apply(PayoutSettings saved, PayoutSettingsViewModel vm, SecretProtector protector)
    {
        var updated = new PayoutSettings
        {
            Enabled = vm.Enabled,
            DryRun = vm.DryRun,
            ExchangeId = Clean(vm.ExchangeId),
            ProtectedApiKey = saved.ProtectedApiKey,
            ProtectedApiSecret = saved.ProtectedApiSecret,
            Asset = Clean(vm.Asset)?.ToUpperInvariant() ?? "",
            Destination = Clean(vm.Destination) ?? "",
            Minimum = vm.Minimum,
            KeepBalance = vm.KeepBalance,
            DailyCap = vm.DailyCap,
            MaxFee = vm.MaxFee,
            IntervalMinutes = vm.IntervalMinutes
        };
        if (vm.ClearCredentials || updated.ExchangeId != saved.ExchangeId)
        {
            updated.ProtectedApiKey = null;
            updated.ProtectedApiSecret = null;
        }
        var key = Clean(vm.ApiKey);
        var secret = Clean(vm.ApiSecret);
        if (!vm.ClearCredentials && updated.ExchangeId is not null && key is not null && secret is not null)
        {
            updated.ProtectedApiKey = protector.Protect(key);
            updated.ProtectedApiSecret = protector.Protect(secret);
        }
        return updated;
    }

    PayoutSettingsViewModel ToViewModel(PayoutSettings settings, PayoutState state, PayoutSettingsViewModel? posted) => new()
    {
        Enabled = posted?.Enabled ?? settings.Enabled,
        DryRun = posted?.DryRun ?? settings.DryRun,
        ExchangeId = posted?.ExchangeId ?? settings.ExchangeId,
        Asset = posted?.Asset ?? settings.Asset,
        Destination = posted?.Destination ?? settings.Destination,
        Minimum = posted?.Minimum ?? settings.Minimum,
        KeepBalance = posted?.KeepBalance ?? settings.KeepBalance,
        DailyCap = posted?.DailyCap ?? settings.DailyCap,
        MaxFee = posted?.MaxFee ?? settings.MaxFee,
        IntervalMinutes = posted?.IntervalMinutes ?? settings.IntervalMinutes,
        LastRun = state.LastRun,
        Pending = state.Pending,
        Activity = state.Activity,
        HasCredentials = protector.Credentials(settings) is not null,
        CredentialsUnreadable = settings.HasCredentials && protector.Credentials(settings) is null,
        Exchanges = withdrawals.All.Select(e => new ExchangeOption(e.Id, e.Name, e.KeyPermissionsHelp)).ToList()
    };

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public class PayoutSettingsViewModel
{
    [Display(Name = "Pay out from the exchange")]
    public bool Enabled { get; set; }

    [Display(Name = "Dry run")]
    public bool DryRun { get; set; } = true;

    [Display(Name = "Exchange")]
    public string? ExchangeId { get; set; }

    [Display(Name = "Payout API key")]
    public string? ApiKey { get; set; }

    [Display(Name = "Payout private key")]
    public string? ApiSecret { get; set; }

    [Display(Name = "Remove the saved payout key")]
    public bool ClearCredentials { get; set; }

    [Display(Name = "Asset to withdraw")]
    public string? Asset { get; set; }

    [Display(Name = "Saved destination (its name in your exchange)")]
    public string? Destination { get; set; }

    [Display(Name = "Smallest withdrawal")]
    public decimal Minimum { get; set; }

    [Display(Name = "Always keep on the exchange")]
    public decimal KeepBalance { get; set; }

    [Display(Name = "Daily cap")]
    public decimal DailyCap { get; set; }

    [Display(Name = "Highest fee per withdrawal")]
    public decimal MaxFee { get; set; }

    [Display(Name = "Check every (minutes)")]
    public int IntervalMinutes { get; set; } = 1440;

    public DateTimeOffset? LastRun { get; set; }

    public List<PendingWithdrawal> Pending { get; set; } = [];

    public List<ActivityEntry> Activity { get; set; } = [];

    public bool HasCredentials { get; set; }

    public bool CredentialsUnreadable { get; set; }

    public List<ExchangeOption> Exchanges { get; set; } = [];
}
