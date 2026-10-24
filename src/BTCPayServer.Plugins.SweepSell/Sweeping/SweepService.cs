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
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SweepSell.Sweeping;

public enum SweepRunResult
{
    /// <summary>The run went through its steps; what happened is in the store's activity.</summary>
    Ran,
    Paused,
    Disabled,
    /// <summary>Another sweep run of the same store was in progress.</summary>
    Busy
}

/// <summary>
/// Sweeps each store's hot wallets to its exchange (and a share to a cold wallet), then converts at the exchange as
/// soon as the deposit is credited. Each signed sweep is saved as pending before it is broadcast, and only that same
/// transaction is ever rebroadcast, so a lost answer or a restart never sends twice.
/// </summary>
public class SweepService(ISweepSellRepository repository, SecretProtector protector, ExchangeRegistry exchanges, IStoreWallets wallets,
    ConvertService convert, TimeProvider clock, ILogger<SweepService> logger)
{
    /// <summary>How many stores sweep at the same time, so one slow wallet or exchange doesn't hold up the others.</summary>
    public const int MaxParallelStores = 4;

    /// <summary>How long a sweep's deposit is watched for on the exchange before the owner is asked to check it.</summary>
    public static readonly TimeSpan DepositWatchLimit = TimeSpan.FromHours(24);

    static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new();

    // A wallet can be set up in several stores; only one of them may build and broadcast from it at a time.
    static readonly ConcurrentDictionary<string, SemaphoreSlim> WalletLocks = new();

    /// <summary>Runs every store whose interval has passed.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var due = new List<string>();
        foreach (var (storeId, settings) in await repository.GetAllAsync())
        {
            if (settings.Paused || !settings.Sweep.Enabled)
                continue;
            var state = await repository.GetSweepStateAsync(storeId);
            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.Sweep.IntervalMinutes, SweepSettings.MinIntervalMinutes, SweepSettings.MaxIntervalMinutes));
            if (state.LastRun is not { } last || clock.GetUtcNow() - last >= interval)
                due.Add(storeId);
        }
        await ForEachStoreAsync(due, (storeId, token) => RunAsync(storeId, wait: false, token), cancellationToken);
    }

    /// <summary>
    /// A transaction for one of <paramref name="cryptoCode"/>'s wallets was seen or confirmed, or a block was found: sweeps
    /// the stores that sweep that coin straight away, so a balance is swept as soon as it reaches the minimum.
    /// </summary>
    public async Task OnChainActivityAsync(string cryptoCode, CancellationToken cancellationToken)
    {
        var stores = (await repository.GetAllAsync())
            .Where(s => !s.Value.Paused && s.Value.Sweep.Enabled && Rule(s.Value, cryptoCode) is not null)
            .Select(s => s.Key)
            .ToList();
        await ForEachStoreAsync(stores, (storeId, token) => RunAsync(storeId, wait: true, token), cancellationToken);
    }

    async Task ForEachStoreAsync(IReadOnlyList<string> storeIds, Func<string, CancellationToken, Task<SweepRunResult>> run, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelStores, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(storeIds, options, async (storeId, token) =>
        {
            try
            {
                await run(storeId, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                // One store's failure must not stop the others; pending sweeps were saved before broadcasting.
                logger.LogError(e, "Sweep & Sell sweep run of store {StoreId} failed", storeId);
            }
        });
    }

    /// <summary>
    /// Sweeps every enabled coin of the store. With <paramref name="wait"/>, waits for a run in progress instead of
    /// returning <see cref="SweepRunResult.Busy"/>, so a new transaction is never missed.
    /// </summary>
    public async Task<SweepRunResult> RunAsync(string storeId, bool wait, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        if (wait)
            await storeLock.WaitAsync(cancellationToken);
        else if (!await storeLock.WaitAsync(0, cancellationToken))
            return SweepRunResult.Busy;
        try
        {
            var settings = await repository.GetAsync(storeId);
            if (settings.Paused)
                return SweepRunResult.Paused;
            if (!settings.Sweep.Enabled)
                return SweepRunResult.Disabled;
            var state = await repository.GetSweepStateAsync(storeId);
            state.LastRun = clock.GetUtcNow();
            try
            {
                await RunAsync(storeId, settings, state, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                // Anything unexpected still reaches the owner, not only the server log. Pending sweeps were saved before broadcasting.
                logger.LogError(e, "Sweep & Sell sweep run of store {StoreId} stopped on an unexpected error", storeId);
                Record(storeId, state, null, ActivityOutcome.Failed, $"The sweep run stopped on an unexpected error: {e.Message}");
            }
            finally
            {
                await repository.SaveSweepStateAsync(storeId, state);
            }
            return SweepRunResult.Ran;
        }
        finally
        {
            storeLock.Release();
        }
    }

    async Task RunAsync(string storeId, SweepSellSettings settings, SweepState state, CancellationToken cancellationToken)
    {
        var exchange = exchanges.Find(settings.ExchangeId);
        var credentials = protector.Credentials(settings);
        if (exchange is null || credentials is null)
        {
            Record(storeId, state, null, ActivityOutcome.Failed, "No exchange key is saved (or it can't be read), so there is nowhere to sweep to. Save one above.");
            return;
        }
        foreach (var rule in settings.Sweep.Rules.Where(r => r.Enabled))
        {
            try
            {
                await SweepAsync(storeId, settings, rule, state, exchange, credentials, cancellationToken);
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, rule.CryptoCode, ActivityOutcome.Failed, $"Could not sweep {rule.CryptoCode}: {e.Message}");
            }
        }
    }

    async Task SweepAsync(string storeId, SweepSellSettings settings, SweepRule rule, SweepState state, IExchangeConnector exchange,
        ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        var code = rule.CryptoCode;
        if (state.Pending.FirstOrDefault(p => p.CryptoCode == code) is { } earlier && !await SettlePendingAsync(storeId, state, earlier, cancellationToken))
            return;

        var wallet = await wallets.GetWalletAsync(storeId, code, cancellationToken);
        if (wallet is null)
        {
            Record(storeId, state, code, ActivityOutcome.Failed, $"This store has no {code} on-chain wallet to sweep.");
            return;
        }
        if (!wallet.IsHot)
        {
            Record(storeId, state, code, ActivityOutcome.Failed,
                $"The {code} wallet isn't a hot wallet on this server, so it can't be swept automatically.");
            return;
        }

        var walletLock = WalletLocks.GetOrAdd($"{code}:{wallet.Key}", _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            var balance = await wallets.GetBalanceAsync(wallet, rule.IncludeUnconfirmed, cancellationToken);
            var coins = rule.IncludeUnconfirmed ? "" : " confirmed";
            if (balance <= 0 || balance < rule.MinimumBalance)
            {
                Record(storeId, state, code, ActivityOutcome.Waiting,
                    $"The wallet holds {Amount(balance)} {code}{coins}, below the minimum of {Amount(rule.MinimumBalance)} {code}. Waiting for more.");
                return;
            }
            var now = clock.GetUtcNow();
            decimal? amount = null;
            if (rule.DailyCap > 0)
            {
                var left = rule.DailyCap - state.Swept.On(now, code);
                if (left <= 0)
                {
                    Record(storeId, state, code, ActivityOutcome.Waiting,
                        $"Today's cap of {Amount(rule.DailyCap)} {code} is reached. Sweeping resumes tomorrow (UTC).");
                    return;
                }
                if (left < balance)
                    amount = left;
            }

            var minimumDeposit = await exchange.GetMinimumDepositAsync(credentials, code, cancellationToken);
            var deposit = await exchange.GetDepositAddressAsync(credentials, code, fresh: false, cancellationToken);
            if (deposit.RequiresTag)
            {
                Record(storeId, state, code, ActivityOutcome.Failed,
                    $"{exchange.Name} needs a tag or memo with {code} deposits, which an on-chain sweep can't attach. Nothing was sent.");
                return;
            }
            // Exchange answers are untrusted: the address must be valid for this coin's network before anything is sent to it.
            if (wallets.CheckAddress(code, deposit.Address) is { } badAddress)
            {
                Record(storeId, state, code, ActivityOutcome.Failed, $"{exchange.Name} returned an unusable deposit address. {badAddress} Nothing was sent.");
                return;
            }
            string? coldAddress = null;
            int? coldIndex = null;
            if (rule.ColdPercent > 0 && !string.IsNullOrWhiteSpace(rule.ColdDestination))
            {
                var index = Math.Max(rule.ColdStartIndex, state.NextColdIndex.GetValueOrDefault(ColdKey(rule.ColdDestination)));
                var (address, derived) = wallets.GetColdAddress(code, rule.ColdDestination, index);
                coldAddress = address;
                coldIndex = derived ? index : null;
            }
            var sweep = await wallets.BuildAsync(wallet,
                new SweepRequest(rule.IncludeUnconfirmed, amount, deposit.Address, coldAddress, coldAddress is null ? 0 : rule.ColdPercent,
                    Math.Clamp(settings.Sweep.FeeTargetBlocks, SweepSettings.MinFeeTargetBlocks, SweepSettings.MaxFeeTargetBlocks)),
                cancellationToken);
            if (sweep.Amounts.ToExchange < minimumDeposit)
            {
                Record(storeId, state, code, ActivityOutcome.Waiting,
                    $"Sweeping now would send {Amount(sweep.Amounts.ToExchange)} {code} to {exchange.Name}, below its minimum deposit of {Amount(minimumDeposit)} {code}. Waiting for more.");
                return;
            }
            var what = Describe(code, sweep.Amounts.ToExchange, exchange.Name, deposit.Address, sweep.Amounts.ToCold, coldAddress, sweep.Amounts.Fee);
            if (settings.Sweep.DryRun)
            {
                Record(storeId, state, code, ActivityOutcome.DryRun, $"Dry run: would send {what}. The transaction was signed but not broadcast.");
                return;
            }

            // Settings saves take the same lock, so a pause or change saved during the run applies before the broadcast.
            var settingsLock = SettingsGate.For(storeId);
            await settingsLock.WaitAsync(cancellationToken);
            try
            {
                if (!StillApplies(await repository.GetAsync(storeId), settings, rule, exchange, credentials))
                {
                    Record(storeId, state, null, ActivityOutcome.Waiting,
                        "The settings changed during the run (paused, sweeping or its options changed, or another key); nothing was broadcast this run.");
                    return;
                }
                var pending = new PendingSweep
                {
                    CryptoCode = code,
                    Txid = sweep.Txid,
                    Hex = sweep.Hex,
                    ExchangeId = exchange.Id,
                    ExchangeAddress = deposit.Address,
                    ToExchange = sweep.Amounts.ToExchange,
                    ColdAddress = coldAddress,
                    ToCold = sweep.Amounts.ToCold,
                    Fee = sweep.Amounts.Fee,
                    ColdDestination = coldIndex is null ? null : ColdKey(rule.ColdDestination!),
                    ColdIndex = coldIndex,
                    CreatedAt = now
                };
                // Saved before broadcasting: if the answer is lost or the server stops, the next run finds or rebroadcasts this same transaction.
                state.Pending.Add(pending);
                await repository.SaveSweepStateAsync(storeId, state);
                BroadcastOutcome outcome;
                try
                {
                    outcome = await wallets.BroadcastAsync(code, sweep.Hex, cancellationToken);
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested)
                {
                    Record(storeId, state, code, ActivityOutcome.Failed,
                        $"Broadcasting the sweep of {Amount(pending.Spent)} {code} ({pending.Txid}) failed or got no answer: {e.Message}. The next run checks for it and rebroadcasts the same transaction.");
                    return;
                }
                if (outcome.Accepted)
                {
                    Sent(storeId, state, pending, what);
                    return;
                }
                // This transaction was never broadcast before, so the node's refusal means nothing was sent.
                state.Pending.Remove(pending);
                Record(storeId, state, code, ActivityOutcome.Failed,
                    $"The node refused the sweep of {Amount(pending.Spent)} {code} ({pending.Txid}): {outcome.Error}. Nothing was sent.");
            }
            finally
            {
                settingsLock.Release();
            }
        }
        finally
        {
            walletLock.Release();
        }
    }

    /// <summary>
    /// Finds or rebroadcasts a sweep whose broadcast wasn't confirmed. True when it is settled and the coin may be swept
    /// again; a sweep the node refuses on rebroadcast stays pending for the owner, since it may still be on the network.
    /// </summary>
    async Task<bool> SettlePendingAsync(string storeId, SweepState state, PendingSweep pending, CancellationToken cancellationToken)
    {
        var code = pending.CryptoCode;
        var what = Describe(code, pending.ToExchange, pending.ExchangeId ?? "the exchange", pending.ExchangeAddress, pending.ToCold, pending.ColdAddress, pending.Fee);
        if (await wallets.GetStatusAsync(code, pending.Txid, cancellationToken) != TransactionStatus.Unknown)
        {
            Sent(storeId, state, pending, what);
            return true;
        }
        BroadcastOutcome outcome;
        try
        {
            outcome = await wallets.BroadcastAsync(code, pending.Hex, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            Record(storeId, state, code, ActivityOutcome.Failed,
                $"Could not rebroadcast the earlier sweep of {Amount(pending.Spent)} {code} ({pending.Txid}): {e.Message}. Nothing more is swept until it is found.");
            return false;
        }
        if (outcome.Accepted)
        {
            Sent(storeId, state, pending, what);
            return true;
        }
        Record(storeId, state, code, ActivityOutcome.Failed,
            $"The node refuses the earlier sweep of {Amount(pending.Spent)} {code} ({pending.Txid}): {outcome.Error}. It may still be on the network, so nothing more is swept. " +
            "Check the transaction in a block explorer, then remove it under Pending sweeps.");
        return false;
    }

    void Sent(string storeId, SweepState state, PendingSweep pending, string what)
    {
        state.Pending.Remove(pending);
        state.Swept.Add(pending.CreatedAt, pending.CryptoCode, pending.Spent);
        if (pending.ColdDestination is { } cold && pending.ColdIndex is { } index)
            state.NextColdIndex[cold] = Math.Max(state.NextColdIndex.GetValueOrDefault(cold), index + 1);
        if (pending.ToExchange > 0)
        {
            state.Awaiting.Add(new AwaitingDeposit
            {
                Asset = pending.CryptoCode,
                Txid = pending.Txid,
                Amount = pending.ToExchange,
                ExchangeId = pending.ExchangeId,
                SentAt = pending.CreatedAt
            });
        }
        Record(storeId, state, pending.CryptoCode, ActivityOutcome.Sent, $"Sent {what} ({pending.Txid}).");
    }

    /// <summary>
    /// Checks every sweep sent to the exchange. Once one is credited, the store converts straight away instead of waiting
    /// for its schedule. Runs every minute.
    /// </summary>
    public async Task WatchDepositsAsync(CancellationToken cancellationToken)
    {
        var watched = new List<string>();
        foreach (var storeId in (await repository.GetAllAsync()).Keys)
        {
            var state = await repository.GetSweepStateAsync(storeId);
            if (state.Awaiting.Count > 0 || state.ConvertRequested)
                watched.Add(storeId);
        }
        await ForEachStoreAsync(watched, async (storeId, token) =>
        {
            await WatchDepositsAsync(storeId, token);
            return SweepRunResult.Ran;
        }, cancellationToken);
    }

    async Task WatchDepositsAsync(string storeId, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        if (!await storeLock.WaitAsync(0, cancellationToken))
            return; // a sweep run of the store is in progress; checked again next minute
        try
        {
            var settings = await repository.GetAsync(storeId);
            var state = await repository.GetSweepStateAsync(storeId);
            try
            {
                var exchange = exchanges.Find(settings.ExchangeId);
                var credentials = protector.Credentials(settings);
                var now = clock.GetUtcNow();
                foreach (var awaiting in state.Awaiting.ToList())
                {
                    DepositStatus? status = null;
                    if (exchange is not null && credentials is not null && awaiting.ExchangeId == exchange.Id)
                    {
                        try
                        {
                            status = await exchange.FindDepositAsync(credentials, awaiting.Asset, awaiting.Txid, cancellationToken);
                        }
                        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
                        {
                            Record(storeId, state, awaiting.Asset, ActivityOutcome.Failed,
                                $"Could not check the {Amount(awaiting.Amount)} {awaiting.Asset} deposit ({awaiting.Txid}) on {exchange.Name}: {e.Message}. Trying again in a minute.");
                        }
                    }
                    switch (status?.State)
                    {
                        case DepositState.Credited:
                            state.Awaiting.Remove(awaiting);
                            state.ConvertRequested = true;
                            Record(storeId, state, awaiting.Asset, ActivityOutcome.Sent,
                                $"{exchange!.Name} credited {Amount(status.Amount)} {awaiting.Asset} from the sweep ({awaiting.Txid}).");
                            break;
                        case DepositState.Failed:
                            state.Awaiting.Remove(awaiting);
                            Record(storeId, state, awaiting.Asset, ActivityOutcome.Failed,
                                $"{exchange!.Name} reports the {Amount(awaiting.Amount)} {awaiting.Asset} deposit ({awaiting.Txid}) as failed. Contact {exchange.Name} about it.");
                            break;
                        default:
                            if (now - awaiting.SentAt >= DepositWatchLimit)
                            {
                                state.Awaiting.Remove(awaiting);
                                Record(storeId, state, awaiting.Asset, ActivityOutcome.Failed,
                                    $"The {Amount(awaiting.Amount)} {awaiting.Asset} deposit ({awaiting.Txid}) wasn't credited within {DepositWatchLimit.TotalHours:0} hours. " +
                                    "Check it on the exchange; the scheduled convert still sells it once it arrives.");
                            }
                            break;
                    }
                }
                if (state.ConvertRequested && !settings.Paused)
                {
                    if (!settings.Convert.Enabled)
                    {
                        state.ConvertRequested = false;
                    }
                    else
                    {
                        // Convert keeps its own locks; this lock only covers the sweep state.
                        var result = await convert.RunAsync(storeId, cancellationToken);
                        state.ConvertRequested = result == ConvertRunResult.Busy;
                    }
                }
            }
            finally
            {
                await repository.SaveSweepStateAsync(storeId, state);
            }
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Drops a pending sweep the owner has checked themselves. Its full amount counts against today's cap, since whether it
    /// went through isn't known here. False when there is no such sweep.
    /// </summary>
    public async Task<bool> RemovePendingAsync(string storeId, string txid, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            var state = await repository.GetSweepStateAsync(storeId);
            var pending = state.Pending.FirstOrDefault(p => p.Txid == txid);
            if (pending is null)
                return false;
            state.Pending.Remove(pending);
            state.Swept.Add(pending.CreatedAt, pending.CryptoCode, pending.Spent);
            Record(storeId, state, pending.CryptoCode, ActivityOutcome.Failed,
                $"The pending sweep of {Amount(pending.Spent)} {pending.CryptoCode} ({pending.Txid}) was removed by hand. " +
                "It counts against today's cap, since whether it went through isn't known. Sweeping resumes.");
            await repository.SaveSweepStateAsync(storeId, state);
            return true;
        }
        finally
        {
            storeLock.Release();
        }
    }

    /// <summary>
    /// Writes a change to where sweeps go (cold wallet or its share) to the sweep activity, so a changed destination is
    /// visible to everyone who manages the store, not only to whoever changed it.
    /// </summary>
    public async Task RecordDestinationChangesAsync(string storeId, SweepSettings before, SweepSettings after, CancellationToken cancellationToken)
    {
        var changes = after.Rules
            .Select(rule => (rule, old: before.Rules.FirstOrDefault(r => string.Equals(r.CryptoCode, rule.CryptoCode, StringComparison.OrdinalIgnoreCase))))
            .Where(c => (c.old?.ColdDestination ?? "") != (c.rule.ColdDestination ?? "") || (c.old?.ColdPercent ?? 0) != c.rule.ColdPercent ||
                        (c.old?.ColdStartIndex ?? 0) != c.rule.ColdStartIndex)
            .ToList();
        if (changes.Count == 0)
            return;
        var storeLock = StoreLock(storeId);
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            var state = await repository.GetSweepStateAsync(storeId);
            foreach (var (rule, old) in changes)
            {
                Record(storeId, state, rule.CryptoCode, ActivityOutcome.Changed,
                    $"Cold wallet changed from {Destination(old)} to {Destination(rule)}.");
            }
            await repository.SaveSweepStateAsync(storeId, state);
        }
        finally
        {
            storeLock.Release();
        }
    }

    static string Destination(SweepRule? rule) =>
        rule is null || rule.ColdPercent == 0 || string.IsNullOrWhiteSpace(rule.ColdDestination)
            ? "none"
            : $"{rule.ColdPercent.ToString(CultureInfo.InvariantCulture)}% to {rule.ColdDestination}" +
              (rule.ColdStartIndex > 0 ? $" from address {rule.ColdStartIndex}" : "");

    /// <summary>Whether the settings saved now still allow the broadcast a run planned with <paramref name="planned"/>.</summary>
    bool StillApplies(SweepSellSettings current, SweepSellSettings planned, SweepRule rule, IExchangeConnector exchange, ExchangeCredentials credentials)
    {
        var now = Rule(current, rule.CryptoCode);
        return !current.Paused && current.Sweep.Enabled && !current.Sweep.DryRun && current.ExchangeId == exchange.Id &&
               credentials.Equals(protector.Credentials(current)) && // the whole key: a replaced secret must not be used either
               now is not null && now.ColdPercent == rule.ColdPercent && now.ColdDestination == rule.ColdDestination &&
               now.ColdStartIndex == rule.ColdStartIndex && now.DailyCap == rule.DailyCap && now.IncludeUnconfirmed == rule.IncludeUnconfirmed &&
               now.MinimumBalance == rule.MinimumBalance && current.Sweep.FeeTargetBlocks == planned.Sweep.FeeTargetBlocks;
    }

    static SweepRule? Rule(SweepSellSettings settings, string cryptoCode) =>
        settings.Sweep.Rules.FirstOrDefault(r => r.Enabled && string.Equals(r.CryptoCode, cryptoCode, StringComparison.OrdinalIgnoreCase));

    static string ColdKey(string destination) => destination.Trim();

    static string Describe(string code, decimal toExchange, string exchangeName, string exchangeAddress, decimal toCold, string? coldAddress, decimal fee) =>
        $"{Amount(toExchange)} {code} to {exchangeName} ({exchangeAddress})" +
        (toCold > 0 ? $" and {Amount(toCold)} {code} to the cold wallet ({coldAddress})" : "") +
        $", fee {Amount(fee)} {code}";

    void Record(string storeId, SweepState state, string? asset, ActivityOutcome outcome, string message)
    {
        // Waiting repeats every run until something changes; keep it out of the default log.
        var level = outcome switch
        {
            ActivityOutcome.Failed or ActivityOutcome.Changed => LogLevel.Warning,
            ActivityOutcome.Waiting => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, "Sweep & Sell sweep, store {StoreId}, coin {Asset}: {Outcome}: {Message}", storeId, asset ?? "-", outcome, message);
        state.Add(new ActivityEntry { At = clock.GetUtcNow(), Asset = asset, Outcome = outcome, Message = message });
    }

    static SemaphoreSlim StoreLock(string storeId) => StoreLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));

    // Arithmetic failures (an absurd balance overflowing a decimal) are recorded per coin like exchange and wallet errors.
    static bool IsRecordedFailure(Exception e, CancellationToken cancellationToken) =>
        e is ExchangeException or SweepException or HttpRequestException or ArithmeticException ||
        e is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
