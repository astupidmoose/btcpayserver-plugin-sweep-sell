#nullable enable
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Controllers;

[Route("stores/{storeId}/sweep-sell")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UISweepSellController(SweepSellSettingsRepository repository, SecretProtector protector, ExchangeRegistry exchanges,
    ILogger<UISweepSellController> logger) : Controller
{
    public const string SettingsView = SweepSellPlugin.ViewsDirectory + "/Settings.cshtml";

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var settings = await repository.GetAsync(storeId);
        return View(SettingsView, ToViewModel(settings, null));
    }

    [HttpPost("")]
    public async Task<IActionResult> Settings(string storeId, SweepSellSettingsViewModel vm)
    {
        var saved = await repository.GetAsync(storeId);
        var exchange = exchanges.Find(vm.ExchangeId);
        if (!string.IsNullOrEmpty(vm.ExchangeId) && exchange is null)
            ModelState.AddModelError(nameof(vm.ExchangeId), "Pick an exchange from the list.");
        var updated = Apply(saved, vm, protector);
        if (string.IsNullOrWhiteSpace(vm.ApiKey) != string.IsNullOrWhiteSpace(vm.ApiSecret))
            ModelState.AddModelError(nameof(vm.ApiSecret), "Enter both the API key and its private key.");
        if (!ModelState.IsValid)
            return View(SettingsView, ToViewModel(saved, vm));

        await repository.SaveAsync(storeId, updated);
        // Never log the key itself; whether one is saved is enough to follow what happened.
        logger.LogInformation("Sweep & Sell settings of store {StoreId} saved: exchange {Exchange}, credentials {Credentials}, paused {Paused}",
            storeId, updated.ExchangeId ?? "none", updated.HasCredentials ? "saved" : "none", updated.Paused);
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
            var check = await exchange.TestCredentialsAsync(credentials, cancellationToken);
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
            ProtectedApiSecret = saved.ProtectedApiSecret
        };
        var key = Clean(vm.ApiKey);
        var secret = Clean(vm.ApiSecret);
        if (vm.ClearCredentials || updated.ExchangeId != saved.ExchangeId)
        {
            // A key belongs to one exchange: switching exchanges forgets it.
            updated.ProtectedApiKey = null;
            updated.ProtectedApiSecret = null;
        }
        if (key is not null && secret is not null)
        {
            updated.ProtectedApiKey = protector.Protect(key);
            updated.ProtectedApiSecret = protector.Protect(secret);
        }
        return updated;
    }

    SweepSellSettingsViewModel ToViewModel(SweepSellSettings settings, SweepSellSettingsViewModel? posted) => new()
    {
        Paused = posted?.Paused ?? settings.Paused,
        ExchangeId = posted?.ExchangeId ?? settings.ExchangeId,
        HasCredentials = settings.HasCredentials,
        Exchanges = exchanges.All.Select(e => new ExchangeOption(e.Id, e.Name, e.KeyPermissionsHelp)).ToList()
    };

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

    public bool HasCredentials { get; set; }

    public List<ExchangeOption> Exchanges { get; set; } = [];
}
