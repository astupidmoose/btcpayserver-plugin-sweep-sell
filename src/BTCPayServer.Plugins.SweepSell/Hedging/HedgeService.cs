#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Plugins.SweepSell.Sweeping;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Hedging;

public enum HedgeRunResult
{
    /// <summary>The run went through its steps; what happened is in the store's activity.</summary>
    Ran,
    Paused,
    Disabled,
    /// <summary>Another hedge run of the same store was in progress.</summary>
    Busy
}

/// <summary>
/// Sells the BTC each paid invoice brought in, on the store's exchange, as soon as the invoice reaches the chosen status.
/// Each invoice is recorded once, by its ID, with the BTC still to sell; sells are immediate-or-cancel limits at a floor
/// below the 24-hour average, sent only from BTC already on the exchange (never on credit), and every live order is
/// saved as pending before it is sent, like convert's.
/// </summary>
public class HedgeService(ISweepSellRepository repository, SecretProtector protector, ExchangeRegistry exchanges, IStoreInvoices invoices,
    TimeProvider clock, ILogger<HedgeService> logger)
{
    public const string Asset = "BTC";

    /// <summary>Invoices created longer ago than this are never hedged, so a forgotten old invoice can't trigger a sell.</summary>
    public static readonly TimeSpan MaxInvoiceAge = TimeSpan.FromDays(30);

    /// <summary>How often recent invoices are listed again, to catch any whose event was missed.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(10);

    public const int MaxParallelStores = 4;

    static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new();

    /// <summary>
    /// Every minute: runs stores with BTC still to sell or orders to look up, and lists recent invoices of each store
    /// every <see cref="ScanInterval"/>.
    /// </summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var due = new List<string>();
        foreach (var (storeId, settings) in await repository.GetAllAsync())
        {
            if (settings.Paused || !settings.Hedge.Enabled)
                continue;
            var state = await repository.GetHedgeStateAsync(storeId);
            if (ScanDue(state) || state.Pending.Count > 0 || state.Invoices.Values.Any(i => i.Remaining > 0))
                due.Add(storeId);
        }
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelStores, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(due, options, async (storeId, token) =>
        {
            try
            {
                await RunAsync(storeId, wait: false, null, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                logger.LogError(e, "Sweep & Sell hedge run of store {StoreId} failed", storeId);
            }
        });
    }

    /// <summary>An invoice changed: hedges it straight away if it reached the store's trigger status.</summary>
    public async Task OnInvoiceAsync(string invoiceId, CancellationToken cancellationToken)
    {
        // Read again rather than trusting the event's copy, so the payments counted are the saved ones.
        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice is null || invoice.Status == HedgeInvoiceStatus.Other)
            return;
        var settings = await repository.GetAsync(invoice.StoreId);
        if (settings.Paused || !settings.Hedge.Enabled || !Reached(invoice, settings.Hedge))
            return;
        await RunAsync(invoice.StoreId, wait: true, [invoice], cancellationToken);
    }

    /// <summary>Hedges the store's paid invoices now (the Hedge now button), listing recent invoices whatever the scan interval.</summary>
    public Task<HedgeRunResult> RunAsync(string storeId, CancellationToken cancellationToken) => RunAsync(storeId, wait: false, null, cancellationToken, scan: true);

    async Task<HedgeRunResult> RunAsync(string storeId, bool wait, IReadOnlyList<HedgeInvoice>? found, CancellationToken cancellationToken, bool scan = false)
    {
        var storeLock = StoreLock(storeId);
        if (wait)
            await storeLock.WaitAsync(cancellationToken);
        else if (!await storeLock.WaitAsync(0, cancellationToken))
            return HedgeRunResult.Busy;
        try
        {
            var settings = await repository.GetAsync(storeId);
            if (settings.Paused)
                return HedgeRunResult.Paused;
            if (!settings.Hedge.Enabled)
                return HedgeRunResult.Disabled;
            var state = await repository.GetHedgeStateAsync(storeId);
            state.LastRun = clock.GetUtcNow();
            try
            {
                await RunAsync(storeId, settings, state, found, scan, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "Sweep & Sell hedge run of store {StoreId} stopped on an unexpected error", storeId);
                Record(storeId, state, null, ActivityOutcome.Failed, $"The hedge run stopped on an unexpected error: {e.Message}");
            }
            finally
            {
                await repository.SaveHedgeStateAsync(storeId, state);
            }
            return HedgeRunResult.Ran;
        }
        finally
        {
            storeLock.Release();
        }
    }

    async Task RunAsync(string storeId, SweepSellSettings settings, HedgeState state, IReadOnlyList<HedgeInvoice>? found, bool scan, CancellationToken cancellationToken)
    {
        var hedge = settings.Hedge;
        var now = clock.GetUtcNow();
        state.Prune(now - HedgeState.KeepHedged);
        if (found is null && (scan || ScanDue(state)))
        {
            var since = new[] { now - MaxInvoiceAge, hedge.EnabledSince ?? now }.Max();
            try
            {
                found = await invoices.GetPaidSinceAsync(storeId, since, cancellationToken);
                state.LastScan = now;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                Record(storeId, state, null, ActivityOutcome.Failed, $"Could not list the store's paid invoices: {e.Message}. Trying again in a minute.");
            }
        }
        foreach (var invoice in found ?? [])
            Take(storeId, hedge, state, invoice, now);

        if (!state.Invoices.Values.Any(i => i.Remaining > 0) && state.Pending.Count == 0)
            return;

        var exchange = exchanges.Find(settings.ExchangeId);
        var credentials = protector.Credentials(settings);
        if (exchange is null || credentials is null)
        {
            Record(storeId, state, null, ActivityOutcome.Failed, "No exchange key is saved (or it can't be read), so nothing can be hedged. Save one above.");
            return;
        }

        if (!await PendingOrders.ReconcileAsync(state.Pending, exchange, credentials, now,
                (order, executed) => Sold(state, order, order.SentAt, executed),
                (asset, outcome, message) => Record(storeId, state, asset, outcome, message),
                e => IsRecordedFailure(e, cancellationToken), cancellationToken))
            return;

        // Only once every earlier order's fill is counted against the invoices it was for: moving an invoice to another market
        // first would leave a late fill unallocated, and the invoice sold again in its new market.
        foreach (var (id, invoice) in state.Invoices.Where(i => i.Value.Remaining > 0).ToList())
        {
            // Taken before hedging was last turned on: turning it on again starts afresh, so what was left isn't sold now.
            if (invoice.EnabledSince != hedge.EnabledSince)
            {
                invoice.Remaining = 0;
                Record(storeId, state, Asset, ActivityOutcome.Waiting, $"Invoice {id} was taken before hedging was last turned on, so what was left of it isn't sold.");
            }
            // Taken while in dry run, so the owner never saw it as a live sell; selling it now would surprise.
            else if (invoice.DryRun && !hedge.DryRun)
            {
                invoice.Remaining = 0;
                Record(storeId, state, Asset, ActivityOutcome.Waiting, $"Invoice {id} was taken while hedging was in dry run, so it isn't sold now that it is live.");
            }
            // Taken live: a dry run would neither check nor sell it, and turning dry run off again must not sell it untested.
            else if (!invoice.DryRun && hedge.DryRun)
            {
                invoice.Remaining = 0;
                Record(storeId, state, Asset, ActivityOutcome.Waiting,
                    $"What was left of invoice {id} ({Amount(invoice.Amount)} BTC taken while hedging was live) isn't sold now that hedging is in dry run.");
            }
            // Taken under a trigger or an amount to hedge that no longer applies: taken again under the current settings (a
            // Processing invoice once it settles), or, if part of it was sold already, the rest isn't sold.
            else if ((invoice.TakenProcessing && hedge.Trigger == HedgeTrigger.Settled) || invoice.Basis != hedge.Amount)
            {
                var why = invoice.Basis != hedge.Amount ? "the amount to hedge changed" : "hedging now waits for Settled invoices";
                if (invoice.Remaining == invoice.Amount)
                {
                    state.Invoices.Remove(id);
                    state.LastScan = null; // so the next run finds it again
                    Record(storeId, state, Asset, ActivityOutcome.Waiting, invoice.Basis != hedge.Amount
                        ? $"Invoice {id} is taken again with the new amount to hedge."
                        : $"Invoice {id} isn't sold until it settles, as hedging now waits for Settled invoices.");
                }
                else
                {
                    invoice.Remaining = 0;
                    Record(storeId, state, Asset, ActivityOutcome.Waiting, $"Part of invoice {id} was sold already; the rest isn't sold, as {why}.");
                }
            }
            // The market for its currency changed since it was taken: what is left sells where the settings now say.
            else if (invoice.Currency is { } currency && hedge.MarketFor(currency).ToUpperInvariant() is var market && market != invoice.Market)
            {
                if (market == Asset)
                {
                    invoice.Remaining = 0;
                    Record(storeId, state, Asset, ActivityOutcome.Waiting,
                        $"Invoice {id} was to be sold for {invoice.Market}, but {currency} invoices have no market now, so what was left of it isn't sold.");
                }
                else
                {
                    Record(storeId, state, Asset, ActivityOutcome.Waiting,
                        $"What is left of invoice {id} ({Amount(invoice.Remaining)} BTC) now sells for {market} instead of {invoice.Market}, as the settings changed.");
                    invoice.Market = market;
                }
            }
        }

        var markets = state.Invoices.Values.Where(i => i.Remaining > 0 && i.DryRun == hedge.DryRun).Select(i => i.Market).Distinct().Order().ToList();
        if (markets.Count == 0)
            return;

        decimal available;
        try
        {
            available = (await exchange.GetAvailableBalancesAsync(credentials, cancellationToken)).GetValueOrDefault(Asset);
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, Asset, ActivityOutcome.Failed, $"Could not read balances from {exchange.Name}: {e.Message}");
            return;
        }

        var slippage = Math.Clamp(hedge.MaxSlippagePercent, ConvertSettings.MinSlippageLimit, ConvertSettings.MaxSlippageLimit) / 100m;
        // What this run's dry-run checks would have sold, so the cap applies across markets as in a live run.
        // By UTC day, like the live totals: a dry run that crosses midnight gets the new day's cap.
        var simulated = new DailyTotals();
        foreach (var market in markets)
        {
            var queued = state.Queued(market, hedge.DryRun, hedge.EnabledSince).Sum(i => i.Value.Remaining);
            decimal amount, limitPrice, reference;
            try
            {
                var rules = await exchange.GetMarketRulesAsync(Asset, market, cancellationToken);
                var price = await exchange.GetPriceAsync(Asset, market, cancellationToken);
                reference = price.DailyAverage;
                if (reference <= 0)
                    throw new ExchangeException($"{exchange.Name} returned no average price for {Asset}/{market}.");
                var floor = reference * (1 - slippage);
                if (price.Bid < floor)
                {
                    Record(storeId, state, Asset, ActivityOutcome.Waiting,
                        $"{Amount(queued)} BTC is waiting to be sold for {market}, but the best bid ({Amount(price.Bid)} {market}) is more than {Amount(slippage * 100)}% below the 24-hour average ({Amount(reference)} {market}). Waiting for the price to recover.");
                    continue;
                }
                limitPrice = rules.LimitPriceAtLeast(floor);
                var wanted = Math.Min(queued, available);
                var capped = false;
                if (hedge.DailyCap > 0)
                {
                    // The clock is read again: a run that crosses midnight counts against the day its orders are recorded on.
                    var left = hedge.DailyCap - state.Sold.On(clock.GetUtcNow(), Asset) - simulated.On(clock.GetUtcNow(), Asset);
                    if (left < wanted)
                    {
                        wanted = Math.Max(left, 0);
                        capped = true;
                    }
                }
                amount = rules.Sellable(wanted, limitPrice);
                if (amount == 0)
                {
                    var minimum = rules.MinimumSellAmount(limitPrice);
                    Record(storeId, state, Asset, ActivityOutcome.Waiting,
                        capped ? $"{Amount(queued)} BTC is waiting to be sold for {market}, but today's cap of {Amount(hedge.DailyCap)} BTC doesn't leave enough for {exchange.Name}'s minimum order. Selling resumes tomorrow (UTC)."
                        : queued < minimum ? $"{Amount(queued)} BTC is waiting to be sold for {market}, below {exchange.Name}'s minimum order of {Amount(minimum)} BTC. Waiting for more paid invoices."
                        : $"{Amount(queued)} BTC is waiting to be sold for {market}, but {exchange.Name} only has {Amount(available)} BTC available. Waiting for BTC to arrive there (hedges never borrow).");
                    continue;
                }
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, Asset, ActivityOutcome.Failed, $"Could not read the BTC/{market} market on {exchange.Name} to sell {Amount(queued)} BTC: {e.Message}");
                continue;
            }

            if (hedge.DryRun)
            {
                try
                {
                    var check = await exchange.SellAsync(credentials, Asset, market, amount, limitPrice, Guid.NewGuid(), validateOnly: true, cancellationToken);
                    state.Allocate(market, amount, hedge.EnabledSince, dryRun: true);
                    available -= amount;
                    simulated.Add(clock.GetUtcNow(), Asset, amount);
                    Record(storeId, state, Asset, ActivityOutcome.DryRun,
                        $"Dry run: would sell up to {Amount(amount)} BTC for {market} at {Amount(limitPrice)} or better ({check.Description}). {exchange.Name} accepted the order check; nothing was sold.");
                }
                catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
                {
                    Record(storeId, state, Asset, ActivityOutcome.Failed, $"Dry run: {exchange.Name} rejected selling {Amount(amount)} BTC for {market}: {e.Message}");
                }
                continue;
            }

            var pending = new PendingOrder
            {
                ClientOrderId = Guid.NewGuid(),
                ExchangeId = exchange.Id,
                KeyFingerprint = credentials.Fingerprint(),
                Asset = Asset,
                TargetAsset = market,
                Amount = amount,
                ReferencePrice = reference,
                SentAt = clock.GetUtcNow(),
                HedgeEnabledSince = hedge.EnabledSince
            };
            OrderResult result;
            // Settings saves take the same lock, so a pause or change saved during the run applies before anything is sent.
            var settingsLock = SettingsGate.For(storeId);
            await settingsLock.WaitAsync(cancellationToken);
            try
            {
                if (!StillApplies(await repository.GetAsync(storeId), settings, exchange, credentials))
                {
                    Record(storeId, state, null, ActivityOutcome.Waiting,
                        "The settings changed during the run (paused, hedging or its options changed, or another key); nothing more is sent this run.");
                    return;
                }
                // Saved before sending: if the response is lost or the server stops, the next run looks the order up first.
                state.Pending.Add(pending);
                await repository.SaveHedgeStateAsync(storeId, state);
                try
                {
                    result = await exchange.SellAsync(credentials, Asset, market, amount, limitPrice, pending.ClientOrderId, validateOnly: false, cancellationToken);
                }
                catch (ExchangeException e) when (e.Rejected)
                {
                    state.Pending.Remove(pending);
                    Record(storeId, state, Asset, ActivityOutcome.Failed,
                        $"{exchange.Name} refused selling {Amount(amount)} BTC for {market}: {e.Message}. Nothing was sold.");
                    continue;
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    Record(storeId, state, Asset, ActivityOutcome.Failed,
                        $"Selling {Amount(amount)} BTC for {market} failed or got no answer: {e.Message}. The next run checks {exchange.Name} for the order before selling again.");
                    return;
                }
            }
            finally
            {
                settingsLock.Release();
            }
            pending.OrderIds = result.OrderIds.ToList();
            // Saved straight away: once the exchange confirmed the order, a restart must never treat it as unplaced.
            await repository.SaveHedgeStateAsync(storeId, state);
            var orders = string.Join(", ", result.OrderIds);

            OrderLookup? fill;
            try
            {
                fill = await exchange.FindOrderAsync(credentials, pending.ClientOrderId, cancellationToken);
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, Asset, ActivityOutcome.Failed,
                    $"Placed a sell of up to {Amount(amount)} BTC for {market} at {Amount(limitPrice)} or better ({orders}), " +
                    $"but could not look up how much sold on {exchange.Name}: {e.Message}. It stays pending; the next run checks it before selling more.");
                return;
            }
            if (fill is null || fill.Open)
            {
                Record(storeId, state, Asset, ActivityOutcome.Waiting,
                    $"Placed a sell of up to {Amount(amount)} BTC for {market} at {Amount(limitPrice)} or better ({orders}). Waiting for {exchange.Name} to report it closed before selling more.");
                return;
            }
            if (PendingOrders.Implausible(fill, pending, exchange.Name) is { } bad)
            {
                Record(storeId, state, Asset, ActivityOutcome.Failed, bad);
                return;
            }
            state.Pending.Remove(pending);
            if (fill.ExecutedAmount == 0)
            {
                Record(storeId, state, Asset, ActivityOutcome.Waiting,
                    $"No buyer for BTC at {Amount(limitPrice)} {market} or better; nothing was sold ({orders}). Trying again on the next run.");
                continue;
            }
            Sold(state, pending, pending.SentAt, fill.ExecutedAmount);
            available -= fill.ExecutedAmount;
            Record(storeId, state, Asset, ActivityOutcome.Sold,
                $"Sold {Amount(fill.ExecutedAmount)} of {Amount(amount)} BTC for {market} at {Amount(limitPrice)} or better ({orders}).");
        }
    }

    /// <summary>Records <paramref name="invoice"/> for hedging if it qualifies and hasn't been taken before.</summary>
    void Take(string storeId, HedgeSettings hedge, HedgeState state, HedgeInvoice invoice, DateTimeOffset now)
    {
        if (invoice.StoreId != storeId || state.Invoices.ContainsKey(invoice.Id) || !Reached(invoice, hedge) ||
            hedge.EnabledSince is not { } since || invoice.CreatedAt < since || invoice.CreatedAt < now - MaxInvoiceAge)
            return;
        var market = hedge.MarketFor(invoice.Currency).ToUpperInvariant();
        var amount = hedge.Amount == HedgeAmount.InvoiceValue ? invoice.BtcValue : invoice.BtcPaid;
        // Recorded even when there is nothing to sell, so the invoice is decided once and never looked at again.
        var entry = new HedgedInvoice
        {
            Market = market,
            Currency = invoice.Currency.ToUpperInvariant(),
            DryRun = hedge.DryRun,
            EnabledSince = hedge.EnabledSince,
            TakenProcessing = invoice.Status == HedgeInvoiceStatus.Processing,
            Basis = hedge.Amount,
            CreatedAt = invoice.CreatedAt,
            AddedAt = now
        };
        state.Invoices[invoice.Id] = entry;
        if (amount is null)
        {
            Record(storeId, state, Asset, ActivityOutcome.Failed, $"Invoice {invoice.Id} has no BTC rate, so its value in BTC is unknown. It isn't hedged.");
            return;
        }
        if (market == Asset)
        {
            Record(storeId, state, Asset, ActivityOutcome.Failed,
                $"Invoice {invoice.Id} is priced in BTC and no market is set for it, so it isn't hedged. Set a default market to hedge BTC invoices.");
            return;
        }
        // Whole satoshis: the exchange rounds amounts down anyway.
        entry.Amount = entry.Remaining = Math.Floor(Math.Max(amount.Value, 0) * 100_000_000m) / 100_000_000m;
        if (entry.Amount > 0)
            Record(storeId, state, Asset, ActivityOutcome.Waiting, $"Invoice {invoice.Id} brought in {Amount(entry.Amount)} BTC to sell for {market}.");
    }

    static bool Reached(HedgeInvoice invoice, HedgeSettings hedge) => invoice.Status switch
    {
        HedgeInvoiceStatus.Settled => true,
        HedgeInvoiceStatus.Processing => hedge.Trigger == HedgeTrigger.Processing,
        _ => false
    };

    bool ScanDue(HedgeState state) => state.LastScan is not { } last || clock.GetUtcNow() - last >= ScanInterval;

    /// <summary>
    /// Counts what <paramref name="order"/> sold against the cap on the day of <paramref name="at"/>, and against the
    /// invoices it was sent for: those of its market and hedging period. Invoices of an earlier period were already
    /// dropped when hedging was turned on again, so a fill found after that sells for none of today's invoices.
    /// </summary>
    static void Sold(HedgeState state, PendingOrder order, DateTimeOffset at, decimal executed)
    {
        state.Sold.Add(at, Asset, executed);
        state.Allocate(order.TargetAsset, executed, order.HedgeEnabledSince);
    }

    /// <summary>
    /// Drops a pending order the owner has checked on the exchange themselves. It counts as fully sold: selling for the
    /// same invoices again could hedge them twice. False when there is no such order.
    /// </summary>
    public async Task<bool> RemovePendingAsync(string storeId, Guid clientOrderId, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            var state = await repository.GetHedgeStateAsync(storeId);
            var pending = state.Pending.FirstOrDefault(p => p.ClientOrderId == clientOrderId);
            if (pending is null)
                return false;
            state.Pending.Remove(pending);
            // Counted against the day of removal: one sent on an earlier day wouldn't touch today's cap.
            Sold(state, pending, clock.GetUtcNow(), pending.Amount);
            Record(storeId, state, Asset, ActivityOutcome.Failed,
                $"The pending sell of {Amount(pending.Amount)} BTC for {pending.TargetAsset} (client order {clientOrderId:D}) was removed by hand. " +
                "It counts as fully sold, for its invoices and today's cap, so they can't be hedged twice. Hedging resumes.");
            await repository.SaveHedgeStateAsync(storeId, state);
            return true;
        }
        finally
        {
            storeLock.Release();
        }
    }

    bool StillApplies(SweepSellSettings current, SweepSellSettings planned, IExchangeConnector exchange, ExchangeCredentials credentials)
    {
        var (now, then) = (current.Hedge, planned.Hedge);
        return !current.Paused && now.Enabled && !now.DryRun && current.ExchangeId == exchange.Id &&
               credentials.Equals(protector.Credentials(current)) && // the whole key: a replaced secret must not be used either
               now.EnabledSince == then.EnabledSince && // turned off and on again: the invoices this run planned for are dropped
               now.Trigger == then.Trigger && now.Amount == then.Amount && // which invoices count, and how much of each
               now.MaxSlippagePercent == then.MaxSlippagePercent && now.DailyCap == then.DailyCap &&
               now.DefaultMarket == then.DefaultMarket && now.Markets.Count == then.Markets.Count &&
               now.Markets.All(m => then.Markets.TryGetValue(m.Key, out var market) && market == m.Value);
    }

    static SemaphoreSlim StoreLock(string storeId) => StoreLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));

    void Record(string storeId, HedgeState state, string? asset, ActivityOutcome outcome, string message)
    {
        var level = outcome switch
        {
            ActivityOutcome.Failed => LogLevel.Warning,
            ActivityOutcome.Waiting => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, "Sweep & Sell hedge, store {StoreId}: {Outcome}: {Message}", storeId, outcome, message);
        state.Add(new ActivityEntry { At = clock.GetUtcNow(), Asset = asset, Outcome = outcome, Message = message });
    }

    static bool IsRecordedFailure(Exception e, CancellationToken cancellationToken) =>
        e is ExchangeException or HttpRequestException or ArithmeticException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
