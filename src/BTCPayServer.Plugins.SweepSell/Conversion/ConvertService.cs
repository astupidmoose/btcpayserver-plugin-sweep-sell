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
using BTCPayServer.Plugins.SweepSell.Sweeping;
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
    public static TimeSpan PendingGrace => PendingOrders.Grace;

    /// <summary>How many stores convert at the same time, so one slow exchange doesn't hold up the others.</summary>
    public const int MaxParallelStores = 4;

    static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new();

    /// <summary>Runs every store whose interval has passed.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var due = new List<string>();
        foreach (var (storeId, settings) in await repository.GetAllAsync())
        {
            if (settings.Paused || !settings.Convert.Enabled)
                continue;
            var state = await repository.GetConvertStateAsync(storeId);
            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.Convert.IntervalMinutes, ConvertSettings.MinIntervalMinutes, ConvertSettings.MaxIntervalMinutes));
            if (state.LastRun is not { } last || clock.GetUtcNow() - last >= interval)
                due.Add(storeId);
        }
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelStores, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(due, options, async (storeId, token) =>
        {
            try
            {
                await RunAsync(storeId, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                // One store's failure must not stop the others; pending orders were saved before sending.
                logger.LogError(e, "Sweep & Sell convert run of store {StoreId} failed", storeId);
            }
        });
    }

    public async Task<ConvertRunResult> RunAsync(string storeId, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
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
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // Anything unexpected still reaches the owner, not only the server log. Pending orders were saved before sending.
                logger.LogError(e, "Sweep & Sell convert run of store {StoreId} stopped on an unexpected error", storeId);
                Record(storeId, state, null, ActivityOutcome.Failed, $"The run stopped on an unexpected error: {e.Message}");
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
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, null, ActivityOutcome.Failed, $"Could not read balances from {exchange.Name}: {e.Message}");
            return;
        }

        var target = convert.TargetAsset;
        // What this run's dry-run checks would have sold, so the cap applies across assets as in a live run.
        // By UTC day, like the live totals: a dry run that crosses midnight gets the new day's cap.
        var simulated = new DailyTotals();
        var slippage = Math.Clamp(convert.MaxSlippagePercent, ConvertSettings.MinSlippageLimit, ConvertSettings.MaxSlippageLimit) / 100m;
        foreach (var asset in convert.Assets.Where(a => !string.Equals(a, target, StringComparison.OrdinalIgnoreCase)))
        {
            var available = balances.GetValueOrDefault(asset);
            if (available <= 0)
                continue;
            decimal amount, limitPrice, reference;
            try
            {
                var rules = await exchange.GetMarketRulesAsync(asset, target, cancellationToken);
                var price = await exchange.GetPriceAsync(asset, target, cancellationToken);
                // The floor is set from the 24-hour average, which one trade on a thin market can't drag down.
                reference = price.DailyAverage;
                if (reference <= 0)
                    throw new ExchangeException($"{exchange.Name} returned no average price for {asset}/{target}.");
                var floor = reference * (1 - slippage);
                if (price.Bid < floor)
                {
                    Record(storeId, state, asset, ActivityOutcome.Waiting,
                        $"The best bid for {asset} ({Amount(price.Bid)} {target}) is more than {Amount(slippage * 100)}% below the 24-hour average ({Amount(reference)} {target}). Waiting for the price to recover.");
                    continue;
                }
                limitPrice = rules.LimitPriceAtLeast(floor);
                var wanted = available;
                if (convert.DailyCap > 0)
                {
                    var left = convert.DailyCap - state.SoldOn(clock.GetUtcNow(), target) - simulated.On(clock.GetUtcNow(), target);
                    if (left <= 0)
                    {
                        Record(storeId, state, asset, ActivityOutcome.Waiting,
                            $"Today's cap of {Amount(convert.DailyCap)} {target} is reached. Selling resumes tomorrow (UTC).");
                        continue;
                    }
                    wanted = Math.Min(available, left / reference);
                }
                // Minimums are checked at the limit price: the lowest price the order can fill at.
                amount = rules.Sellable(wanted, limitPrice);
                if (amount == 0)
                {
                    Record(storeId, state, asset, ActivityOutcome.Waiting, wanted < available
                        ? $"What today's cap still allows of {asset} is below {exchange.Name}'s minimum order. Selling resumes tomorrow (UTC)."
                        : $"{Amount(available)} {asset} is below {exchange.Name}'s minimum order of {Amount(rules.MinimumSellAmount(limitPrice))} {asset}. Waiting for more.");
                    continue;
                }
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the {asset}/{target} market on {exchange.Name} to sell {Amount(available)} {asset}: {e.Message}");
                continue;
            }

            if (convert.DryRun)
            {
                try
                {
                    var check = await exchange.SellAsync(credentials, asset, target, amount, limitPrice, Guid.NewGuid(), validateOnly: true, cancellationToken);
                    simulated.Add(clock.GetUtcNow(), target, amount * reference);
                    Record(storeId, state, asset, ActivityOutcome.DryRun,
                        $"Dry run: would sell up to {Amount(amount)} {asset} for {target} at {Amount(limitPrice)} or better ({check.Description}). {exchange.Name} accepted the order check; nothing was sold.");
                }
                catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
                {
                    Record(storeId, state, asset, ActivityOutcome.Failed, $"Dry run: {exchange.Name} rejected selling {Amount(amount)} {asset} for {target}: {e.Message}");
                }
                continue;
            }

            // Saved before sending: if the response is lost or the server stops, the next run looks the order up first.
            var pending = new PendingOrder
            {
                ClientOrderId = Guid.NewGuid(),
                ExchangeId = exchange.Id,
                KeyFingerprint = credentials.Fingerprint(),
                Asset = asset,
                TargetAsset = target,
                Amount = amount,
                ReferencePrice = reference
            };
            OrderResult result;
            // Settings saves take the same lock, so a pause, a new key or other changes saved during the run apply before
            // anything more is sent, and the settings page can't report a pause while an order is going out.
            var settingsLock = SettingsLock(storeId);
            await settingsLock.WaitAsync(cancellationToken);
            try
            {
                if (!StillApplies(await repository.GetAsync(storeId), settings, asset, exchange, credentials))
                {
                    Record(storeId, state, null, ActivityOutcome.Waiting,
                        "The settings changed during the run (paused, conversion or its options changed, or another key); nothing more is sent this run.");
                    return;
                }
                // Stamped once the lock is held, just before sending: the fill's day and the lookup grace count from here.
                pending.SentAt = clock.GetUtcNow();
                state.Pending.Add(pending);
                await repository.SaveConvertStateAsync(storeId, state);
                try
                {
                    result = await exchange.SellAsync(credentials, asset, target, amount, limitPrice, pending.ClientOrderId, validateOnly: false, cancellationToken);
                }
                catch (ExchangeException e) when (e.Rejected)
                {
                    // The exchange refused the order outright, so there is nothing to look up later.
                    state.Pending.Remove(pending);
                    Record(storeId, state, asset, ActivityOutcome.Failed,
                        $"{exchange.Name} refused selling {Amount(amount)} {asset} for {target}: {e.Message}. Nothing was sold.");
                    continue;
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    // The order may or may not exist: keep it pending and stop selling until it is looked up.
                    Record(storeId, state, asset, ActivityOutcome.Failed,
                        $"Selling {Amount(amount)} {asset} for {target} failed or got no answer: {e.Message}. The next run checks {exchange.Name} for the order before selling again.");
                    return;
                }
            }
            finally
            {
                settingsLock.Release();
            }
            pending.OrderIds = result.OrderIds.ToList();
            // Saved straight away: once the exchange confirmed the order, a restart must never treat it as unplaced.
            await repository.SaveConvertStateAsync(storeId, state);

            // An immediate-or-cancel order closes straight away; look up how much filled, for the record and the cap.
            OrderLookup? fill = null;
            try
            {
                fill = await exchange.FindOrderAsync(credentials, pending.ClientOrderId, cancellationToken);
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, asset, ActivityOutcome.Failed,
                    $"Placed a sell of up to {Amount(amount)} {asset} for {target} at {Amount(limitPrice)} or better ({string.Join(", ", result.OrderIds)}), " +
                    $"but could not look up how much sold on {exchange.Name}: {e.Message}. It stays pending; the next run checks it before selling more.");
                return;
            }
            var orders = string.Join(", ", result.OrderIds);
            if (fill is null || fill.Open)
            {
                // Not listed or not closed yet: it stays pending, so its final fill is counted before anything more is sold.
                Record(storeId, state, asset, ActivityOutcome.Waiting,
                    $"Placed a sell of up to {Amount(amount)} {asset} for {target} at {Amount(limitPrice)} or better ({orders}). Waiting for {exchange.Name} to report it closed before selling more.");
                return;
            }
            if (PendingOrders.Implausible(fill, pending, exchange.Name) is { } bad)
            {
                Record(storeId, state, asset, ActivityOutcome.Failed, bad);
                return;
            }
            state.Pending.Remove(pending);
            if (fill.ExecutedAmount == 0)
            {
                Record(storeId, state, asset, ActivityOutcome.Waiting,
                    $"No buyer for {asset} at {Amount(limitPrice)} {target} or better; nothing was sold ({orders}). Trying again on the next run.");
            }
            else
            {
                state.AddSold(PendingOrders.CountedAt(pending, clock.GetUtcNow()), target, fill.ExecutedAmount * reference);
                Record(storeId, state, asset, ActivityOutcome.Sold,
                    $"Sold {Amount(fill.ExecutedAmount)} of {Amount(amount)} {asset} for {target} at {Amount(limitPrice)} or better ({orders}).");
            }
        }
    }

    /// <summary>Looks up every pending order. True when none is left, so selling may continue.</summary>
    Task<bool> ReconcileAsync(string storeId, ConvertState state, IExchangeConnector exchange, ExchangeCredentials credentials,
        CancellationToken cancellationToken) =>
        PendingOrders.ReconcileAsync(state.Pending, exchange, credentials, clock.GetUtcNow(),
            // Counted when it last could have filled, however late it is found.
            (order, executed) => state.AddSold(PendingOrders.CountedAt(order, clock.GetUtcNow()), order.TargetAsset, executed * order.ReferencePrice),
            (asset, outcome, message) => Record(storeId, state, asset, outcome, message),
            e => IsRecordedFailure(e, cancellationToken), cancellationToken);

    /// <summary>
    /// Drops a pending order the owner has checked on the exchange themselves. Waits for a running job of the store,
    /// so the removal can't be overwritten by it. False when there is no such order.
    /// </summary>
    public Task<bool> RemovePendingAsync(string storeId, Guid clientOrderId, CancellationToken cancellationToken) =>
        WithStoreLockAsync(storeId, async () =>
        {
            var state = await repository.GetConvertStateAsync(storeId);
            var pending = state.Pending.FirstOrDefault(p => p.ClientOrderId == clientOrderId);
            if (pending is null)
                return false;
            state.Pending.Remove(pending);
            // What it sold isn't known here, so its full value counts against today's cap (the day of removal, which an
            // order sent on an earlier day wouldn't touch): the cap is never exceeded.
            state.AddSold(clock.GetUtcNow(), pending.TargetAsset, pending.Amount * pending.ReferencePrice);
            Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                $"The pending sell of {Amount(pending.Amount)} {pending.Asset} (client order {clientOrderId:D}) was removed by hand. " +
                $"Its full value counts against today's cap, since what it sold isn't known. Selling resumes.");
            await repository.SaveConvertStateAsync(storeId, state);
            return true;
        }, cancellationToken);

    /// <summary>Runs <paramref name="action"/> while no convert job of the store runs, waiting for one in progress to end.</summary>
    public async Task<T> WithStoreLockAsync<T>(string storeId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> (a settings save) while no live order of the store is being sent. Waits at most for
    /// one order request, never for a whole run.
    /// </summary>
    public async Task<T> WithSettingsLockAsync<T>(string storeId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var settingsLock = SettingsLock(storeId);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            settingsLock.Release();
        }
    }

    /// <summary>Whether the settings saved now still allow the live sell a run planned with <paramref name="planned"/>.</summary>
    bool StillApplies(SweepSellSettings current, SweepSellSettings planned, string asset, IExchangeConnector exchange, ExchangeCredentials credentials)
    {
        var (now, then) = (current.Convert, planned.Convert);
        return !current.Paused && now.Enabled && !now.DryRun && current.ExchangeId == exchange.Id &&
               credentials.Equals(protector.Credentials(current)) && // the whole key: a replaced secret must not be used either
               now.TargetAsset == then.TargetAsset && now.Assets.Contains(asset) &&
               now.DailyCap == then.DailyCap && now.MaxSlippagePercent == then.MaxSlippagePercent;
    }

    static SemaphoreSlim StoreLock(string storeId) => StoreLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
    static SemaphoreSlim SettingsLock(string storeId) => SettingsGate.For(storeId);

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

    // Arithmetic failures (an absurd balance or price overflowing a decimal) are recorded per asset like exchange errors.
    static bool IsRecordedFailure(Exception e, CancellationToken cancellationToken) =>
        e is ExchangeException or HttpRequestException or ArithmeticException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
