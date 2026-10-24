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

namespace BTCPayServer.Plugins.SweepSell.Payout;

public enum PayoutRunResult
{
    /// <summary>The run went through its steps; what happened is in the store's payout activity.</summary>
    Ran,
    Paused,
    Disabled,
    /// <summary>Another payout run of the same store was in progress.</summary>
    Busy
}

/// <summary>
/// Withdraws a store's exchange balance of one asset to a destination the owner saved in the exchange account, with a
/// separate withdraw-only key. At most one withdrawal per run, within a daily cap, a minimum and a highest fee. Each
/// withdrawal is saved as pending before it is sent; exchanges take no client ID for withdrawals, so one whose response
/// is lost is looked for in the exchange's recent withdrawals and never sent again blindly.
/// </summary>
public class PayoutService(IPayoutRepository payouts, ISweepSellRepository repository, SecretProtector protector, WithdrawalRegistry withdrawals,
    TimeProvider clock, ILogger<PayoutService> logger)
{
    /// <summary>How far before its saved send time a withdrawal may be listed: the exchange's clock can differ from ours.</summary>
    public static readonly TimeSpan MatchBefore = TimeSpan.FromMinutes(2);

    /// <summary>How long after its saved send time a lost withdrawal may be listed and still be taken for it.</summary>
    public static readonly TimeSpan MatchAfter = TimeSpan.FromHours(1);

    /// <summary>How long a lost withdrawal may be missing from the exchange's list before the owner is asked to check.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(5);

    /// <summary>How far back withdrawals are read, to find lost ones and those still in progress.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(30);

    public const int MaxParallelStores = 4;

    static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks = new();

    /// <summary>Runs every store whose interval has passed.</summary>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var due = new List<string>();
        foreach (var (storeId, payout) in await payouts.GetAllAsync())
        {
            if (!payout.Enabled || (await repository.GetAsync(storeId)).Paused)
                continue;
            var state = await payouts.GetStateAsync(storeId);
            var interval = TimeSpan.FromMinutes(Math.Clamp(payout.IntervalMinutes, PayoutSettings.MinIntervalMinutes, PayoutSettings.MaxIntervalMinutes));
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
                logger.LogError(e, "Sweep & Sell payout run of store {StoreId} failed", storeId);
            }
        });
    }

    public async Task<PayoutRunResult> RunAsync(string storeId, CancellationToken cancellationToken)
    {
        var storeLock = StoreLock(storeId);
        if (!await storeLock.WaitAsync(0, cancellationToken))
            return PayoutRunResult.Busy;
        try
        {
            var settings = await repository.GetAsync(storeId);
            var payout = await payouts.GetAsync(storeId);
            if (settings.Paused)
                return PayoutRunResult.Paused;
            if (!payout.Enabled)
                return PayoutRunResult.Disabled;
            var state = await payouts.GetStateAsync(storeId);
            state.LastRun = clock.GetUtcNow();
            try
            {
                await RunAsync(storeId, settings, payout, state, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(e, "Sweep & Sell payout run of store {StoreId} stopped on an unexpected error", storeId);
                Record(storeId, state, payout.Asset, ActivityOutcome.Failed, $"The run stopped on an unexpected error: {e.Message}");
            }
            finally
            {
                await payouts.SaveStateAsync(storeId, state);
            }
            return PayoutRunResult.Ran;
        }
        finally
        {
            storeLock.Release();
        }
    }

    async Task RunAsync(string storeId, SweepSellSettings settings, PayoutSettings payout, PayoutState state, CancellationToken cancellationToken)
    {
        var asset = payout.Asset;
        var connector = withdrawals.Find(payout.ExchangeId);
        var credentials = protector.Credentials(payout);
        if (connector is null || credentials is null)
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, "No payout key is saved (or it can't be read). Save one above.");
            return;
        }
        if (IsTradingKey(settings, credentials))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed,
                "The payout key is the store's trading key. Payout needs its own key, with only Query Funds and Withdraw Funds.");
            return;
        }
        if (payout.DailyCap <= 0 || string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(payout.Destination))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, "Payout needs an asset, a saved destination and a daily cap. Set them above.");
            return;
        }
        // Checked every run, not only by the key test: permissions can be added to a key after it is saved.
        try
        {
            if (await connector.GetKeyProblemsAsync(credentials, cancellationToken) is { Count: > 0 } problems)
            {
                Record(storeId, state, asset, ActivityOutcome.Failed, $"The payout key isn't used: {string.Join(" ", problems)}");
                return;
            }
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the payout key's permissions from {connector.Name}: {e.Message}");
            return;
        }
        // The destination is named, so a trading key on the same exchange that can add or change destinations could
        // change where that name sends funds. Checked every run too.
        var trading = TradingKey(settings, connector);
        if (trading is not null && !await TradingKeyIsSafeAsync(storeId, state, asset, connector, trading, cancellationToken))
            return;

        // Withdrawals still in progress are remembered by exchange and asset (not by key, so a new key for the same account
        // still holds them back), and read back to the oldest of them.
        var list = new WithdrawalList(connector.Id, asset);
        var since = Since(state, list);
        IReadOnlyList<WithdrawalRecord> recent;
        try
        {
            recent = await connector.GetRecentWithdrawalsAsync(credentials, asset, since, cancellationToken);
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read recent {asset} withdrawals from {connector.Name}: {e.Message}");
            return;
        }
        var inProgress = Track(state, list, recent, since);
        if (!Reconcile(storeId, state, asset, connector, credentials, recent))
            return;

        var left = payout.DailyCap - state.WithdrawnOn(clock.GetUtcNow(), asset);
        if (left <= 0)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting, $"Today's cap of {Amount(payout.DailyCap)} {asset} is reached. Payout resumes tomorrow (UTC).");
            return;
        }

        decimal balance;
        try
        {
            balance = await connector.GetAvailableBalanceAsync(credentials, asset, cancellationToken);
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the {asset} balance from {connector.Name}: {e.Message}");
            return;
        }
        // A withdrawal still in progress may not have left the balance yet. Leaving its amount on the exchange means nothing
        // is ever paid out twice; if it had already left, this run withdraws that much less and a later run sends the rest.
        var available = balance - payout.KeepBalance - inProgress;
        var amount = Math.Min(available, left);
        if (amount <= 0 || amount < payout.Minimum)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting, available < payout.Minimum || available <= 0
                ? $"{Amount(Math.Max(available, 0))} {asset} can be withdrawn ({Amount(balance)} available, {Amount(payout.KeepBalance)} kept" +
                  (inProgress > 0 ? $", {Amount(inProgress)} in withdrawals still in progress" : "") +
                  $"). Waiting until at least {Amount(payout.Minimum)} {asset} can be withdrawn."
                : $"What today's cap still allows ({Amount(left)} {asset}) is below your minimum of {Amount(payout.Minimum)} {asset}. Payout resumes tomorrow (UTC).");
            return;
        }

        WithdrawalQuote quote;
        try
        {
            quote = await connector.QuoteAsync(credentials, asset, payout.Destination, amount, cancellationToken);
            // The exchange's limit is on what arrives; withdrawing the limit itself stays inside it once the fee is taken.
            if (quote.NetAmount > quote.Limit && quote.Limit > 0)
                quote = await connector.QuoteAsync(credentials, asset, payout.Destination, Math.Min(amount, quote.Limit), cancellationToken);
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed,
                $"Could not get a quote from {connector.Name} for withdrawing {Amount(amount)} {asset} to \"{payout.Destination}\": {e.Message}");
            return;
        }
        if (quote.Amount <= 0 || quote.Amount > amount || quote.Destination != payout.Destination)
        {
            Record(storeId, state, asset, ActivityOutcome.Failed,
                $"{connector.Name} quoted {Amount(quote.Amount)} {asset} to \"{quote.Destination}\" for a withdrawal of {Amount(amount)} {asset} to \"{payout.Destination}\". Nothing was withdrawn.");
            return;
        }
        if (quote.NetAmount > quote.Limit)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting,
                $"{connector.Name}'s {asset} withdrawal limit right now is {Amount(quote.Limit)}. Waiting for it to allow a withdrawal.");
            return;
        }
        if (quote.Amount < payout.Minimum)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting,
                $"{connector.Name} allows {Amount(quote.Amount)} {asset} right now, below your minimum of {Amount(payout.Minimum)} {asset}. Waiting.");
            return;
        }
        if (quote.NetAmount < quote.Minimum)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting,
                $"{Amount(quote.NetAmount)} {asset} would arrive, below {connector.Name}'s minimum withdrawal of {Amount(quote.Minimum)} {asset}. Waiting for more.");
            return;
        }
        if (quote.Fee > payout.MaxFee)
        {
            Record(storeId, state, asset, ActivityOutcome.Waiting,
                $"{connector.Name}'s fee for withdrawing {Amount(quote.Amount)} {asset} is {Amount(quote.Fee)}, above your highest fee of {Amount(payout.MaxFee)}. " +
                "Waiting; raise the highest fee if this fee is expected.");
            return;
        }

        var description = $"{Amount(quote.Amount)} {asset} to \"{payout.Destination}\" ({quote.Method}; {Amount(quote.NetAmount)} arrives after a {Amount(quote.Fee)} fee)";
        if (payout.DryRun)
        {
            Record(storeId, state, asset, ActivityOutcome.DryRun, $"Dry run: would withdraw {description}. {connector.Name} quoted it; nothing was withdrawn.");
            return;
        }

        // Saved before sending: if the response is lost or the server stops, the next run looks for it first.
        var pending = new PendingWithdrawal
        {
            Id = Guid.NewGuid(),
            ExchangeId = connector.Id,
            KeyFingerprint = credentials.Fingerprint(),
            Asset = asset,
            Destination = payout.Destination,
            Amount = quote.Amount,
            NetAmount = quote.NetAmount,
            Fee = quote.Fee
        };
        string reference;
        // Settings saves take the same lock, so a pause, a new destination or key saved during the run applies first.
        var gate = SettingsGate.For(storeId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var settingsNow = await repository.GetAsync(storeId);
            if (!StillApplies(settingsNow, await payouts.GetAsync(storeId), payout, connector, credentials))
            {
                Record(storeId, state, asset, ActivityOutcome.Waiting,
                    "The settings changed during the run (paused, payout or its options changed, or another key); nothing was withdrawn.");
                return;
            }
            // A trading key saved since the check above is checked too, before anything is sent.
            if (TradingKey(settingsNow, connector) is { } tradingNow && !tradingNow.Equals(trading) &&
                !await TradingKeyIsSafeAsync(storeId, state, asset, connector, tradingNow, cancellationToken))
                return;
            // Each run pays out what its store sees of the account; a second store paying out the same account wouldn't see
            // this one's withdrawals until they are listed, and could send them again. Checked under the lock against the
            // saved settings, so of two stores turned on together, the later run always sees the earlier one.
            if (await SharesPayoutKeyAsync(storeId, connector, credentials))
            {
                Record(storeId, state, asset, ActivityOutcome.Failed,
                    "Another store on this server pays out live with the same payout key. Each exchange account can be paid out by one store " +
                    "only, so give each store its own account and key. Nothing was withdrawn.");
                return;
            }
            // The balance and withdrawals are read again under the lock: a convert sell or another withdrawal of this asset
            // since they were first read must not eat into what is kept, and every withdrawal that could be confused with this
            // one is known.
            IReadOnlyList<WithdrawalRecord> before;
            decimal balanceNow;
            var sinceNow = Since(state, list);
            try
            {
                before = await connector.GetRecentWithdrawalsAsync(credentials, asset, sinceNow, cancellationToken);
                balanceNow = await connector.GetAvailableBalanceAsync(credentials, asset, cancellationToken);
            }
            catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
            {
                Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the {asset} balance and withdrawals from {connector.Name} before withdrawing: {e.Message}. Nothing was withdrawn.");
                return;
            }
            // Stamped right before sending: the withdrawal counts on this UTC day, and the cap is checked again for it, so a
            // run that crossed midnight can't spend the previous day's remainder.
            pending.SentAt = clock.GetUtcNow();
            pending.SeenBefore = recent.Concat(before).Where(r => r.RequestedAt >= pending.SentAt - MatchBefore - TimeSpan.FromMinutes(1))
                .Select(r => r.Reference).Distinct().ToList();
            var availableNow = balanceNow - payout.KeepBalance - Track(state, list, before, sinceNow);
            var leftNow = payout.DailyCap - state.WithdrawnOn(pending.SentAt, asset);
            if (quote.Amount > availableNow || quote.Amount > leftNow)
            {
                Record(storeId, state, asset, ActivityOutcome.Waiting, quote.Amount > leftNow
                    ? $"Today's cap (UTC) leaves {Amount(Math.Max(leftNow, 0))} {asset}, less than the {Amount(quote.Amount)} {asset} planned, as the day changed during the run. Nothing was withdrawn; the next run plans again."
                    : $"The {asset} balance changed during the run: {Amount(Math.Max(availableNow, 0))} {asset} can be withdrawn now, less than the {Amount(quote.Amount)} {asset} planned. Nothing was withdrawn; the next run plans again.");
                return;
            }
            state.Pending.Add(pending);
            await payouts.SaveStateAsync(storeId, state);
            try
            {
                reference = await connector.WithdrawAsync(credentials, asset, payout.Destination, quote.Amount, payout.MaxFee, cancellationToken);
            }
            catch (ExchangeException e) when (e.Rejected)
            {
                // Refused outright, so there is nothing to look for later.
                state.Pending.Remove(pending);
                Record(storeId, state, asset, ActivityOutcome.Failed, $"{connector.Name} refused withdrawing {description}: {e.Message}. Nothing was withdrawn.");
                return;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                Record(storeId, state, asset, ActivityOutcome.Failed,
                    $"Withdrawing {description} failed or got no answer: {e.Message}. It is never retried automatically: the next run looks for it in " +
                    $"{connector.Name}'s withdrawals, and nothing more is withdrawn until it is found or you remove it under Pending withdrawals.");
                return;
            }
        }
        finally
        {
            gate.Release();
        }
        // Accepted isn't done: an account that needs withdrawals approved returns a reference before the request is listed or
        // final. It stays pending, counted in full, until the exchange lists it, so nothing more is sent before then.
        pending.Reference = reference;
        state.Remember(reference);
        state.AddWithdrawn(pending.SentAt, asset, pending.Amount);
        Record(storeId, state, asset, ActivityOutcome.Sent,
            $"Withdrew {description} ({reference}). Payout continues once {connector.Name} lists it among its withdrawals.");
    }

    /// <summary>Looks for every pending withdrawal in the exchange's list. True when none is left, so payout may continue.</summary>
    bool Reconcile(string storeId, PayoutState state, string asset, IWithdrawalConnector connector, ExchangeCredentials credentials,
        IReadOnlyList<WithdrawalRecord> recent)
    {
        var fingerprint = credentials.Fingerprint();
        foreach (var pending in state.Pending.ToList())
        {
            var what = $"the earlier withdrawal of {Amount(pending.Amount)} {pending.Asset} to \"{pending.Destination}\" (sent {pending.SentAt:u})";
            if (pending.ExchangeId != connector.Id || pending.KeyFingerprint != fingerprint || pending.Asset != asset)
            {
                // Another account (or another asset's list) would never show it, and it would wrongly look lost.
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"{Capitalize(what)} was sent with a different payout key or asset, so it can't be looked up with these settings. Put them back, or " +
                    "check your exchange's withdrawals yourself and remove it under Pending withdrawals. Nothing is withdrawn until then.");
                return false;
            }
            if (pending.Reference is not null)
            {
                var listed = recent.FirstOrDefault(r => r.Reference == pending.Reference);
                if (listed is null)
                {
                    var hint = clock.GetUtcNow() - pending.SentAt < PendingGrace ? "Checking again on the next run." :
                        "If it needs approval on the exchange, approve or decline it there. If you've checked it is gone, remove it under Pending withdrawals.";
                    Record(storeId, state, pending.Asset, ActivityOutcome.Waiting,
                        $"{connector.Name} accepted {what} ({pending.Reference}) but doesn't list it yet. Nothing more is withdrawn until it does. {hint}");
                    continue;
                }
                state.Pending.Remove(pending);
                // Already counted when it was accepted, and left counted even if it failed: the cap is never exceeded.
                Record(storeId, state, pending.Asset, listed.Status == WithdrawalStatus.Failed ? ActivityOutcome.Failed : ActivityOutcome.Sent,
                    listed.Status == WithdrawalStatus.Failed
                        ? $"{connector.Name} lists {what} ({listed.Reference}) as failed or cancelled, so nothing left the account. It still counts against that day's cap. Payout resumes."
                        : $"{connector.Name} lists {what} ({listed.Reference}, {listed.Status}).");
                continue;
            }
            var matches = recent.Where(r => Matches(r, pending, state)).ToList();
            if (matches.Count == 1 && clock.GetUtcNow() < pending.SentAt + MatchAfter)
            {
                // Another withdrawal of the same amount and destination made after this one may be what is listed so far:
                // it is taken only once every withdrawal that could be it is listed.
                Record(storeId, state, pending.Asset, ActivityOutcome.Waiting,
                    $"{connector.Name} lists a withdrawal that may be {what} ({matches[0].Reference}). It is taken for it once no other withdrawal can " +
                    $"match, after {pending.SentAt + MatchAfter:u}. Nothing more is withdrawn until then.");
                continue;
            }
            if (matches.Count == 1)
            {
                var found = matches[0];
                state.Pending.Remove(pending);
                state.Remember(found.Reference);
                if (found.Status == WithdrawalStatus.Failed)
                {
                    Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                        $"Found {what} on {connector.Name} ({found.Reference}): it failed or was cancelled, so nothing left the account. Payout resumes.");
                }
                else
                {
                    // Counted on the day it was sent, in full: the cap is never exceeded.
                    state.AddWithdrawn(pending.SentAt, pending.Asset, pending.Amount);
                    Record(storeId, state, pending.Asset, ActivityOutcome.Sent, $"Found {what} on {connector.Name} ({found.Reference}, {found.Status}).");
                }
                continue;
            }
            if (matches.Count > 1)
            {
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"Several withdrawals on {connector.Name} ({string.Join(", ", matches.Select(m => m.Reference))}) match {what}, so which one it is isn't clear. " +
                    "Check them in your exchange, then remove it under Pending withdrawals. Nothing is withdrawn until then.");
                continue;
            }
            if (clock.GetUtcNow() - pending.SentAt < PendingGrace)
            {
                Record(storeId, state, pending.Asset, ActivityOutcome.Waiting, $"{Capitalize(what)} isn't listed on {connector.Name} yet. Checking again on the next run.");
                continue;
            }
            Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                $"{connector.Name} lists no withdrawal matching {what}. It is never retried automatically: check your exchange's withdrawals, " +
                "then remove it under Pending withdrawals. Nothing is withdrawn until then.");
        }
        return state.Pending.Count == 0;
    }

    /// <summary>
    /// Whether <paramref name="record"/> can be the lost <paramref name="pending"/> withdrawal: not one already accounted for,
    /// to the same destination when the exchange names it, requested around when it was sent, for its amount (exchanges
    /// report either what left the account or what arrived).
    /// </summary>
    public static bool Matches(WithdrawalRecord record, PendingWithdrawal pending, PayoutState state) =>
        !state.Known.Contains(record.Reference) && !pending.SeenBefore.Contains(record.Reference) &&
        (record.Destination is null || record.Destination == pending.Destination) &&
        record.RequestedAt >= pending.SentAt - MatchBefore && record.RequestedAt <= pending.SentAt + MatchAfter &&
        (record.Amount == pending.Amount || record.Amount == pending.NetAmount || record.Amount + record.Fee == pending.Amount);

    /// <summary>
    /// Drops a pending withdrawal the owner has checked on the exchange themselves. One whose response was lost counts in
    /// full against today's cap; one the exchange accepted was counted then. Waits for a running payout job of the store.
    /// False when there is no such withdrawal.
    /// </summary>
    public Task<bool> RemovePendingAsync(string storeId, Guid id, CancellationToken cancellationToken) =>
        WithStoreLockAsync(storeId, async () =>
        {
            var state = await payouts.GetStateAsync(storeId);
            var pending = state.Pending.FirstOrDefault(p => p.Id == id);
            if (pending is null)
                return false;
            state.Pending.Remove(pending);
            var what = $"The pending withdrawal of {Amount(pending.Amount)} {pending.Asset} to \"{pending.Destination}\" (sent {pending.SentAt:u})";
            if (pending.Reference is not null)
            {
                // Counted when the exchange accepted it.
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"{what} ({pending.Reference}) was removed by hand. It already counts against the cap of the day it was sent. Payout resumes.");
            }
            else
            {
                // Whether it went through isn't known here, so it counts in full (today, the day of removal): the cap is never exceeded.
                state.AddWithdrawn(clock.GetUtcNow(), pending.Asset, pending.Amount);
                Record(storeId, state, pending.Asset, ActivityOutcome.Failed,
                    $"{what} was removed by hand. Its full amount counts against today's cap, since whether it went through isn't known. Payout resumes.");
            }
            await payouts.SaveStateAsync(storeId, state);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Saves payout settings with the record of what changed in one step, under the store's payout lock and then its settings
    /// lock (the order a run takes them in). <paramref name="decide"/> gets the saved settings and returns the new ones, or
    /// why they can't be saved. The change is written to the activity before the settings, and neither write can be
    /// cancelled once started, so a change of where funds go is never in force without its record.
    /// </summary>
    public async Task<(PayoutSettings Saved, PayoutSettings Updated, string? Error)> SaveSettingsAsync(string storeId,
        Func<PayoutSettings, Task<(PayoutSettings Next, string? Error)>> decide, CancellationToken cancellationToken) =>
        await WithStoreLockAsync(storeId, async () =>
        {
            var gate = SettingsGate.For(storeId);
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = await payouts.GetAsync(storeId);
                var (next, error) = await decide(current);
                if (error is not null)
                    return (current, next, error);
                if (Changes(current, next) is { Count: > 0 } changes)
                {
                    var state = await payouts.GetStateAsync(storeId);
                    Record(storeId, state, next.Asset, ActivityOutcome.Changed, "Payout settings changed: " + string.Join("; ", changes) + ".");
                    await payouts.SaveStateAsync(storeId, state);
                }
                await payouts.SaveAsync(storeId, next);
                return (current, next, (string?)null);
            }
            finally
            {
                gate.Release();
            }
        }, cancellationToken);

    /// <summary>What a save changes about where funds go, for the activity. Never includes the key itself.</summary>
    static List<string> Changes(PayoutSettings saved, PayoutSettings updated)
    {
        var changes = new List<string>();
        if (saved.ExchangeId != updated.ExchangeId)
            changes.Add($"exchange from {saved.ExchangeId ?? "none"} to {updated.ExchangeId ?? "none"}");
        if (saved.ProtectedApiKey != updated.ProtectedApiKey || saved.ProtectedApiSecret != updated.ProtectedApiSecret)
            changes.Add(updated.HasCredentials ? "a new payout key was saved" : "the payout key was removed");
        if (saved.Asset != updated.Asset)
            changes.Add($"asset from {Shown(saved.Asset)} to {Shown(updated.Asset)}");
        if (saved.Destination != updated.Destination)
            changes.Add($"destination from \"{saved.Destination}\" to \"{updated.Destination}\"");
        return changes;
    }

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

    /// <summary>Whether another store pays out live (enabled, not a dry run) with the same exchange and key.</summary>
    async Task<bool> SharesPayoutKeyAsync(string storeId, IWithdrawalConnector connector, ExchangeCredentials credentials) =>
        (await payouts.GetAllAsync()).Any(p => p.Key != storeId && p.Value is { Enabled: true, DryRun: false } && p.Value.ExchangeId == connector.Id &&
                                               protector.Credentials(p.Value)?.ApiKey == credentials.ApiKey);

    /// <summary>The store's trading key when it is on the payout exchange, where it could change the saved destinations.</summary>
    ExchangeCredentials? TradingKey(SweepSellSettings settings, IWithdrawalConnector connector) =>
        settings.ExchangeId == connector.Id ? protector.Credentials(settings) : null;

    /// <summary>Whether <paramref name="trading"/> can't change withdrawal destinations; records why not when it can, or can't be checked.</summary>
    async Task<bool> TradingKeyIsSafeAsync(string storeId, PayoutState state, string asset, IWithdrawalConnector connector, ExchangeCredentials trading,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await connector.GetOtherKeyProblemsAsync(trading, cancellationToken) is { Count: > 0 } problems)
            {
                Record(storeId, state, asset, ActivityOutcome.Failed,
                    $"The store's trading key makes payouts unsafe: {string.Join(" ", problems)} Remove Add and Update Withdrawal Addresses from the trading key. Nothing was withdrawn.");
                return false;
            }
            return true;
        }
        catch (Exception e) when (IsRecordedFailure(e, cancellationToken))
        {
            Record(storeId, state, asset, ActivityOutcome.Failed, $"Could not read the trading key's permissions from {connector.Name}: {e.Message}. Nothing was withdrawn.");
            return false;
        }
    }

    /// <summary>
    /// How far back withdrawals are read: <see cref="History"/>, or further to the oldest one still unsettled, so a
    /// withdrawal on hold for longer stays held back from the balance.
    /// </summary>
    DateTimeOffset Since(PayoutState state, WithdrawalList list)
    {
        var since = clock.GetUtcNow() - History;
        return state.Unsettled.Where(list.Holds).Select(u => u.RequestedAt - MatchBefore).Append(since).Min();
    }

    /// <summary>
    /// Notes which of <paramref name="list"/>'s withdrawals, as read back to <paramref name="since"/>, are still in progress
    /// and which have settled, and returns the amount of all its unsettled ones, fees included. One the read covers but
    /// doesn't list is in no account the key can see (a key for another account was saved), so it no longer holds anything
    /// back here. Other exchanges' and assets' entries are kept for when they are used again.
    /// </summary>
    static decimal Track(PayoutState state, WithdrawalList list, IReadOnlyList<WithdrawalRecord> listed, DateTimeOffset since)
    {
        state.Unsettled.RemoveAll(u => list.Holds(u) && u.RequestedAt >= since && listed.All(r => r.Reference != u.Reference));
        foreach (var record in listed)
        {
            state.Unsettled.RemoveAll(u => list.Holds(u) && u.Reference == record.Reference);
            if (record.Status == WithdrawalStatus.InProgress)
                state.Unsettled.Add(new UnsettledWithdrawal
                {
                    ExchangeId = list.ExchangeId, Asset = list.Asset, Reference = record.Reference, RequestedAt = record.RequestedAt,
                    Amount = record.Amount + record.Fee
                });
        }
        return state.Unsettled.Where(list.Holds).Sum(u => u.Amount);
    }

    /// <summary>The exchange and asset whose withdrawals a run reads.</summary>
    sealed record WithdrawalList(string ExchangeId, string Asset)
    {
        public bool Holds(UnsettledWithdrawal u) => u.ExchangeId == ExchangeId && u.Asset == Asset;
    }

    /// <summary>Whether the settings saved now still allow the live withdrawal a run planned with <paramref name="planned"/>.</summary>
    bool StillApplies(SweepSellSettings settings, PayoutSettings now, PayoutSettings planned, IWithdrawalConnector connector, ExchangeCredentials credentials) =>
        !settings.Paused && !IsTradingKey(settings, credentials) &&
        now.Enabled && !now.DryRun && now.ExchangeId == connector.Id &&
        credentials.Equals(protector.Credentials(now)) && // the whole key: a replaced secret must not be used either
        now.Asset == planned.Asset && now.Destination == planned.Destination && now.Minimum == planned.Minimum &&
        now.KeepBalance == planned.KeepBalance && now.DailyCap == planned.DailyCap && now.MaxFee == planned.MaxFee;

    /// <summary>Whether <paramref name="payoutKey"/> is the store's trading key: payout must have its own.</summary>
    public bool IsTradingKey(SweepSellSettings settings, ExchangeCredentials payoutKey) =>
        protector.Credentials(settings) is { } trading && trading.ApiKey == payoutKey.ApiKey;

    static SemaphoreSlim StoreLock(string storeId) => StoreLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));

    void Record(string storeId, PayoutState state, string? asset, ActivityOutcome outcome, string message)
    {
        var level = outcome switch
        {
            ActivityOutcome.Failed => LogLevel.Warning,
            ActivityOutcome.Waiting => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, "Sweep & Sell payout, store {StoreId}, asset {Asset}: {Outcome}: {Message}", storeId, asset ?? "-", outcome, message);
        state.Add(new ActivityEntry { At = clock.GetUtcNow(), Asset = string.IsNullOrEmpty(asset) ? null : asset, Outcome = outcome, Message = message });
    }

    static bool IsRecordedFailure(Exception e, CancellationToken cancellationToken) =>
        e is ExchangeException or HttpRequestException or ArithmeticException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    static string Shown(string asset) => string.IsNullOrEmpty(asset) ? "none" : asset;

    static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
