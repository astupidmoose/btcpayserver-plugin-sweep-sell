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
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Controllers;

[Route("stores/{storeId}/sweep-sell")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISweepSellController(ISweepSellRepository repository, SecretProtector protector, ExchangeRegistry exchanges,
    ConvertService convert, ILogger<UISweepSellController> logger) : Controller
{
    public const string SettingsView = SweepSellPlugin.ViewsDirectory + "/Settings.cshtml";

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var settings = await repository.GetAsync(storeId);
        return View(SettingsView, ToViewModel(settings, await repository.GetConvertStateAsync(storeId), null));
    }

    [HttpPost("")]
    public async Task<IActionResult> Settings(string storeId, SweepSellSettingsViewModel vm)
    {
        var saved = await repository.GetAsync(storeId);
        var state = await repository.GetConvertStateAsync(storeId);
        foreach (var (field, error) in Validate(vm, exchanges))
            ModelState.AddModelError(field, error);
        if (PendingConflict(saved, vm, state.Pending.Count) is { } conflict)
            ModelState.AddModelError(nameof(vm.ExchangeId), conflict);
        var updated = Apply(saved, vm, protector);
        if (!ModelState.IsValid)
            return View(SettingsView, ToViewModel(saved, state, vm));

        await repository.SaveAsync(storeId, updated);
        // Never log the key itself; whether one is saved is enough to follow what happened.
        logger.LogInformation("Sweep & Sell settings of store {StoreId} saved: exchange {Exchange}, credentials {Credentials}, paused {Paused}, " +
                              "convert {Convert} ({ConvertMode}, {ConvertAssets} to {ConvertTarget} every {ConvertInterval} min)",
            storeId, updated.ExchangeId ?? "none", updated.HasCredentials ? "saved" : "none", updated.Paused,
            updated.Convert.Enabled ? "on" : "off", updated.Convert.DryRun ? "dry run" : "live", string.Join(",", updated.Convert.Assets),
            updated.Convert.TargetAsset, updated.Convert.IntervalMinutes);
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

    /// <summary>
    /// Changing the exchange or entering a new key while orders are pending would make them impossible to look up.
    /// Removing the key is always allowed: it must never be blocked in an emergency.
    /// </summary>
    public static string? PendingConflict(SweepSellSettings saved, SweepSellSettingsViewModel vm, int pendingOrders)
    {
        if (pendingOrders == 0 || vm.ClearCredentials)
            return null;
        var exchangeChanges = Clean(vm.ExchangeId) != saved.ExchangeId;
        var newKey = Clean(vm.ApiKey) is not null || Clean(vm.ApiSecret) is not null;
        return exchangeChanges || newKey
            ? "Orders are still being checked with the exchange. Wait for them to be found (see Pending orders), or remove them there after checking your exchange, before changing the exchange or key."
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

    /// <summary>
    /// The settings after a save. Keys are write-only on the page: an empty field keeps the saved key,
    /// "remove" clears both, and a new pair replaces both.
    /// </summary>
    public static SweepSellSettings Apply(SweepSellSettings saved, SweepSellSettingsViewModel vm, SecretProtector protector)
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

    SweepSellSettingsViewModel ToViewModel(SweepSellSettings settings, ConvertState state, SweepSellSettingsViewModel? posted) => new()
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
        ConvertLastRun = state.LastRun,
        ConvertPending = state.Pending,
        ConvertActivity = state.Activity,
        // Saved but undecryptable (e.g. restored on a server with other data-protection keys) counts as not saved.
        HasCredentials = protector.Credentials(settings) is not null,
        CredentialsUnreadable = settings.HasCredentials && protector.Credentials(settings) is null,
        Exchanges = exchanges.All.Select(e => new ExchangeOption(e.Id, e.Name, e.KeyPermissionsHelp)).ToList()
    };

    /// <summary>"doge, LTC  xmr" → DOGE, LTC, XMR.</summary>
    static List<string> Tickers(string? value) =>
        (value ?? "").Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.ToUpperInvariant())
        .Distinct()
        .ToList();

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public record ExchangeOption(string Id, string Name, string KeyPermissionsHelp);

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

    public bool HasCredentials { get; set; }

    public bool CredentialsUnreadable { get; set; }

    public List<ExchangeOption> Exchanges { get; set; } = [];
}
