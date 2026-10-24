#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Conversion;

public enum ConvertRunResult
{
    /// <summary>The run went through its steps; what happened is in the store's activity.</summary>
    Ran,
    Paused,
    Disabled,
    /// <summary>Another run of the same store was in progress.</summary>
    Busy
}

/// <summary>
/// Sells everything available of a store's convert assets for its target asset, once each clears the exchange's
/// minimum order. Every live order is saved as pending before it is sent and looked up by its client order ID
/// when its outcome is unknown, so a lost response or a restart never sells twice.
/// </summary>
public class ConvertService(ISweepSellRepository repository, SecretProtector protector, ExchangeRegistry exchanges,
    TimeProvider clock, ILogger<ConvertService> logger)
{
    /// <summary>How long an order the exchange has no record of stays pending before it counts as never placed.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(5);

    static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new();

    /// <summary>Runs every store whose interval has passed.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        foreach (var (storeId, settings) in await repository.GetAllAsync())
        {
            if (settings.Paused || !settings.Convert.Enabled)
                continue;
            var state = await repository.GetConvertStateAsync(storeId);
            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.Convert.IntervalMinutes, ConvertSettings.MinIntervalMinutes, ConvertSettings.MaxIntervalMinutes));
            if (state.LastRun is { } last && clock.GetUtcNow() - last < interval)
                continue;
            try
            {
                await RunAsync(storeId, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // One store's failure must not stop the others; pending orders were saved before sending.
                logger.LogError(e, "Sweep & Sell convert run of store {StoreId} failed", storeId);
            }
        }
    }

    public async Task<ConvertRunResult> RunAsync(string storeId, CancellationToken cancellationToken)
    {
        var storeLock = StoreLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        if (!await storeLock.WaitAsync(0, cancellationToken))
            return ConvertRunResult.Busy;
        try
        {
            var settings = await repository.GetAsync(storeId);
            if (settings.Paused)
                return ConvertRunResult.Paused;
            if (!settings.Convert.Enabled)
                return ConvertRunResult.Disabled;
            var state = await repository.GetConvertStateAsync(storeId);
            state.LastRun = clock.GetUtcNow();
            try
            {
                await RunAsync(storeId, settings, state, cancellationToken);
            }
            finally
            {
                await repository.SaveConvertStateAsync(storeId, state);
            }
            return ConvertRunResult.Ran;
        }
        finally
        {
            storeLock.Release();
        }
    }

    async Task RunAsync(string storeId, SweepSellSettings settings, ConvertState state, CancellationToken cancellationToken)
    {
        var convert = settings.Convert;
        var exchange = exchanges.Find(settings.ExchangeId);
        var credentials = protector.Credentials(settings);
        if (exchange is null || credentials is null)
        {
            Record(storeId, state, null, ActivityOutcome.Failed, "No exchange key is saved (or it can't be read). Save one above.");
            return;
        }

        if (!await ReconcileAsync(storeId, state, exchange, credentials, cancellationToken))
            return;

        IReadOnlyDictionary<string, decimal> balances;
        try
        {
            balances = await exchange.GetAvailableBalancesAsync(credentials, cancellationToken);
        }
        catch (Exception e) when (IsExchangeFailure(e, cancellationToken))
        {
            Record(storeId, state, null, ActivityOutcome.Failed, $"Could not read balances from {exchange.Name}: {e.Message}");
            return;
        }

        var target = convert.TargetAsset;
        foreach (var asset in convert.Assets.Where(a => !string.Equals(a, target, StringComparison.OrdinalIgnoreCase)))
        {
            var available = balances.GetValueOrDefault(asset);
            if (available <= 0)
                continue;
            decimal amount;
            MarketRules rules;
            try
            {
                rules = await exchange.GetMarketRulesAsync(asset, target, cancellationToken);
                var price = await exchange.GetPriceAsync(asset, target, cancellationToken);
                amount = rules.Sellable(available, price);
                if (amount == 0)
                {
                    Record(storeId, state, asset, ActivityOutcome.Waiting,
                        $"{Amount(available)} {asset} is below {exchange.Name}'s minimum order of {Amount(rules.MinimumSellAmount(price))} {asset}. Waiting for more.");
                    continue;
                }
            }
            catch (Exception e) when (IsExchangeFailure(e, cancellationToken))
            {
                Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the {asset}/{target} market on {exchange.Name}: {e.Message}");
                continue;
            }

            if (convert.DryRun)
            {
                try
                {
                    var check = await exchange.SellAtMarketAsync(credentials, asset, target, amount, Guid.NewGuid(), validateOnly: true, cancellationToken);
                    Record(storeId, state, asset, ActivityOutcome.DryRun,
                        $"Dry run: would sell {Amount(amount)} {asset} for {target} ({check.Description}). {exchange.Name} accepted the order check; nothing was sold.");
                }
                catch (Exception e) when (IsExchangeFailure(e, cancellationToken))
                {
                    Record(storeId, state, asset, ActivityOutcome.Failed, $"Dry run: {exchange.Name} rejected selling {Amount(amount)} {asset} for {target}: {e.Message}");
                }
                continue;
            }

            // Saved before sending: if the response is lost or the server stops, the next run looks the order up first.
            var pending = new PendingOrder
            {
                ClientOrderId = Guid.NewGuid(),
                Asset = asset,
                TargetAsset = target,
                Amount = amount,
                SentAt = clock.GetUtcNow()
            };
            state.Pending.Add(pending);
            await repository.SaveConvertStateAsync(storeId, state);
            OrderResult result;
            try
            {
                result = await exchange.SellAtMarketAsync(credentials, asset, target, amount, pending.ClientOrderId, validateOnly: false, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // The order may or may not exist: keep it pending and stop selling until it is looked up.
                Record(storeId, state, asset, ActivityOutcome.Failed,
                    $"Selling {Amount(amount)} {asset} for {target} failed or got no answer: {e.Message}. The next run checks {exchange.Name} for the order before selling again.");
                return;
            }
            state.Pending.Remove(pending);
            Record(storeId, state, asset, ActivityOutcome.Sold,
                $"Sold {Amount(amount)} {asset} for {target} at market ({string.Join(", ", result.OrderIds)}).");
        }
    }

    /// <summary>Looks up every pending order. True when none is left, so selling may continue.</summary>
    async Task<bool> ReconcileAsync(string storeId, ConvertState state, IExchangeConnector exchange, ExchangeCredentials credentials,
        CancellationToken cancellationToken)
    {
        foreach (var pending in state.Pending.ToList())
        {
            OrderLookup? lookup;
            try
            {
                lookup = await exchange.FindOrderAsync(credentials, pending.ClientOrderId, cancellationToken);
            }
            catch (Exception e) when (IsExchangeFailure(e, cancellationToken))
            {
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"Could not look up the earlier {pending.Asset} sell on {exchange.Name}: {e.Message}. Nothing is sold until it is found.");
                return false;
            }
            if (lookup is null)
            {
                if (clock.GetUtcNow() - pending.SentAt < PendingGrace)
                {
                    Record(storeId, state, pending.Asset, ActivityOutcome.Waiting,
                        $"The earlier {pending.Asset} sell isn't on {exchange.Name} yet. Checking again on the next run.");
                    continue;
                }
                state.Pending.Remove(pending);
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"The earlier sell of {Amount(pending.Amount)} {pending.Asset} was never placed on {exchange.Name}. It will be tried again.");
                continue;
            }
            if (lookup.Open)
            {
                Record(storeId, state, pending.Asset, ActivityOutcome.Waiting,
                    $"The earlier {pending.Asset} sell ({string.Join(", ", lookup.OrderIds)}) is still open on {exchange.Name}.");
                continue;
            }
            state.Pending.Remove(pending);
            Record(storeId, state, pending.Asset, ActivityOutcome.Sold,
                $"Found the earlier sell on {exchange.Name}: {Amount(lookup.ExecutedAmount)} {pending.Asset} sold for {pending.TargetAsset} ({string.Join(", ", lookup.OrderIds)}).");
        }
        return state.Pending.Count == 0;
    }

    void Record(string storeId, ConvertState state, string? asset, ActivityOutcome outcome, string message)
    {
        // Waiting repeats every run until something changes; keep it out of the default log.
        var level = outcome switch
        {
            ActivityOutcome.Failed => LogLevel.Warning,
            ActivityOutcome.Waiting => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, "Sweep & Sell convert, store {StoreId}, asset {Asset}: {Outcome}: {Message}", storeId, asset ?? "-", outcome, message);
        state.Add(new ActivityEntry { At = clock.GetUtcNow(), Asset = asset, Outcome = outcome, Message = message });
    }

    static bool IsExchangeFailure(Exception e, CancellationToken cancellationToken) =>
        e is ExchangeException or HttpRequestException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
